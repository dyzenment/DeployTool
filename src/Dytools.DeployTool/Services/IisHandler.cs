using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Services;

public sealed class IisHandler : IDeployTypeHandler
{
    public DeployType SupportedType => DeployType.Iis;

    /// <summary>An IIS site lives on a specific box, so every server gets its own step.</summary>
    public DeployScope GetScope(TargetConfig target) => DeployScope.Server;

    /// <summary>How long to wait for the warmup request before calling the slot bad.</summary>
    private const int WarmupTimeoutSeconds = 60;

    private static readonly string AppcmdPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "inetsrv", "appcmd.exe");

    public async Task<TargetDeployResult> ApplyAsync(ApplyStep step)
    {
        var iis = step.Iis
            ?? throw new InvalidOperationException(
                $"Project '{step.Project}': type is 'iis' but the 'iis' block is missing.");

        var blueGreen = !string.IsNullOrWhiteSpace(iis.SecondaryDeployPath);
        var tokens    = new TokenContext(iis.SiteName, step.Project);
        var lb        = iis.LoadBalancer;

        // Ask whether this account can drive IIS at all, before anything is stopped or drained.
        //
        // Without this the wasted path is long and visible to users: precheck, drain, fail on the
        // first appcmd, restore, hand off to the agent, and the agent then repeats the whole
        // routine. The site is taken out of rotation and put back having had nothing done to it.
        //
        // The probe cannot promise the deploy will work - a pool can be readable and still refuse
        // to stop - so the existing drain/fail/restore/hand-off path stays exactly as it was for
        // everything it does not catch. All this does is catch the case that is knowable up front.
        var accessDenied = await ProbeControlAccessAsync(iis, step);
        if (accessDenied is not null) return accessDenied;

        // Ask next whether this instance is in rotation at all. A box that is already out of
        // it - stopped site, maintenance, a flag flipped by hand - takes the plain routine:
        // draining something already drained is pointless, and on an offline box the drain
        // notification cannot even be delivered, which would abort the deploy outright.
        StepResult? precheckStep = null;
        if (lb?.Precheck is not null)
        {
            var (inRotation, stepResult) = await LoadBalancerGate.PrecheckAsync(lb.Precheck, tokens);
            precheckStep = stepResult;
            if (!inRotation) lb = null;
        }

        if (lb is null)
        {
            var (plain, _) = blueGreen
                ? await ApplyBlueGreenAsync(step, iis)
                : await ApplyClassicAsync(step, iis);

            if (precheckStep is not null) plain.DeploySteps.Insert(0, precheckStep);
            return plain;
        }

        var result = new TargetDeployResult { TargetLabel = step.Label, Type = DeployType.Iis };
        if (precheckStep is not null) result.DeploySteps.Add(precheckStep);

        // Drain first, and treat a failure here as fatal before anything is stopped. The site
        // is still up and serving at this point, so aborting costs a deploy; continuing would
        // cost live requests.
        if (!await LoadBalancerGate.RunPhaseAsync(lb.Drain, "drain", tokens, result.DeploySteps))
        {
            result.ErrorMessage = "Drain phase failed - the site was left untouched and in rotation.";
            Console.WriteLine($"  [IIS] ✗ {result.ErrorMessage}");
            return result;
        }

        var (inner, liveModified) = blueGreen
            ? await ApplyBlueGreenAsync(step, iis)
            : await ApplyClassicAsync(step, iis);

        // Carry the inner attempt's outcome onto the result that already holds the drain steps.
        result.TargetLabel = inner.TargetLabel;
        result.Success     = inner.Success;
        result.RolledBack  = inner.RolledBack;
        result.ErrorMessage = inner.ErrorMessage;
        result.DeploySteps.AddRange(inner.DeploySteps);

        if (LoadBalancerGate.ShouldRestore(inner, liveModified))
        {
            if (!await LoadBalancerGate.RunPhaseAsync(lb.Restore, "restore", tokens, result.DeploySteps))
            {
                result.Success = false;
                result.ErrorMessage = (result.ErrorMessage is null ? "" : result.ErrorMessage + " ") +
                    "Restore phase failed - this instance is still out of rotation.";
                Console.WriteLine($"  [IIS] ✗ {result.ErrorMessage}");
            }
        }
        else
        {
            // Deliberately left drained: what is live was modified and never verified good.
            result.DeploySteps.Add(ProcessRunner.Synthetic(
                "restore skipped",
                "Deploy failed with live files modified and no rollback - instance left OUT of rotation.",
                success: false));
            Console.WriteLine("  [IIS] ✗ Instance left OUT of rotation - deploy failed and nothing restored it.");
        }

        return result;
    }

    // -- Blue-green: mirror into the idle slot, then flip -----------------------

    /// <summary>
    /// The copy lands in the slot that is not serving traffic, so the app pool is never
    /// stopped while files move - that removes the entire file-copy outage. All that
    /// remains is the app domain restart on flip.
    ///
    /// Rollback is inherent: the previously-live slot is left untouched and still holds
    /// the last-known-good build, so recovery is one more flip.
    /// </summary>
    /// <returns>
    /// The attempt's result, and whether what was live got modified. The second value drives
    /// the load-balancer restore decision: a run that failed before the flip left the previous
    /// build serving untouched, so the instance is safe to put back in rotation.
    /// </returns>
    private async Task<(TargetDeployResult Result, bool LiveModified)> ApplyBlueGreenAsync(
        ApplyStep step, IisConfig iis)
    {
        var result = new TargetDeployResult { TargetLabel = step.Label, Type = DeployType.Iis };

        if (string.IsNullOrWhiteSpace(iis.SiteName))
        {
            result.ErrorMessage =
                $"Project '{step.Project}': 'secondaryDeployPath' is set (blue-green) but 'siteName' " +
                "is missing - the site is what gets flipped between slots.";
            Console.WriteLine($"  [IIS] ✗ {result.ErrorMessage}");
            return (result, false);
        }

        // Expanded here, on the box that owns the paths - never at plan time.
        var slotA = FileHelper.ExpandEnvVars(iis.DeployPath);
        var slotB = FileHelper.ExpandEnvVars(iis.SecondaryDeployPath);

        string? previousPath = null;
        var     flipped      = false;

        try
        {
            // -- 1. Which slot is live? No state file - ask IIS. ---------------
            var activeStep = await AppCmdAsync("Resolve active slot",
                $"list vdir \"{iis.SiteName}/\" /text:physicalPath");
            result.DeploySteps.Add(activeStep);
            if (!activeStep.Success)
                throw new DeployException(
                    $"Could not read the current physicalPath for site '{iis.SiteName}'.");

            var activePath = activeStep.Stdout.Trim();
            var idlePath   = ResolveIdleSlot(activePath, slotA, slotB);
            previousPath   = activePath;

            Console.WriteLine($"  [IIS] active={activePath} -> deploying to idle={idlePath}");
            result.TargetLabel = $"IIS / {iis.SiteName} → {SlotName(idlePath)}";
            result.DeploySteps.Add(ProcessRunner.Synthetic("Resolved slots",
                $"active={activePath}, idle={idlePath}"));

            // -- 2. Mirror into the idle slot - zero outage, nothing serves it -
            Directory.CreateDirectory(idlePath);
            StepResult copyResult;
            try
            {
                FileHelper.MirrorDirectory(step.Artifact, idlePath);
                copyResult = ProcessRunner.Synthetic("Mirror files to idle slot",
                    $"{step.Artifact} -> {idlePath}");
            }
            catch (Exception ex)
            {
                copyResult = ProcessRunner.Synthetic("Mirror files to idle slot", ex.Message, success: false);
            }
            result.DeploySteps.Add(copyResult);
            if (!copyResult.Success) throw new DeployException("File copy to the idle slot failed.");

            // -- 3. Flip ------------------------------------------------------
            var flipStep = await AppCmdAsync("Flip site physicalPath",
                $"set vdir \"{iis.SiteName}/\" /physicalPath:\"{idlePath}\"");
            result.DeploySteps.Add(flipStep);
            if (!flipStep.Success)
                throw new DeployException($"Failed to flip site '{iis.SiteName}' to '{idlePath}'.");
            flipped = true;

            // -- 4. Recycle ---------------------------------------------------
            // The physicalPath change already restarts the app domain; this is belt-and-braces.
            var recycleStep = await AppCmdAsync("Recycle app pool",
                $"recycle apppool /apppool.name:\"{iis.AppPool}\"");
            result.DeploySteps.Add(recycleStep);
            if (!recycleStep.Success)
                throw new DeployException($"Failed to recycle app pool '{iis.AppPool}'.");

            // -- 5. Warm ------------------------------------------------------
            // Cold start lands on this request instead of the first real user's.
            if (!string.IsNullOrWhiteSpace(iis.WarmupUrl))
            {
                var warmStep = await WarmupAsync(iis.WarmupUrl);
                result.DeploySteps.Add(warmStep);
                if (!warmStep.Success)
                    throw new DeployException(
                        $"Warmup failed for '{iis.WarmupUrl}' - the new slot is not healthy.");
            }

            result.Success = true;
        }
        catch (DeployException ex)
        {
            result.ErrorMessage = ex.Message;
            Console.WriteLine($"  [IIS] ✗ {ex.Message}");

            // Always flip back if we already flipped - leaving a known-bad slot live is
            // strictly worse than returning to the last-known-good one. Not gated on the
            // rollback flag: the previous slot is still intact either way, so this costs
            // one appcmd call and no data.
            if (flipped && previousPath is not null)
            {
                Console.WriteLine($"  [IIS] Flipping back to {previousPath}...");
                var back = await AppCmdAsync("Flip back to previous slot",
                    $"set vdir \"{iis.SiteName}/\" /physicalPath:\"{previousPath}\"");
                result.DeploySteps.Add(back);
                result.DeploySteps.Add(await AppCmdAsync("Recycle app pool (flip back)",
                    $"recycle apppool /apppool.name:\"{iis.AppPool}\""));
                result.RolledBack = back.Success;
            }
            // If we never flipped, the live slot was never touched - nothing to undo.
        }

        return (result, flipped);
    }

    // -- Classic: stop pool, mirror in place, start pool ------------------------

    /// <returns>
    /// The attempt's result, and whether the live folder got modified. Mirroring deletes files
    /// that are not in the artifact, so once it starts the folder is neither the old build nor
    /// the new one until it finishes - which is what the restore decision turns on.
    /// </returns>
    private async Task<(TargetDeployResult Result, bool LiveModified)> ApplyClassicAsync(
        ApplyStep step, IisConfig iis)
    {
        // Expanded here, on the box that owns the path - %ProgramData% and friends can
        // differ between the primary and a peer.
        var deployPath = FileHelper.ExpandEnvVars(iis.DeployPath);
        var result     = new TargetDeployResult { TargetLabel = step.Label, Type = DeployType.Iis };

        string? backupDir    = null;
        var     liveModified = false;
        var     siteStopped  = false;

        // 1062 is "the service has not been started" - stopping something already stopped is
        // the desired end state, not a failure.
        static bool StopSucceeded(int code) => code is 0 or 1062;

        try
        {
            // Site before pool: closing the bindings stops new connections arriving, then
            // stopping the pool tears down the worker that is finishing the rest.
            if (iis.StopSite)
            {
                if (string.IsNullOrWhiteSpace(iis.SiteName))
                    throw new DeployException(
                        $"Project '{step.Project}': 'stopSite' is set but 'siteName' is missing.");

                var stopSite = await AppCmdAsync("Stop site",
                    $"stop site /site.name:\"{iis.SiteName}\"", StopSucceeded);
                result.DeploySteps.Add(stopSite);
                if (!stopSite.Success) throw new DeployException($"Failed to stop site '{iis.SiteName}'.");
                siteStopped = true;
            }

            var stopResult = await AppCmdAsync("Stop app pool",
                $"stop apppool /apppool.name:\"{iis.AppPool}\"", StopSucceeded);
            result.DeploySteps.Add(stopResult);
            if (!stopResult.Success) throw new DeployException($"Failed to stop app pool '{iis.AppPool}'.");

            if (step.Rollback && Directory.Exists(deployPath))
            {
                backupDir = deployPath.TrimEnd('\\', '/') + "_rollback_" + DateTimeOffset.Now.ToUnixTimeSeconds();
                FileHelper.CopyDirectory(deployPath, backupDir);
                result.DeploySteps.Add(ProcessRunner.Synthetic("Snapshot deploy folder", $"Backed up to {backupDir}"));
            }

            Directory.CreateDirectory(deployPath);
            StepResult copyResult;
            try
            {
                // From here the live folder is in an indeterminate state until the mirror
                // completes: it deletes whatever the artifact does not contain.
                liveModified = true;
                FileHelper.MirrorDirectory(step.Artifact, deployPath);
                copyResult = ProcessRunner.Synthetic("Mirror files to IIS path", $"{step.Artifact} -> {deployPath}");
            }
            catch (Exception ex)
            {
                copyResult = ProcessRunner.Synthetic("Mirror files to IIS path", ex.Message, success: false);
            }
            result.DeploySteps.Add(copyResult);
            if (!copyResult.Success) throw new DeployException("File copy to IIS path failed.");

            // Pool before site, the mirror of the stop order: the worker is ready before the
            // bindings reopen, so the first request in does not race a starting pool.
            var startResult = await AppCmdAsync("Start app pool", $"start apppool /apppool.name:\"{iis.AppPool}\"");
            result.DeploySteps.Add(startResult);
            if (!startResult.Success) throw new DeployException($"Failed to start app pool '{iis.AppPool}'.");

            if (siteStopped)
            {
                var startSite = await AppCmdAsync("Start site", $"start site /site.name:\"{iis.SiteName}\"");
                result.DeploySteps.Add(startSite);
                if (!startSite.Success) throw new DeployException($"Failed to start site '{iis.SiteName}'.");
                siteStopped = false;
            }

            if (!string.IsNullOrWhiteSpace(iis.WarmupUrl))
            {
                var warmStep = await WarmupAsync(iis.WarmupUrl);
                result.DeploySteps.Add(warmStep);
                // Classic mode has no intact previous slot to fall back to, so a failed
                // warmup is reported but does not trigger an automatic recovery.
                if (!warmStep.Success)
                    Console.WriteLine($"  [IIS] Warning: warmup failed for '{iis.WarmupUrl}'.");
            }

            result.Success = true;
        }
        catch (DeployException ex)
        {
            result.ErrorMessage = ex.Message;
            Console.WriteLine($"  [IIS] ✗ {ex.Message}");

            if (step.Rollback && backupDir is not null && Directory.Exists(backupDir))
            {
                FileHelper.TryDeleteDirectory(deployPath);
                Directory.Move(backupDir, deployPath);
                result.RolledBack = true;
            }

            var recovery = await AppCmdAsync("Start app pool (error recovery)",
                $"start apppool /apppool.name:\"{iis.AppPool}\"");
            result.DeploySteps.Add(recovery);

            // Leaving the site stopped would keep it dark even after a successful rollback,
            // so bring the bindings back whatever else went wrong.
            if (siteStopped)
                result.DeploySteps.Add(await AppCmdAsync("Start site (error recovery)",
                    $"start site /site.name:\"{iis.SiteName}\""));
        }
        finally
        {
            if (result.Success && backupDir is not null)
                FileHelper.TryDeleteDirectory(backupDir);
        }

        return (result, liveModified);
    }

    // -- Access probe ----------------------------------------------------------

    /// <summary>
    /// Reads the app pool's state - the cheapest appcmd call that still goes through
    /// applicationHost.config, which is what a non-Administrator is actually stopped by.
    ///
    /// Returns a failed result to abort on, or null to carry on. Only a positive access-denied
    /// signal aborts: a pool that does not exist, a missing appcmd, or any other failure falls
    /// through to the real steps, which report it far better than a probe could. Guessing from an
    /// inconclusive probe would block deploys that work today.
    /// </summary>
    private static async Task<TargetDeployResult?> ProbeControlAccessAsync(IisConfig iis, ApplyStep step)
    {
        // IIS is Windows-only; elsewhere this would just be a guaranteed "executable not found".
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(iis.AppPool)) return null;

        var probe = await AppCmdAsync("Precheck IIS access",
            $"list apppool \"{iis.AppPool}\" /text:state");

        if (!AccessDenied.Looks(probe)) return null;

        // Phrased to contain the phrase the classifier looks for, so this reads as access denied
        // from ErrorMessage alone and gets handed to the agent like any inline denial would.
        var message =
            $"Cannot control app pool '{iis.AppPool}' as {AccessDenied.CurrentIdentity()} - access is denied. " +
            "Nothing was stopped, drained or copied.";

        Console.WriteLine($"  [IIS] ✗ {message}");

        var result = new TargetDeployResult
        {
            TargetLabel  = step.Label,
            Type         = DeployType.Iis,
            Success      = false,
            ErrorMessage = message
        };
        result.DeploySteps.Add(probe);
        return result;
    }

    // -- Helpers ---------------------------------------------------------------

    private static async Task<StepResult> WarmupAsync(string url)
    {
        var result = new StepResult
        {
            StepName  = $"Warmup {url}",
            Command   = $"GET {url}",
            StartedAt = DateTimeOffset.Now
        };

        Console.WriteLine();
        Console.WriteLine($"  ┌- Warmup {url}");

        try
        {
            using var http = ProbeHttp.Create(TimeSpan.FromSeconds(WarmupTimeoutSeconds));
            var response = await http.GetAsync(url);

            result.Stdout   = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            result.Success  = response.IsSuccessStatusCode;
            result.ExitCode = result.Success ? 0 : (int)response.StatusCode;
            Console.WriteLine($"  │  {result.Stdout}");
        }
        catch (Exception ex)
        {
            result.Stderr   = ProbeHttp.Describe(ex);
            result.Success  = false;
            result.ExitCode = -1;
            Console.Error.WriteLine($"  │  ERR {result.Stderr}");
        }

        result.CompletedAt = DateTimeOffset.Now;
        var icon = result.Success ? "✓" : "✗";
        Console.WriteLine($"  └- {icon} ({result.Duration?.TotalSeconds:F1}s)");

        return result;
    }

    /// <summary>
    /// Picks the slot to deploy into: whichever one is not currently serving traffic.
    ///
    /// If the site points at neither slot - the first blue-green deploy, or a site still
    /// on its pre-blue-green path - slot A is chosen and flipped to. That bootstraps
    /// correctly without a special case.
    ///
    /// Pure and public so the three-way choice can be tested without IIS present.
    /// </summary>
    public static string ResolveIdleSlot(string activePath, string slotA, string slotB)
        => PathsEqual(activePath, slotA) ? slotB : slotA;

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            a.Trim().TrimEnd('\\', '/'),
            b.Trim().TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Last path segment, for a readable label: "C:\inetpub\Web_B" -> "Web_B".</summary>
    private static string SlotName(string path)
        => Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;

    private static Task<StepResult> AppCmdAsync(string stepName, string arguments,
        Func<int, bool>? successPredicate = null)
        => ProcessRunner.RunAsync(stepName, AppcmdPath, arguments,
            successPredicate: successPredicate);
}
