using System.Reflection;
using System.Text.Json;
using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Models.Plan;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Services;

/// <summary>
/// Hands each peer its run folder, after the primary has already applied its own plan
/// successfully.
///
/// The ordering is the failure policy. Nothing is written to any peer until the primary is
/// proven good, so a broken build cannot reach the fleet - there is no separate guard that
/// could be forgotten. The caller enforces this by simply not calling in.
///
/// Handoff is staged then moved: files land in a staging folder the peer's agent does not
/// look at, and only a completed folder is moved into incoming. A move within one share is
/// atomic on NTFS, so a poller can never observe a half-copied run.
///
/// This does NOT wait out the soak window. The soak deadline travels in the manifest as
/// notBeforeUtc and is the peer's business, so a CI runner is never held for an hour.
/// </summary>
public static class Propagator
{
    /// <summary>
    /// Where the shipped tool lands inside a run folder. The peer agent's poller looks for
    /// exactly this, so it must not vary with how the primary happened to be launched.
    /// </summary>
    public const string ToolFolderName = "tool";

    /// <summary>
    /// The frozen contract between a run folder and the peer's poll script: whatever this
    /// build needed in order to start, it is behind this filename.
    /// </summary>
    public const string LauncherCmd = "apply.cmd";

    /// <summary>The same launcher for a Unix peer. Both are always written - see ShipTool.</summary>
    public const string LauncherSh = "apply.sh";

    /// <summary>
    /// Sibling of the peer's incoming folder. Must share its volume for the atomic move, which
    /// is exactly the layout `install-agent` creates - hence the shared constant.
    /// </summary>
    private const string StagingFolderName = AgentLayout.StagingFolderName;

    /// <summary>
    /// Ships every peer plan. <paramref name="primarySucceededAt"/> starts the soak clock -
    /// pass the moment the primary finished applying, not the moment the plan was built.
    /// </summary>
    public static List<PropagationResult> Propagate(
        RolloutPlan plan,
        string stagingRoot,
        int keepRuns,
        DateTimeOffset primarySucceededAt)
    {
        var results      = new List<PropagationResult>();
        var notBeforeUtc = primarySucceededAt.ToUniversalTime().AddSeconds(plan.WaitSeconds);

        // SelectedPeers, not Peers: a box an srv: directive excluded is not "nothing to ship",
        // it is "not part of this run" - it gets no run folder and no report entry.
        foreach (var peer in plan.SelectedPeers)
        {
            var result = new PropagationResult
            {
                ServerName    = peer.ServerName,
                IncomingShare = peer.IncomingShare ?? string.Empty,
                StepCount     = peer.Steps.Count,
                NotBeforeUtc  = notBeforeUtc
            };

            // Every step in this run was Global scope, so there is nothing for this box to
            // apply. Shipping an empty run folder would still be picked up by its poller,
            // logged, and marked complete - noise standing in for work that does not exist.
            if (peer.Steps.Count == 0)
            {
                result.Success     = true;
                result.CompletedAt = DateTimeOffset.Now;
                results.Add(result);
                Console.WriteLine($"\n  → {peer.ServerName}: nothing server-scoped in this run - not shipped.");
                continue;
            }

            Console.WriteLine($"\n  → {peer.ServerName}: {peer.Steps.Count} step(s), " +
                $"applies no earlier than {notBeforeUtc.ToLocalTime():HH:mm:ss}");

            try
            {
                Ship(plan, peer, stagingRoot, keepRuns, notBeforeUtc, result);
                result.Success = true;
                Console.WriteLine($"    ✓ Delivered to {result.RunFolder}");
            }
            catch (Exception ex)
            {
                result.Success      = false;
                result.ErrorMessage = ex.Message;
                Console.WriteLine($"    ✗ {ex.Message}");
            }

            result.CompletedAt = DateTimeOffset.Now;
            results.Add(result);
        }

        return results;
    }

    // -- One peer --------------------------------------------------------------

