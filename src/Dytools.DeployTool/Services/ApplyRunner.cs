using System.Text.Json;
using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Services;

/// <summary>
/// The peer half of a rollout: takes an <c>incoming</c> folder full of run folders and
/// applies the ones that are due.
///
/// It builds nothing, resolves nothing and reads no deploy-config.json. Every decision was
/// already made by the primary's planner and travels in <c>manifest.json</c>; this only
/// decides *when*. That is the point of the split - a peer cannot drift from the plan
/// because it has nothing to drift with.
///
/// Two invariants carry the whole design:
///
///   * <b>manifest present + no result.json = pending.</b> The poller fires every minute
///     forever, so a completion marker is the only thing standing between a finished
///     rollout and an infinite re-apply loop. result.json is written on failure too: a
///     deploy that failed at 02:00 must not be retried 1,400 times before someone looks.
///
///   * <b>Oldest first.</b> Run folders lead with yyyyMMdd-HHmmss, so applying in name
///     order replays the primary's history in the order it happened. Newest-first would
///     leave an older build on disk after a backlog drains.
/// </summary>
public static class ApplyRunner
{
    private enum Outcome { NotARun, AlreadyDone, Waiting, Applied, Failed }

    /// <summary>
    /// Applies every due run in <paramref name="incomingRoot"/>. Returns a process exit code:
    /// 0 when nothing failed, 1 when at least one run did.
    /// </summary>
    public static async Task<int> RunAsync(
        string incomingRoot,
        IReadOnlyDictionary<DeployType, IDeployTypeHandler> handlers)
    {
        incomingRoot = Path.GetFullPath(FileHelper.ExpandEnvVars(incomingRoot));

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  DeployTool apply  {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine($"  Host:     {HostIdentity.Resolve()}");
        Console.WriteLine($"  Incoming: {incomingRoot}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        // A box that has never received a run has no incoming folder, and the poller will
        // reach this line every minute of its life. Absence is the steady state, not a fault.
        if (!Directory.Exists(incomingRoot))
        {
            Console.WriteLine("  Nothing to do - incoming folder does not exist yet.");
            return 0;
        }

        var runFolders = Directory.GetDirectories(incomingRoot)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var counts = new Dictionary<Outcome, int>();

        foreach (var runFolder in runFolders)
        {
            var outcome = await ApplyRunFolderAsync(runFolder, handlers);
            counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
        }

        var applied = counts.GetValueOrDefault(Outcome.Applied);
        var failed  = counts.GetValueOrDefault(Outcome.Failed);
        var waiting = counts.GetValueOrDefault(Outcome.Waiting);
        var done    = counts.GetValueOrDefault(Outcome.AlreadyDone);

        Console.WriteLine();
        Console.WriteLine($"  {runFolders.Count} run folder(s): " +
            $"{applied} applied, {failed} failed, {waiting} waiting, {done} already complete.");

        return failed > 0 ? 1 : 0;
    }

    // -- One run folder --------------------------------------------------------

    private static async Task<Outcome> ApplyRunFolderAsync(
        string runFolder,
        IReadOnlyDictionary<DeployType, IDeployTypeHandler> handlers)
    {
        var runName      = Path.GetFileName(runFolder);
        var manifestPath = Path.Combine(runFolder, DeployManifest.FileName);
        var resultPath   = Path.Combine(runFolder, ReportWriter.FileName);

        // Not a run folder at all. Whatever else lives under incoming is none of our business.
        if (!File.Exists(manifestPath)) return Outcome.NotARun;

        if (File.Exists(resultPath)) return Outcome.AlreadyDone;

        DeployManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DeployManifest>(
                           await File.ReadAllTextAsync(manifestPath), JsonOptions.Read)
                       ?? throw new DeployException("manifest.json deserialized to null.");
        }
        catch (Exception ex)
        {
            // Rejected, not ignored. An unreadable manifest is a permanent condition - without
            // a marker the poller would re-read and re-fail it every minute until someone
            // noticed, and the failure would never be written down anywhere.
            Console.WriteLine($"\n  ✗ {runName}: unreadable manifest - {ex.Message}");
            WriteRejection(runFolder, runName, $"Could not read {DeployManifest.FileName}: {ex.Message}");
            return Outcome.Failed;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < manifest.NotBeforeUtc)
        {
            var due = manifest.NotBeforeUtc.ToLocalTime();

            // Date only when it is not today: this line is read in agent.log at 3am, and a
            // bare "05:00:00" for a run due next Tuesday is worse than no time at all.
            var when = due.Date == DateTimeOffset.Now.Date
                ? due.ToString("HH:mm:ss")
                : due.ToString("yyyy-MM-dd HH:mm:ss");

            Console.WriteLine(
                $"  {runName}: soaking until {when} ({FormatRemaining(manifest.NotBeforeUtc - now)} to go).");
            return Outcome.Waiting;
        }

        Console.WriteLine();
        Console.WriteLine($"══ Applying {runName} ══════════════════════════════════════");
        Console.WriteLine($"  From primary: {manifest.Primary}");
        Console.WriteLine($"  Commit:       {manifest.CommitSha}");
        Console.WriteLine($"  Planned for:  {manifest.Server}");
        Console.WriteLine($"  Steps:        {manifest.Steps.Count}");

        var report = new DeployReport
        {
            RunId      = manifest.RunId,
            ServerName = manifest.Server,
            CommitSha  = manifest.CommitSha
        };

        // A peer whose every step was Global scope gets an empty manifest. Nothing to do is a
        // success - but it still needs its marker, or it stays pending forever.
        if (manifest.Steps.Count == 0)
            Console.WriteLine("  Nothing server-scoped in this run - marking complete.");

        var success = true;

        // Grouped by project so a peer's result.json has the same shape as the primary's,
        // which is what lets one reader handle both.
        foreach (var group in manifest.Steps.GroupBy(s => s.Project, StringComparer.OrdinalIgnoreCase))
        {
            var projectResult = new ProjectDeployResult { ProjectName = group.Key };

            foreach (var step in group)
            {
                if (!handlers.TryGetValue(step.Type, out var handler))
                {
                    projectResult.TargetResults.Add(new TargetDeployResult
                    {
                        TargetLabel  = step.Label,
                        Type         = step.Type,
                        Success      = false,
                        ErrorMessage =
                            $"No handler for deploy type '{step.Type}'. The manifest was written " +
                            "by a newer DeployTool than the one applying it."
                    });
                    continue;
                }

                Console.WriteLine($"\n  -- {step.Project}: {step.Label} ----------------------");

                // Artifact paths are stored relative to the run folder, which is exactly what
                // lets a manifest written on the primary execute here at a different path.
                var targetResult = await handler.ApplyAsync(step.RebasedTo(runFolder));
                projectResult.TargetResults.Add(targetResult);

                if (!targetResult.Success)
                    Console.WriteLine($"  ✗ {targetResult.ErrorMessage}");
            }

            if (!projectResult.Success) success = false;
            report.Results.Add(projectResult);
        }

        report.CompletedAt = DateTimeOffset.Now;
        ReportWriter.Write(report, runFolder);

        Console.WriteLine(success
            ? $"  ✓ {runName} applied in {report.Duration?.TotalSeconds:F1}s."
            : $"  ✗ {runName} FAILED after {report.Duration?.TotalSeconds:F1}s - see {ReportWriter.FileName}.");

        return success ? Outcome.Applied : Outcome.Failed;
    }

    // -- Rejection marker ------------------------------------------------------

    /// <summary>
    /// Records a run that could not even be read as a failed run, so it is not retried and
    /// the reason survives in the same place every other outcome is recorded.
    /// </summary>
    private static void WriteRejection(string runFolder, string runName, string message)
    {
        var report = new DeployReport { RunId = runName, ServerName = HostIdentity.Resolve() };

        report.Results.Add(new ProjectDeployResult
        {
            ProjectName   = "(manifest)",
            TargetResults =
            {
                new TargetDeployResult
                {
                    TargetLabel  = "Read manifest",
                    Success      = false,
                    ErrorMessage = message
                }
            }
        });

        report.CompletedAt = DateTimeOffset.Now;

        try { ReportWriter.Write(report, runFolder); }
        catch (Exception ex)
        {
            // A read-only or vanished share is the likely cause. Say so - otherwise the run
            // silently re-fails on the next tick with no explanation for why the marker never
            // appeared.
            Console.WriteLine($"    ! Could not write {ReportWriter.FileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Largest sensible unit only. A soak window is minutes or hours; anything longer usually
    /// means a clock skew between primary and peer, and "3d 4h" makes that obvious in a way
    /// that "4560m" does not.
    /// </summary>
    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining.TotalDays    >= 1) return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours   >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        if (remaining.TotalMinutes >= 1) return $"{(int)remaining.TotalMinutes}m";
        return $"{Math.Ceiling(remaining.TotalSeconds):F0}s";
    }
}
