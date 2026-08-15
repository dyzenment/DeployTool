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

    public Task<TargetDeployResult> ApplyAsync(ApplyStep step)
    {
        var iis = step.Iis
            ?? throw new InvalidOperationException(
                $"Project '{step.Project}': type is 'iis' but the 'iis' block is missing.");

        return string.IsNullOrWhiteSpace(iis.SecondaryDeployPath)
            ? ApplyClassicAsync(step, iis)
            : ApplyBlueGreenAsync(step, iis);
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
    private async Task<TargetDeployResult> ApplyBlueGreenAsync(ApplyStep step, IisConfig iis)
    {
        var result = new TargetDeployResult { TargetLabel = step.Label, Type = DeployType.Iis };

        if (string.IsNullOrWhiteSpace(iis.SiteName))
        {
            result.ErrorMessage =
                $"Project '{step.Project}': 'secondaryDeployPath' is set (blue-green) but 'siteName' " +
                "is missing - the site is what gets flipped between slots.";
            Console.WriteLine($"  [IIS] ✗ {result.ErrorMessage}");
            return result;
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

        return result;
    }

    // -- Classic: stop pool, mirror in place, start pool ------------------------

    private async Task<TargetDeployResult> ApplyClassicAsync(ApplyStep step, IisConfig iis)
    {
        // Expanded here, on the box that owns the path - %ProgramData% and friends can
        // differ between the primary and a peer.
        var deployPath = FileHelper.ExpandEnvVars(iis.DeployPath);
        var result     = new TargetDeployResult { TargetLabel = step.Label, Type = DeployType.Iis };

        string? backupDir = null;

        try
        {
            var stopResult = await AppCmdAsync("Stop app pool",
                $"stop apppool /apppool.name:\"{iis.AppPool}\"",
                code => code == 0 || code == 1062);
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
                FileHelper.MirrorDirectory(step.Artifact, deployPath);
                copyResult = ProcessRunner.Synthetic("Mirror files to IIS path", $"{step.Artifact} -> {deployPath}");
            }
            catch (Exception ex)
            {
                copyResult = ProcessRunner.Synthetic("Mirror files to IIS path", ex.Message, success: false);
            }
            result.DeploySteps.Add(copyResult);
            if (!copyResult.Success) throw new DeployException("File copy to IIS path failed.");

            var startResult = await AppCmdAsync("Start app pool", $"start apppool /apppool.name:\"{iis.AppPool}\"");
            result.DeploySteps.Add(startResult);
            if (!startResult.Success) throw new DeployException($"Failed to start app pool '{iis.AppPool}'.");

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
        }
        finally
        {
            if (result.Success && backupDir is not null)
                FileHelper.TryDeleteDirectory(backupDir);
        }

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
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(WarmupTimeoutSeconds) };
            var response = await http.GetAsync(url);

            result.Stdout   = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            result.Success  = response.IsSuccessStatusCode;
            result.ExitCode = result.Success ? 0 : (int)response.StatusCode;
            Console.WriteLine($"  │  {result.Stdout}");
        }
        catch (Exception ex)
        {
            result.Stderr   = ex.Message;
            result.Success  = false;
            result.ExitCode = -1;
            Console.Error.WriteLine($"  │  ERR {ex.Message}");
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