    /// <summary>
    /// Delivers one server's run folder. Public because the local agent fallback ships to
    /// this same box through it: a ServerPlan whose incomingShare is a local path is just a
    /// peer that happens to be nearby, and there is deliberately no second delivery code path.
    /// </summary>
    public static void Ship(
        RolloutPlan plan,
        ServerPlan peer,
        string stagingRoot,
        int keepRuns,
        DateTimeOffset notBeforeUtc,
        PropagationResult result)
    {
        if (string.IsNullOrWhiteSpace(peer.IncomingShare))
            throw new DeployException(
                $"Server '{peer.ServerName}' is a peer but has no incomingShare configured. " +
                "Set it to the UNC path of that box's incoming folder, e.g. \"\\\\\\\\WEB02\\\\deploy\\\\incoming\".");

        // Expanded here, on the primary, because this path names a location the primary must
        // reach - unlike a step's destination path, which belongs to the box that applies it.
        var incoming = FileHelper.ExpandEnvVars(peer.IncomingShare).TrimEnd('\\', '/');
        var deployRoot = Path.GetDirectoryName(incoming);

        // Staging has to sit beside incoming on the same share, or the move stops being a
        // rename and becomes a copy - losing the atomicity the whole handoff rests on.
        if (string.IsNullOrWhiteSpace(deployRoot))
            throw new DeployException(
                $"Server '{peer.ServerName}': incomingShare '{incoming}' has no parent directory, so " +
                "there is nowhere to stage beside it. Point it at a subfolder of the share " +
                "(\"\\\\\\\\WEB02\\\\deploy\\\\incoming\"), not the share root.");

        // Authenticate before touching anything. In a workgroup the primary has no identity the
        // peer recognises, so without this every path below fails with access denied. A null
        // means no credentials were configured, which is the domain / mirrored-account case.
        using var share = NetworkShare.Connect(incoming, peer.ShareUsername, peer.SharePassword);

        if (share is not null)
        {
            result.ConnectedAs = share.Username;
            Console.WriteLine($"    Connected to {share.Root} as {share.Username}");
        }

        var peerStaging = Path.Combine(deployRoot, StagingFolderName, plan.RunId);
        var peerRun     = Path.Combine(incoming, plan.RunId);

        if (Directory.Exists(peerRun))
            throw new DeployException(
                $"Server '{peer.ServerName}': run folder '{peerRun}' already exists. " +
                "Refusing to overwrite a run the peer may be mid-way through.");

        // A staging folder left behind by an earlier failed attempt would otherwise merge
        // its stale artifacts into this run.
        FileHelper.TryDeleteDirectory(peerStaging);
        Directory.CreateDirectory(peerStaging);

        try
        {
            CopyArtifacts(peer, stagingRoot, peerStaging);
            result.ToolShipped = ShipTool(peerStaging);
            WriteManifest(plan, peer, peerStaging, notBeforeUtc);

            // The atomic handoff. Until this line the peer sees nothing it would act on.
            Directory.Move(peerStaging, peerRun);
            result.RunFolder = peerRun;
        }
        catch
        {
            // Never leave a partial staging folder on a share: the next run would find it and
            // refuse, or worse, merge into it.
            FileHelper.TryDeleteDirectory(peerStaging);
            throw;
        }

        Prune(incoming, keepRuns);
    }

    // -- Payload ---------------------------------------------------------------

