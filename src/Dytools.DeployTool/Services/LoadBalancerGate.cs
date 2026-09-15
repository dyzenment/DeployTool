using System.Diagnostics;
using System.Text;
using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Services;

/// <summary>
/// Runs the drain and restore phases that bracket a deploy on a load-balanced box.
///
/// A phase is three optional parts in order: notify (change rotation state), verify (confirm
/// it took), wait (let the balancer notice). Anything not configured is skipped, so a phase
/// carrying only waitSeconds is a plain pause and a phase carrying only a notify is
/// fire-and-forget. That is deliberate: the block should be adoptable one piece at a time.
///
/// What verification can and cannot prove is worth being precise about. Polling localhost
/// confirms this instance's health flag flipped. It does not confirm the balancer has acted
/// on it - that would need the balancer's own API, or a response that identifies which node
/// answered a VIP request. So the wait is not redundant with the verify; it covers the part
/// verification cannot reach.
/// </summary>
public static class LoadBalancerGate
{
    /// <summary>
    /// Decides whether the restore phase should run after a deploy attempt.
    ///
    /// Restore only when what is live is known to be good:
    ///   • the deploy succeeded, or
    ///   • it failed but rolled back to the previous build, or
    ///   • it failed before touching what was live at all (blue-green that never flipped,
    ///     or a classic deploy that died before the mirror started).
    ///
    /// Otherwise the instance stays drained. A stranded instance is visible and fixable; one
    /// returned to rotation serving a half-written folder is neither. Note that a failed
    /// classic deploy with rollback disabled falls in that last category by design - mirroring
    /// deletes files that are not in the artifact, so an interrupted mirror leaves a folder
    /// that is neither the old build nor the new one.
    /// </summary>
    public static bool ShouldRestore(TargetDeployResult result, bool liveModified)
        => result.Success || result.RolledBack || !liveModified;

    /// <summary>
    /// Runs one phase, appending a StepResult for every part that actually executed so the
    /// report shows exactly how long the box spent out of rotation and why.
    /// Returns false when a configured notify or verification failed.
    /// </summary>
    public static async Task<bool> RunPhaseAsync(
        LoadBalancerPhase? phase, string phaseName, TokenContext tokens, List<StepResult> steps)
    {
        if (phase is null) return true;

        if (phase.Notify is not null)
        {
            var notifyStep = await RunNotifyAsync(phase.Notify, phaseName, tokens);
            steps.Add(notifyStep);
            if (!notifyStep.Success)
            {
                // Fail closed. Continuing past a failed drain would stop the site while the
                // balancer still believes this instance is healthy - live traffic dropped by
                // the very step meant to prevent it.
                Console.WriteLine($"  [LB] ✗ {phaseName} notification failed - not proceeding.");
                return false;
            }
        }

        if (phase is { VerifyUrl: not null, ExpectStatus: not null })
        {
            var verifyStep = await VerifyAsync(phase, phaseName, tokens);
            steps.Add(verifyStep);
            if (!verifyStep.Success) return false;
        }

        if (phase.WaitSeconds > 0)
            steps.Add(await WaitAsync(phase.WaitSeconds, phaseName));

        return true;
    }

    // -- Notify ----------------------------------------------------------------

    private static Task<StepResult> RunNotifyAsync(NotifyHook hook, string phaseName, TokenContext tokens)
        => hook.Type switch
        {
            NotifyHookType.Http    => HttpNotifyAsync(hook, phaseName, tokens),
            NotifyHookType.File    => Task.FromResult(FileNotify(hook, phaseName, tokens)),
            NotifyHookType.Command => CommandNotifyAsync(hook, phaseName, tokens),
            _                      => throw new InvalidOperationException($"Unknown notify type '{hook.Type}'.")
        };

    private static async Task<StepResult> HttpNotifyAsync(NotifyHook hook, string phaseName, TokenContext tokens)
    {
        var url = tokens.Resolve(hook.Url);
        if (string.IsNullOrWhiteSpace(url))
            return ProcessRunner.Synthetic($"{phaseName} notify", "type is 'http' but no url was set.", success: false);

        var result = new StepResult
        {
            StepName  = $"{phaseName} notify",
            Command   = $"{hook.Method.ToUpperInvariant()} {url}",
            StartedAt = DateTimeOffset.Now
        };

        Console.WriteLine($"  ┌- [LB] {phaseName} notify");
        Console.WriteLine($"  │  {hook.Method.ToUpperInvariant()} {url}");

        try
        {
            using var http = ProbeHttp.Create(TimeSpan.FromSeconds(hook.TimeoutSeconds));
            using var request = new HttpRequestMessage(new HttpMethod(hook.Method.ToUpperInvariant()), url);

            if (hook.Headers is not null)
                foreach (var (name, value) in hook.Headers)
                    request.Headers.TryAddWithoutValidation(name, tokens.Resolve(value));

            // Resolve returns its input unchanged when null or empty, so a non-empty body
            // cannot come back null here.
            if (!string.IsNullOrEmpty(hook.Body))
                request.Content = new StringContent(tokens.Resolve(hook.Body)!, Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request);
            var status = (int)response.StatusCode;

            result.ExitCode = status;
            result.Success  = hook.ExpectStatus is null ? response.IsSuccessStatusCode : status == hook.ExpectStatus;
            result.Stdout   = $"HTTP {status} {response.ReasonPhrase}";
        }
        catch (Exception ex)
        {
            result.ExitCode = -1;
            result.Success  = false;
            result.Stderr   = ProbeHttp.Describe(ex);
        }

        result.CompletedAt = DateTimeOffset.Now;
        Console.WriteLine($"  └- {(result.Success ? "✓" : "✗")} {result.Stdout}{result.Stderr}");
        return result;
    }

    private static StepResult FileNotify(NotifyHook hook, string phaseName, TokenContext tokens)
    {
        var path = FileHelper.ExpandEnvVars(tokens.Resolve(hook.Path));
        if (string.IsNullOrWhiteSpace(path))
            return ProcessRunner.Synthetic($"{phaseName} notify", "type is 'file' but no path was set.", success: false);

        try
        {
            if (hook.Action == FileHookAction.Create)
            {
                var parent = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(path, string.Empty);
                return ProcessRunner.Synthetic($"{phaseName} notify", $"Created {path}");
            }

            // Deleting an already-absent marker is the desired end state, not a failure.
            if (File.Exists(path)) File.Delete(path);
            return ProcessRunner.Synthetic($"{phaseName} notify", $"Deleted {path}");
        }
        catch (Exception ex)
        {
            return ProcessRunner.Synthetic($"{phaseName} notify", $"{path}: {ex.Message}", success: false);
        }
    }

    private static Task<StepResult> CommandNotifyAsync(NotifyHook hook, string phaseName, TokenContext tokens)
    {
        var exe = FileHelper.ExpandEnvVars(tokens.Resolve(hook.Executable));
        if (string.IsNullOrWhiteSpace(exe))
            return Task.FromResult(ProcessRunner.Synthetic(
                $"{phaseName} notify", "type is 'command' but no executable was set.", success: false));

        return ProcessRunner.RunAsync($"{phaseName} notify", exe, tokens.Resolve(hook.Arguments) ?? string.Empty);
    }

    // -- Precheck --------------------------------------------------------------

    /// <summary>
    /// Answers "is this instance actually in rotation?" before anything else happens.
    ///
    /// Returns false - skip the whole load-balancer routine - when the endpoint does not
    /// answer with the configured live status, including when it cannot be reached at all.
    /// A stopped site refuses connections, and that is precisely the offline-box case this
    /// exists to handle, so an unreachable endpoint is an answer rather than an error.
    ///
    /// A failed TLS handshake is the exception to that. Something is listening, we just cannot
    /// speak to it, so it is not evidence of anything - and guessing "offline" there stops a live
    /// site without draining it. That case assumes in rotation instead.
    ///
    /// Never reports failure: "not in rotation" is a routing decision, not a broken deploy.
    /// </summary>
    public static async Task<(bool InRotation, StepResult Step)> PrecheckAsync(
        LoadBalancerPrecheck precheck, TokenContext tokens)
    {
        var url = tokens.Resolve(precheck.Url);
        var started = DateTimeOffset.Now;

        if (string.IsNullOrWhiteSpace(url))
        {
            // Misconfigured rather than offline. Assume in rotation, because skipping the
            // drain on a live box is the dangerous direction to guess wrong in.
            return (true, ProcessRunner.Synthetic(
                "lb precheck", "No precheck url set - assuming this instance is in rotation."));
        }

        Console.WriteLine($"  ┌- [LB] precheck");
        Console.WriteLine($"  │  GET {url}, in rotation iff HTTP {precheck.LiveStatus}");

        var poll = await PollAsync(url, precheck.LiveStatus, precheck.TimeoutSeconds, precheck.IntervalSeconds);

        // A handshake that never completed tells us nothing about rotation state - only that the
        // probe itself is broken. Reading that as "offline" is how a live box gets its app pool
        // bounced without a drain, so it falls back to the same assumption a missing url does.
        var inRotation = poll.Matched || poll.HandshakeFailed;

        var summary = poll.Matched
            ? $"HTTP {precheck.LiveStatus} - in rotation, running drain/restore."
            : poll.HandshakeFailed
                ? $"Could not complete a TLS handshake in {poll.Elapsed.TotalSeconds:F1}s " +
                  $"({poll.Attempts} attempts, last: {poll.Last}) - rotation state unknown, " +
                  "assuming in rotation and running drain/restore."
                : $"Never returned HTTP {precheck.LiveStatus} in {poll.Elapsed.TotalSeconds:F1}s " +
                  $"({poll.Attempts} attempts, last: {poll.Last}) - already out of rotation, " +
                  "skipping drain/restore.";

        Console.WriteLine($"  └- {(poll.Matched ? "✓" : poll.HandshakeFailed ? "!" : "•")} {summary}");

        return (inRotation, new StepResult
        {
            StepName    = "lb precheck",
            Command     = $"GET {url} == {precheck.LiveStatus}",
            ExitCode    = 0,
            Success     = true,
            Stdout      = summary,
            StartedAt   = started,
            CompletedAt = DateTimeOffset.Now
        });
    }