    /// <summary>
    /// Copies only the artifacts this peer's steps actually reference. A peer never receives
    /// Global-scope output (a Velopack upload), so its folder holds only what it will apply.
    /// </summary>
    private static void CopyArtifacts(ServerPlan peer, string stagingRoot, string peerStaging)
    {
        var artifacts = peer.Steps
            .Select(s => s.Artifact)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in artifacts)
        {
            var normalized = relative.Replace('/', Path.DirectorySeparatorChar);
            var source     = Path.Combine(stagingRoot, normalized);

            if (!Directory.Exists(source))
                throw new DeployException(
                    $"Artifact '{relative}' is missing from the run's staging folder ({source}). " +
                    "Propagation must run before the artifacts are cleaned up.");

            FileHelper.CopyDirectory(source, Path.Combine(peerStaging, normalized));
        }
    }

    /// <summary>
    /// Ships this tool itself, so a peer applies the exact build that planned the run and
    /// version skew between primary and peer is impossible by construction.
    ///
    /// The whole payload directory travels, not one file. A .NET tool is framework-dependent:
    /// the executable is an apphost that is useless without its managed dll, deps.json and
    /// runtimeconfig.json beside it - and when the tool is installed with `dotnet tool install`
    /// there is no apphost in the package at all, only the dll. Copying a single binary
    /// produced a run folder the peer could not execute.
    ///
    /// Alongside it goes a launcher, generated here because this is the only place that knows
    /// how this particular build starts. Both a .cmd and a .sh are written: the primary is
    /// often a Linux runner shipping to a Windows peer, so it cannot pick one.
    ///
    /// Returns false rather than throwing when there is nothing shippable. The artifacts are
    /// still worth delivering - a peer with a stale tool can apply them - and the caller
    /// records the gap in the report.
    /// </summary>
    private static bool ShipTool(string peerStaging)
    {
        var toolDir = Path.Combine(peerStaging, ToolFolderName);

        // Single-file publish: there is no managed Location to read, and the executable
        // genuinely is the whole thing. Ship it alone and launch it directly.
        var entryAssembly = Assembly.GetEntryAssembly();
        var managedPath   = entryAssembly?.Location;

        if (string.IsNullOrEmpty(managedPath) || !File.Exists(managedPath))
            return ShipSingleFile(toolDir);

        var payloadDir = Path.GetDirectoryName(managedPath);

        if (string.IsNullOrEmpty(payloadDir) || !Directory.Exists(payloadDir))
        {
            Console.WriteLine("    ! Cannot locate this tool's payload directory - shipping artifacts only.");
            return false;
        }

        Directory.CreateDirectory(toolDir);

        foreach (var file in Directory.GetFiles(payloadDir))
        {
            // The XML doc file is the single largest thing in the payload and means nothing on
            // a peer. Everything else travels, pdbs included - a stack trace from a failed
            // apply is worth more than the bytes.
            if (Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase)) continue;

            File.Copy(file, Path.Combine(toolDir, Path.GetFileName(file)), overwrite: true);
        }

        WriteLaunchers(toolDir, Path.GetFileName(managedPath), selfContained: false);
        return true;
    }

    private static bool ShipSingleFile(string toolDir)
    {
        var processPath = Environment.ProcessPath;

        // Launched as `dotnet <dll>` with no managed location to read: nothing here is
        // shippable, since copying the shared host would put a launcher on the peer with
        // nothing to launch.
        if (string.IsNullOrEmpty(processPath) || !File.Exists(processPath) ||
            Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("    ! No shippable binary for this build - shipping artifacts only.");
            return false;
        }

        Directory.CreateDirectory(toolDir);
        var name = Path.GetFileName(processPath);
        File.Copy(processPath, Path.Combine(toolDir, name), overwrite: true);

        WriteLaunchers(toolDir, name, selfContained: true);
        return true;
    }

    /// <summary>
    /// Writes the one file the peer's frozen poll script looks for. Everything version- and
    /// layout-specific is decided here, at propagation time, which is what lets poll.cmd stay
    /// unchanged on every box in the fleet for the life of the tool.
    /// </summary>
    private static void WriteLaunchers(string toolDir, string entryFile, bool selfContained)
    {
        var cmd = selfContained
            ? $"""
               @echo off
               REM Generated by the primary at propagation time. Do not edit.
               "%~dp0{entryFile}" %*

               """
            : $"""
               @echo off
               REM Generated by the primary at propagation time. Do not edit.
               REM Framework-dependent build: launched through the shared host.
               setlocal
               set "DOTNET=dotnet"
               REM Task Scheduler runs as SYSTEM, whose PATH is not always refreshed after a
               REM runtime install. Fall back to the default install location before giving up.
               where dotnet >nul 2>&1 || set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
               "%DOTNET%" "%~dp0{entryFile}" %*

               """;

        var sh = selfContained
            ? $"""
               #!/bin/sh
               # Generated by the primary at propagation time. Do not edit.
               DIR=$(cd "$(dirname "$0")" && pwd)
               exec "$DIR/{entryFile}" "$@"

               """
            : $"""
               #!/bin/sh
               # Generated by the primary at propagation time. Do not edit.
               DIR=$(cd "$(dirname "$0")" && pwd)
               exec dotnet "$DIR/{entryFile}" "$@"

               """;

        File.WriteAllText(Path.Combine(toolDir, LauncherCmd), cmd);

        var shPath = Path.Combine(toolDir, LauncherSh);
        File.WriteAllText(shPath, sh);

        // A share written from Windows carries no mode bits, so a Unix peer would find the
        // launcher non-executable. Set it when we can; the peer's poll script does not depend
        // on the bit (it invokes /bin/sh explicitly) so failing here is not fatal.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(shPath,
                    UnixFileMode.UserRead  | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch { /* mode bits are a convenience here, not the contract */ }
        }
    }

    private static void WriteManifest(
        RolloutPlan plan, ServerPlan peer, string peerStaging, DateTimeOffset notBeforeUtc)
    {
        var manifest = new DeployManifest
        {
            RunId        = plan.RunId,
            CommitSha    = plan.CommitSha,
            Primary      = plan.Self.ServerName,
            Server       = peer.ServerName,
            CreatedUtc   = DateTimeOffset.UtcNow,
            NotBeforeUtc = notBeforeUtc,
            Steps        = peer.Steps
        };

        File.WriteAllText(
            Path.Combine(peerStaging, DeployManifest.FileName),
            JsonSerializer.Serialize(manifest, JsonOptions.Write));
    }

    // -- Retention -------------------------------------------------------------

    /// <summary>
    /// Keeps the newest <paramref name="keepRuns"/> run folders and deletes the rest, so a
    /// peer's share does not grow without bound.
    ///
    /// Ordered by folder name, not timestamp: a run id leads with yyyyMMdd-HHmmss, so its
    /// name sorts chronologically and stays correct regardless of what copy times the share
    /// recorded or how the clocks on the two boxes compare.
    ///
    /// Best-effort - a share that will not let us tidy up is not a reason to fail a delivery
    /// that already landed.
    /// </summary>
    private static void Prune(string incoming, int keepRuns)
    {
        if (keepRuns <= 0) return;

        try
        {
            var stale = Directory.GetDirectories(incoming)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Skip(keepRuns)
                .ToList();

            foreach (var dir in stale)
            {
                FileHelper.TryDeleteDirectory(dir);
                Console.WriteLine($"    Pruned old run {Path.GetFileName(dir)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    ! Could not prune old runs: {ex.Message}");
        }
    }
}