    // -- Verify ----------------------------------------------------------------

    private static async Task<StepResult> VerifyAsync(LoadBalancerPhase phase, string phaseName, TokenContext tokens)
    {
        var url      = tokens.Resolve(phase.VerifyUrl)!;
        var expected = phase.ExpectStatus!.Value;
        var started  = DateTimeOffset.Now;

        Console.WriteLine($"  ┌- [LB] {phaseName} verify");
        Console.WriteLine($"  │  GET {url} until HTTP {expected} (up to {phase.VerifyTimeoutSeconds}s)");

        var poll = await PollAsync(url, expected, phase.VerifyTimeoutSeconds, phase.VerifyIntervalSeconds);

        var result = new StepResult
        {
            StepName    = $"{phaseName} verify",
            Command     = $"GET {url} == {expected}",
            ExitCode    = poll.Matched ? expected : -1,
            Success     = poll.Matched,
            Stdout      = poll.Matched
                ? $"HTTP {expected} after {poll.Attempts} attempt(s), {poll.Elapsed.TotalSeconds:F1}s"
                : string.Empty,
            Stderr      = poll.Matched
                ? string.Empty
                : $"Never returned HTTP {expected} within {phase.VerifyTimeoutSeconds}s " +
                  $"({poll.Attempts} attempts, last: {poll.Last})",
            StartedAt   = started,
            CompletedAt = DateTimeOffset.Now
        };

        Console.WriteLine($"  └- {(result.Success ? "✓" : "✗")} {result.Stdout}{result.Stderr}");
        return result;
    }

    // -- Polling ---------------------------------------------------------------

    private readonly record struct PollOutcome(
        bool Matched, int Attempts, TimeSpan Elapsed, string Last, bool HandshakeFailed);

    /// <summary>
    /// Polls a URL until it answers with the expected status or the timeout expires. Shared by
    /// precheck and verify so the two cannot drift; the callers decide what a miss means, since
    /// it is a failure for one and a routing decision for the other.
    /// </summary>
    private static async Task<PollOutcome> PollAsync(string url, int expected, int timeoutSeconds, int intervalSeconds)
    {
        var elapsed   = Stopwatch.StartNew();
        var interval  = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        var timeout   = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));
        var last      = "no response";
        var attempts  = 0;
        var handshake = false;

        using var http = ProbeHttp.Create(interval);

        while (true)
        {
            attempts++;
            try
            {
                using var response = await http.GetAsync(url);
                var status = (int)response.StatusCode;
                last = $"HTTP {status}";

                handshake = false;

                if (status == expected)
                    return new PollOutcome(true, attempts, elapsed.Elapsed, last, false);
            }
            catch (Exception ex)
            {
                // A refused connection is a legitimate answer while a site is stopped, so keep
                // polling rather than treating the first exception as final.
                last      = ProbeHttp.Describe(ex);
                handshake = ProbeHttp.IsHandshakeFailure(ex);
            }

            // Checked after an attempt, not before, so a zero or tiny timeout still asks once.
            if (elapsed.Elapsed >= timeout)
                return new PollOutcome(false, attempts, elapsed.Elapsed, last, handshake);

            await Task.Delay(interval);
        }
    }

    // -- Wait ------------------------------------------------------------------

    private static async Task<StepResult> WaitAsync(int seconds, string phaseName)
    {
        Console.WriteLine($"  [LB] {phaseName} wait {seconds}s...");
        var started = DateTimeOffset.Now;
        await Task.Delay(TimeSpan.FromSeconds(seconds));

        return new StepResult
        {
            StepName    = $"{phaseName} wait",
            Command     = $"wait {seconds}s",
            ExitCode    = 0,
            Success     = true,
            Stdout      = $"Waited {seconds}s",
            StartedAt   = started,
            CompletedAt = DateTimeOffset.Now
        };
    }
}

/// <summary>
/// Substitutes {hostname}, {site} and {project} into hook strings. Built on the box that runs
/// the step - a peer receives the same config as the primary and must resolve its own values,
/// which is also why %ENV_VAR% expansion happens here rather than at plan time.
/// </summary>
public sealed class TokenContext
{
    private readonly (string Token, string Value)[] _tokens;

    public TokenContext(string? site, string project)
        => _tokens =
        [
            ("{hostname}", Environment.MachineName),
            ("{site}",     site ?? string.Empty),
            ("{project}",  project)
        ];

    public string? Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        foreach (var (token, replacement) in _tokens)
            value = value.Replace(token, replacement, StringComparison.OrdinalIgnoreCase);

        return value;
    }
}
