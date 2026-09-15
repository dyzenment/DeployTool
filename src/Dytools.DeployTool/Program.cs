using System.Diagnostics;
using System.Text.Json;
using Dytools.DeployTool;
using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Models.Plan;
using Dytools.DeployTool.Models.Reporting;
using Dytools.DeployTool.Resolvers;
using Dytools.DeployTool.Services;
using Dytools.DeployTool.Wizard;

internal class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Lower this process's priority before anything else runs, so the deploy pipeline
        // doesn't compete with live application processes on the production server. Child
        // processes inherit the priority they are created with, so the earlier this happens
        // the fewer of them escape at Normal. The full policy - core count, build servers,
        // background I/O - is applied once deploy-config.json has been read, below.
        ResourceGovernor.ApplyDefaultPriority();

        // Before anything prints. When output is redirected - the agent's poll script, or a CI
        // job - every line gets a timestamp and loses any escape sequences, so agent.log reads
        // as a log rather than as a transcript of a terminal nobody was sitting at.
        LogConsole.InstallIfRedirected();

        // Subcommands are checked before the --config requirement below, so every one of them
        // works in a fresh repo - and, more to the point, on a peer, which has no repo and no
        // deploy-config.json at all. Leading dashes are tolerated on all of them because the
        // agent's poll script is frozen around `--apply`.
        switch (args.Length > 0 ? args[0].TrimStart('-').ToLowerInvariant() : string.Empty)
        {
            // Scaffold or edit a deploy-config.json.
            case "init" or "makeconfig": return ConfigWizard.RunInit(args[1..]);
            case "edit":                 return ConfigWizard.RunEdit(args[1..]);

            // The peer half of a rollout: apply whatever is due in an incoming folder.
            case "apply":                return await RunApplyAsync(args);

            // Stand a peer up (or take it back down).
            case "install-agent":        return await AgentInstaller.InstallAsync(ReadAgentOptions(args));
            case "uninstall-agent":      return await AgentInstaller.UninstallAsync(ReadAgentOptions(args));

            // Which servers[] entry is this box? Answers it without running a deploy.
            case "hostname" or "whoami": return await RunHostnameAsync(args);

            // Can this box actually reach its peers? Same checks a deploy runs, no build.
            case "test-peers":           return await RunTestPeersAsync(args);

            case "help" or "h" or "?":   PrintUsage(); return 0;
            case "":                     PrintUsage(); return 1;
        }

        // Secrets referenced by deploy-config.json (via %VAR% placeholders) are read from
        // environment variables. Set them in your shell (or your CI job) before running:
        //   # macOS/Linux:  export MY_SECRET="..."
        //   # Windows:      $env:MY_SECRET = "..."
        //   dotnet run --project src/Dytools.DeployTool -- --config deploy-config.json --changed ""

        // -----------------------------------------------------------------------------
        // Parse arguments
        // Expected: --config <path> --changed "file1|file2|..." [--force-all true]
        //           [--pub "Web|Proc*"] [--wait 0]
        //
        // --pub / --wait mirror the pub:/wait: commit directives and override them when
        // present. This is the hand/test path: clone the repo and publish by name without
        // needing a commit that carries the directives, e.g.
        //   dytools-deploy --config deploy-config.json --changed "" --pub "*"
        // -----------------------------------------------------------------------------

        var parsedArgs = Args.Parse(args);
        var configPath = parsedArgs.GetRequired("config");
        var changedRaw = parsedArgs.GetOptional("changed") ?? string.Empty;
        var forceAll   = parsedArgs.GetFlag("force-all");

        var repoRoot = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? throw new InvalidOperationException("Cannot determine repo root from config path.");

        var changedFiles = changedRaw
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // -- Header ------------------------------------------------------------

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  DeployTool  {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine($"  Repo root:   {repoRoot}");
        Console.WriteLine($"  Config:      {configPath}");
        Console.WriteLine($"  Changed:     {changedFiles.Count} file(s)");
        Console.WriteLine($"  Force all:   {forceAll}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        // -- Load config -------------------------------------------------------

        var configJson   = await File.ReadAllTextAsync(configPath);
        var deployConfig = JsonSerializer.Deserialize<DeployConfig>(configJson, JsonOptions.Default)
            ?? throw new InvalidOperationException("Failed to deserialize deploy-config.json.");

        // Apply the full resource policy now that config is available. Everything spawned from
        // here on inherits it - priority through process creation, the build-server and GC
        // opt-outs through the environment.
        ResourceGovernor.Apply(deployConfig.Resources);
        Console.WriteLine($"  Resources:   {ResourceGovernor.Describe()}");

        // -- Filter disabled projects -------------------------------------------

        var disabled = deployConfig.Projects.Where(p => p.Disabled).ToList();
        if (disabled.Count > 0)
        {
            Console.WriteLine("\n  Disabled projects (skipped):");
            foreach (var p in disabled)
                Console.WriteLine($"    • {p.Name}");
            deployConfig.Projects.RemoveAll(p => p.Disabled);
        }

        // -- Validate existence ------------------------------------------------

        var missing = deployConfig.Projects.Where(p =>
        {
            var folder = Path.Combine(repoRoot, deployConfig.ProjectsFolder, p.Name);
            var csproj = Path.Combine(folder, $"{p.Name}.csproj");
            return !Directory.Exists(folder) || !File.Exists(csproj);
        }).ToList();

        if (missing.Count > 0)
        {
            Console.WriteLine("\n  ✗ The following projects in deploy-config.json do not exist on disk:");
            foreach (var p in missing)
            {
                var folder = Path.Combine(repoRoot, deployConfig.ProjectsFolder, p.Name);
                var csproj = Path.Combine(folder, $"{p.Name}.csproj");
                var detail = !Directory.Exists(folder)
                    ? $"folder not found: {folder}"
                    : $"csproj not found: {csproj}";
                Console.WriteLine($"    • {p.Name}  ({detail})");
            }
            Console.WriteLine();
            Console.WriteLine("  Fix deploy-config.json or ensure the projects are checked in.");
            return 1;
        }

        // -- Build project registry (single scan of all csprojs) ---------------

        Console.WriteLine("\n-- Project Discovery -------------------------------------------");
        var registry = ProjectRegistry.Build(deployConfig, repoRoot);
        Console.WriteLine($"  Discovered {registry.AllProjects.Count} project(s) " +
            $"({registry.AllProjects.Count(p => p.IsDeployable)} deployable, " +
            $"{registry.AllProjects.Count(p => !p.IsDeployable)} libraries)");

#if DEBUG
        DeployListResolver.PrintGraph(registry);
#endif

        // -- Initialize report -------------------------------------------------

        var commitSha  = GetCommitSha();
        var shortSha   = commitSha.Length >= 7 ? commitSha[..7] : commitSha;
        var runId      = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{shortSha}";

        // Which servers[] entry this box is. Defaults to the machine name and needs no
        // configuration; --server or DEPLOYTOOL_SERVER pin it for boxes the fleet calls
        // something other than what Windows does. See `dytools-deploy hostname`.
        var serverOverride = parsedArgs.GetOptional("server");
        var serverName     = HostIdentity.Resolve(serverOverride);

        var report = new DeployReport
        {
            RunId        = runId,
            ServerName   = serverName,
            CommitSha    = commitSha,
            ChangedFiles = changedFiles,
            ForcedAll    = forceAll
        };
        Console.WriteLine($"\n  Commit: {report.CommitSha}");
        Console.WriteLine($"  Run ID: {runId}");
        Console.WriteLine($"  Server: {serverName}  (from {HostIdentity.ResolveSource(serverOverride)})");

        // -- Directives --------------------------------------------------------
        // Base directives come from the HEAD commit message; --pub/--wait on the command
        // line override them field by field (for hand/test runs).

        var commitMessage    = GetCommitMessage();
        var commitDirectives = CommitDirectives.Parse(commitMessage);

        var cliOverrides = CommitDirectives.FromValues(
            parsedArgs.GetOptional("pub"),
            parsedArgs.GetOptionalInt("wait"),
            parsedArgs.GetOptionalBool("skip-tests"),
            parsedArgs.GetOptional("srv"));
        var directives = commitDirectives.OverlaidWith(cliOverrides);

        var subject = commitMessage.Split('\n').FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(subject))
            Console.WriteLine($"  Message: {subject}");
        if (cliOverrides.HasPub || cliOverrides.HasSrv
            || cliOverrides.WaitSeconds.HasValue || cliOverrides.SkipTests.HasValue)
            Console.WriteLine("  (directives overridden from command line)");
        if (directives.HasPub)
            Console.WriteLine($"  Directive pub:  {string.Join(" | ", directives.PubPatterns!)}");
        if (directives.HasSrv)
            Console.WriteLine($"  Directive srv:  {string.Join(" | ", directives.SrvPatterns!)}");
        if (directives.WaitSeconds.HasValue)
            Console.WriteLine($"  Directive wait: {directives.WaitSeconds.Value}s");
        if (directives.SkipTests == true)
            Console.WriteLine("  Directive skiptests: unit-test gate bypassed for this run");

        // -- Dependency resolution ---------------------------------------------

        Console.WriteLine("\n-- Dependency Resolution ---------------------------------------");

        var selection        = DeployListResolver.Resolve(
            registry, changedFiles, forceAll, directives,
            deployConfig.DoNotPublishIfNoPubInCommitMessage);
        var projectsToDeploy = selection.Projects;

        report.SkippedProjects = deployConfig.Projects.Count - projectsToDeploy.Count;
        Console.WriteLine($"\n  → {projectsToDeploy.Count} project(s) selected via {selection.Reason}" +
            $", {report.SkippedProjects} skipped.");

        if (projectsToDeploy.Count == 0)
        {
            Console.WriteLine("  Nothing to deploy.");
            report.CompletedAt = DateTimeOffset.Now;
            WriteReport(report);
            return 0;
        }

        // -- Prerequisites -----------------------------------------------------

        Console.WriteLine("\n-- Prerequisites -----------------------------------------------");

        var selectedTargets = projectsToDeploy.SelectMany(p => p.Config!.Targets).ToList();
        var allPrereqs      = selectedTargets.SelectMany(t => t.Prerequisites)
                                             .Distinct(StringComparer.OrdinalIgnoreCase);
        var needsVpk        = selectedTargets.Any(t => t.Type == DeployType.Velopack);

        var prereqResults = await PrerequisiteInstaller.InstallAsync(allPrereqs, needsVpk);
        report.PrerequisiteResults.AddRange(prereqResults);

        if (prereqResults.Any(r => !r.Success))
        {
            Console.WriteLine("\n  ✗ One or more prerequisites failed to install. Aborting.");
            WriteReport(report);
            return 1;
        }

        // -- Handlers ----------------------------------------------------------

        var handlers = BuildHandlers();

        // -- Plan --------------------------------------------------------------
        // The entire rollout -- every server, every step -- decided here in one shot from
        // one set of inputs, so peer plans are never recomputed later or on the peer itself.

        RolloutPlan plan;
        try
        {
            plan = Planner.Plan(
                runId, commitSha, projectsToDeploy, deployConfig, handlers,
                serverName, directives, selection.Reason);
        }
        catch (DeployException ex)
        {
            Console.WriteLine($"\n  ✗ {ex.Message}");
            report.CompletedAt = DateTimeOffset.Now;
            WriteReport(report);
            return 1;
        }

        Console.WriteLine("\n-- Rollout Plan ------------------------------------------------");
        PrintPlan(plan);

        // -- Staging root ------------------------------------------------------
        // Published artifacts live here for the whole run. Owned by the orchestrator, not
        // by the handlers, because they must outlive publish: applied locally now and (from
        // phase 5) copied to peers afterwards.
        //
        // Deliberately NOT the agent's incoming folder: if the primary staged a run where
        // its own poller could see it, the poller could pick it up mid-apply and run it
        // concurrently. Task Scheduler's IgnoreNew guards task-vs-task, not task-vs-primary.

        // -- Peer precheck -----------------------------------------------------
        // Before the build, not after it. Everything checked here is knowable in about a
        // second and none of it depends on build output, so discovering an unreachable peer
        // at propagation time means having paid for a full compile and test cycle first.

        if ((plan.HasPeers || plan.SelfCanHandOff) && (deployConfig.Rollout?.PrecheckPeers ?? true)
                                                    && !parsedArgs.GetFlag("no-precheck"))
        {
            Console.WriteLine("\n-- Peer Precheck -----------------------------------------------");

            // Self first when it may hand steps to its own agent: it is checked exactly as a
            // peer is, because it would be delivered to exactly as a peer is.
            var peerTargets = (plan.SelfCanHandOff ? [plan.Self] : Enumerable.Empty<ServerPlan>())
                .Concat(plan.SelectedPeers)
                .Select(p => new PeerTarget(p.ServerName, p.IncomingShare, p.ShareUsername, p.SharePassword))
                .ToList();

            if (!await PeerPrecheck.RunAsync(peerTargets))
            {
                Console.WriteLine(
                    "\n  ✗ One or more peers are not ready. Nothing was built.\n" +
                    "    Fix the above, or set rollout.precheckPeers to false (or pass " +
                    "--no-precheck) to\n    deploy locally anyway and let propagation fail later.");

                report.CompletedAt = DateTimeOffset.Now;
                WriteReport(report);
                return 1;
            }
        }

        var stagingRoot = Path.Combine(Path.GetTempPath(), "DeployTool", "staging", runId);
        Directory.CreateDirectory(stagingRoot);

        try
        {
            // -- Deploy each project (sequential) ---------------------------------

            var attempts = new List<(ApplyStep Step, ProjectDeployResult Project, TargetDeployResult Result)>();

            foreach (var project in projectsToDeploy)
            {
                Console.WriteLine();
                Console.WriteLine($"══ Project: {project.Name} ══════════════════════════════════════");

                var projectResult = await DeployProjectAsync(
                    project, plan, handlers, registry, stagingRoot, attempts);
                report.Results.Add(projectResult);
            }

            // -- This box, via its agent ------------------------------------------
            // Fire and forget, like a peer. true: every server-scoped step, gated the same way
            // peers are - nothing inline may have failed. null: only the targets that failed
            // inline for lack of rights; everything else already has its verdict.
            var keepRuns = deployConfig.Rollout?.KeepRuns ?? 5;

            if (plan.Self.ApplyViaAgent == true)
            {
                Console.WriteLine("\n-- Apply via agent ---------------------------------------------");

                var steps = plan.SelfAgentSteps;

                if (steps.Count == 0)
                    Console.WriteLine("  Nothing server-scoped for this box in this run.");
                else if (!NothingFailedYet(report))
                    Console.WriteLine("  Build, tests or an inline step failed -- nothing handed to the agent.");
                else
                {
                    var shipped = SelfViaAgent.Ship(plan, steps, stagingRoot, keepRuns, report);
                    var error   = report.Propagation.Last().ErrorMessage;

                    foreach (var step in steps)
                        ProjectResult(report, step.Project).TargetResults.Add(SelfViaAgent.HandedOff(step, shipped, error));
                }
            }
            else if (plan.Self.ApplyViaAgent is null)
            {
                var denied = attempts.Where(a => AccessDenied.Looks(a.Result)).ToList();

                if (denied.Count > 0)
                    HandDeniedToAgent(plan, denied, stagingRoot, keepRuns, report);
            }

            // -- Propagate to peers -------------------------------------------
            // Only reached when this box's own apply succeeded. That ordering IS the
            // failure policy: a broken build never gets written to a peer, so there is no
            // separate guard to forget. The soak clock starts here, at proven-good, and
            // travels in the manifest - the runner is not held for the window.

            if (plan.HasPeers)
            {
                Console.WriteLine("\n-- Propagation -------------------------------------------------");

                if (report.Success)
                    report.Propagation.AddRange(Propagator.Propagate(
                        plan, stagingRoot,
                        deployConfig.Rollout?.KeepRuns ?? 5,
                        DateTimeOffset.Now));
                else
                    Console.WriteLine(
                        "  Primary failed -- no manifests written. " +
                        $"{plan.SelectedPeers.Count()} peer(s) receive nothing.");
            }
        }
        finally
        {
            // Drop the bulk, keep the run folder: result.json is the audit record of what
            // this box did. Artifacts are regenerable; the report is not.
            FileHelper.TryDeleteDirectory(Path.Combine(stagingRoot, "artifacts"));
        }

        // -- Final report ------------------------------------------------------

        report.CompletedAt = DateTimeOffset.Now;
        WriteReport(report);
        ReportWriter.Write(report, stagingRoot);
        return report.Success ? 0 : 1;
    }

    // -- Handlers --------------------------------------------------------------

    /// <summary>
    /// The one handler table. Shared by the primary's inline apply and a peer's apply mode,
    /// because "identical executor on both sides" stops being true the moment there are two
    /// places that decide what a deploy type means.
    /// </summary>
    private static Dictionary<DeployType, IDeployTypeHandler> BuildHandlers() => new()
    {
        [DeployType.Velopack] = new VelopackHandler(),
        [DeployType.Iis]      = new IisHandler(),
        [DeployType.Folder]   = new FolderHandler()
    };

    // -- apply -----------------------------------------------------------------

    /// <summary>
    /// Peer mode. Invoked by the agent's poll script every minute, forever, as
    /// <c>DeployTool.exe --apply "C:\deploy\incoming"</c>.
    ///
    /// Takes no config: everything this box is meant to do arrived with the run. Bare
    /// <c>apply</c> falls back to the standard layout so it can be run by hand on an
    /// installed peer without repeating a path the installer already fixed.
    /// </summary>
    private static async Task<int> RunApplyAsync(string[] args)
    {
        var parsed = Args.Parse(args);

        var incoming = Positional(args, 1)
                    ?? parsed.GetOptional("apply")
                    ?? new AgentLayout(parsed.GetOptional("root") ?? AgentInstaller.DefaultRoot).Incoming;

        try
        {
            return await ApplyRunner.RunAsync(incoming, BuildHandlers());
        }
        catch (Exception ex)
        {
            // The poll script redirects to agent.log and nobody is watching the console, so an
            // unhandled exception here would be an invisible failure repeating every minute.
            Console.WriteLine($"\n  ✗ Apply aborted: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // -- install-agent / uninstall-agent ---------------------------------------

    private static AgentOptions ReadAgentOptions(string[] args)
    {
        var parsed = Args.Parse(args);

        return new AgentOptions
        {
            Root        = parsed.GetOptional("root")      ?? AgentInstaller.DefaultRoot,
            TaskName    = parsed.GetOptional("task-name") ?? AgentInstaller.DefaultTaskName,
            RunAsUser   = parsed.GetOptional("user")      ?? "SYSTEM",
            AccountName = parsed.GetOptional("account")   ?? "deploysvc",
            NoPrompt    = parsed.GetOptionalBool("no-prompt") ?? false,
            // A sub-minute interval is not expressible in Task Scheduler's repetition, and a
            // zero would register a task that never fires.
            IntervalMinutes = Math.Max(1,
                parsed.GetOptionalInt("interval") ?? AgentInstaller.DefaultIntervalMinutes)
        };
    }

    // -- hostname --------------------------------------------------------------

    /// <summary>
    /// Answers "which servers[] entry is this box, and why" without running a deploy.
    ///
    /// Worth its own command because the failure it prevents is silent: a box whose hostname
    /// does not match anything in servers[] still deploys itself perfectly well - it just
    /// never propagates to anyone, and nothing about a green run says so.
    ///
    /// Exits 1 when a config was given and nothing matched, so a fleet check can be scripted.
    /// </summary>
    private static async Task<int> RunHostnameAsync(string[] args)
    {
        var parsed         = Args.Parse(args);
        var serverOverride = parsed.GetOptional("server");
        var identity       = HostIdentity.Resolve(serverOverride);

        Console.WriteLine();
        Console.WriteLine("  Names this box is known by");
        foreach (var (source, value) in HostIdentity.Describe())
            Console.WriteLine($"    {source,-26} {value}");

        Console.WriteLine();
        Console.WriteLine($"  Identifying as: {identity}   (from {HostIdentity.ResolveSource(serverOverride)})");

        var configPath = parsed.GetOptional("config") ?? Positional(args, 1);

        if (configPath is null)
        {
            Console.WriteLine();
            Console.WriteLine("  Pass --config <deploy-config.json> to check this against servers[].");
            return 0;
        }

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"\n  ✗ No such config file: {configPath}");
            return 1;
        }

        var config = JsonSerializer.Deserialize<DeployConfig>(
            await File.ReadAllTextAsync(configPath), JsonOptions.Default);

        if (config is null)
        {
            Console.WriteLine($"\n  ✗ Could not parse {configPath}.");
            return 1;
        }

        if (config.Servers.Count == 0)
        {
            Console.WriteLine($"\n  servers[] in {configPath} is empty - single-box mode, nothing to match.");
            return 0;
        }

        HostMatch? match;
        try
        {
            match = HostIdentity.Find(config.Servers, identity);
        }
        catch (DeployException ex)
        {
            Console.WriteLine($"\n  ✗ {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"  servers[] in {configPath}");
        foreach (var server in config.Servers)
        {
            var isSelf = match is not null && ReferenceEquals(match.Server, server);
            Console.WriteLine(
                $"    {(isSelf ? "→" : " ")} {server.Name,-18} hostname \"{server.Hostname}\"" +
                (isSelf ? $"   ← this box (matched by {match!.Reason})" : string.Empty));
        }

        Console.WriteLine();

        if (match is null)
        {
            Console.WriteLine("  ✗ Nothing matches this box. A deploy run here would apply locally and");
            Console.WriteLine("    propagate to nobody. Either add an entry:");
            Console.WriteLine();
            Console.WriteLine("      {");
            Console.WriteLine($"        \"name\": \"{identity.ToLowerInvariant()}\",");
            Console.WriteLine($"        \"hostname\": \"{identity}\",");
            Console.WriteLine("        \"incomingShare\": null");
            Console.WriteLine("      }");
            Console.WriteLine();
            Console.WriteLine($"    or point this box at an existing entry with {HostIdentity.EnvVariable}.");
            return 1;
        }

        Console.WriteLine(
            $"  ✓ A deploy run here is the primary for '{match.Server.Name}', propagating to " +
            $"{config.Servers.Count - 1} peer(s).");
        return 0;
    }

    // -- test-peers ------------------------------------------------------------

    /// <summary>
    /// The precheck on its own, so a fleet can be verified without committing anything or
    /// waiting for a build. Exits 1 if any peer is not ready, so it can gate a script.
    /// </summary>
    private static async Task<int> RunTestPeersAsync(string[] args)
    {
        var parsed     = Args.Parse(args);
        var configPath = parsed.GetOptional("config") ?? Positional(args, 1) ?? "deploy-config.json";

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"\n  ✗ No such config file: {configPath}");
            return 1;
        }

        var config = JsonSerializer.Deserialize<DeployConfig>(
            await File.ReadAllTextAsync(configPath), JsonOptions.Default);

        if (config is null)
        {
            Console.WriteLine($"\n  ✗ Could not parse {configPath}.");
            return 1;
        }

        var identity = HostIdentity.Resolve(parsed.GetOptional("server"));
        var self     = HostIdentity.Find(config.Servers, identity)?.Server;

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  DeployTool  peer check");
        Console.WriteLine($"  From:   {identity}{(self is null ? "  (not listed in servers[])" : $"  = {self.Name}")}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        // Every server except this one - exactly the set a deploy from here would ship to.
        var peers = config.Servers
            .Where(server => !ReferenceEquals(server, self))
            .Select(server => new PeerTarget(server.Name, server.IncomingShare, server.Username, server.Password))
            .ToList();

        if (peers.Count == 0)
        {
            Console.WriteLine("\n  No peers to check - this is a single-box configuration.");
            return 0;
        }

        var ok = await PeerPrecheck.RunAsync(peers);

        Console.WriteLine(ok
            ? $"\n  ✓ All {peers.Count} peer(s) reachable and writable.\n"
            : "\n  ✗ At least one peer is not ready - see above.\n");

        return ok ? 0 : 1;
    }

    // -- Usage -----------------------------------------------------------------

    private static void PrintUsage()
    {
        Console.WriteLine("""

          dytools-deploy - config-driven .NET deployment across one or more servers.

          Deploy (the primary; this is what CI runs)
            dytools-deploy --config <path> [options]
              --changed "a|b|c"    Pipe-separated changed files. Decides what needs deploying.
              --force-all true     Deploy everything, ignoring the changed-file diff.
              --pub "Web|Proc*"    Override the commit's pub: directive - which projects.
              --srv "S2|web*"      Override the commit's srv: directive - which servers.
              --wait <seconds>     Override the rollout soak delay. 0 = peers apply immediately.
              --skip-tests         Bypass the unit-test gate.
              --server <name>      Pin which servers[] entry this box is.
              --no-precheck        Skip the peer reachability check and build anyway.

          Peer
            dytools-deploy apply [incoming]     Apply whatever is due. Run by the agent every minute.
            dytools-deploy install-agent        Create the layout, poll script and schedule.
            dytools-deploy uninstall-agent      Remove the schedule (folders and history stay).
              --root <path>        Agent root. Default C:\deploy (Windows), /var/lib/deploytool.
              --interval <minutes> Poll interval. Default 1.
              --task-name <name>   Scheduled task name. Default DeployAgent.
              --user <account>     Who the TASK runs as: SYSTEM (default), LOCALSERVICE, NETWORKSERVICE.
              --account <name>     Local account the PRIMARY connects as. Default deploysvc;
                                   install offers to create it and grant it the share.
              --no-prompt          Skip that offer and print the commands instead.

          Diagnostics
            dytools-deploy hostname [--config <path>]     Which servers[] entry is this box?
            dytools-deploy test-peers [--config <path>]   Can this box reach and write to its peers?

          Config
            dytools-deploy init [path]          Scaffold a deploy-config.json.
            dytools-deploy edit <path>          Edit an existing one.

          """);
    }

    /// <summary>
    /// A positional argument, or null when that slot holds a switch instead. Lets
    /// `apply C:\deploy\incoming` and `--apply C:\deploy\incoming` mean the same thing.
    /// </summary>
    private static string? Positional(string[] args, int index)
        => args.Length > index && !args[index].StartsWith("--", StringComparison.Ordinal)
            ? args[index]
            : null;

    // -- Plan output -----------------------------------------------------------

    private static void PrintPlan(RolloutPlan plan)
    {
        foreach (var server in plan.ServerPlans)
        {
            var role     = server.IsSelf
                ? server.ApplyViaAgent switch
                {
                    true  => "self, primary, applies via agent",
                    null when server.IncomingShare is not null => "self, primary, agent on access denied",
                    _     => "self, primary"
                }
                : "peer";
            var excluded = server.Selected ? string.Empty : ", excluded by srv:";
            Console.WriteLine($"  {server.ServerName}  [{role}{excluded}]  {server.Steps.Count} step(s)");

            // Where a peer's run folder is going, and who it will be written as. Both come
            // from config and were previously invisible until propagation failed - which
            // happens after the build, so a stale or mistyped share cost a full run to find.
            if ((!server.IsSelf || server.IncomingShare is not null) && server.Selected)
            {
                Console.WriteLine($"      → {server.IncomingShare ?? "(no incomingShare configured!)"}");

                if (!string.IsNullOrWhiteSpace(server.ShareUsername))
                    Console.WriteLine($"        as {server.ShareUsername}");
            }

            foreach (var step in server.Steps)
                Console.WriteLine($"      {step.Project,-22} {step.Label}");
        }

        // What this box will compile. One line per build variant, with how many targets
        // ride on it - a "(2 targets)" is the dedupe doing its job, visible before any build.
        Console.WriteLine("\n  Publishes (this server):");
        foreach (var publish in plan.Publishes)
        {
            var consumers = plan.Targets.Count(t => t.ArtifactRelativePath == publish.ArtifactRelativePath);
            Console.WriteLine(
                $"      {publish.ProjectName,-22} {publish.Label,-32} → {publish.ArtifactRelativePath}  " +
                $"({consumers} target{(consumers == 1 ? "" : "s")})");
        }

        if (plan.HasPeers)
            Console.WriteLine(
                $"\n  Peers apply {plan.WaitSeconds}s after this server succeeds " +
                "(soak clock starts on success, not now).");
    }

    // -- Project deploy orchestrator -------------------------------------------

    private static async Task<ProjectDeployResult> DeployProjectAsync(
        DiscoveredProject project,
        RolloutPlan plan,
        Dictionary<DeployType, IDeployTypeHandler> handlers,
        ProjectRegistry registry,
        string stagingRoot,
        List<(ApplyStep Step, ProjectDeployResult Project, TargetDeployResult Result)> attempts)
    {
        var result = new ProjectDeployResult { ProjectName = project.Name };

        // -- Pre-build ---------------------------------------------------------
        Console.WriteLine("  -- Pre-build --------------------------------------------");
        if (!await RunPreBuildAsync(project, result)) return result;

        // -- Unit tests --------------------------------------------------------
        Console.WriteLine("  -- Unit Tests -------------------------------------------");
        if (!await RunTestsAsync(project, registry, result, plan.SkipTests)) return result;

        // -- Publish -----------------------------------------------------------
        // One build per variant, not per target. The plan already folded every target with
        // the same build block onto one PublishPlan, so "IIS + folder, same build" compiles
        // once here and the two apply steps below read the same artifact folder. Identical
        // for every deploy type, so it lives here rather than in a handler.
        var publishes = plan.Publishes.Where(p => p.ProjectName == project.Name).ToList();
        var targets   = plan.Targets.Where(t => t.ProjectName == project.Name).ToList();
        var published = new Dictionary<string, PublishResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var publish in publishes)
        {
            var consumers = targets.Where(t => t.ArtifactRelativePath == publish.ArtifactRelativePath)
                                   .Select(t => t.Step.Label)
                                   .ToList();

            Console.WriteLine($"\n  -- Publish: {publish.Label} -------------------------------");
            Console.WriteLine($"     → {publish.ArtifactRelativePath}  ({consumers.Count} target(s))");

            var artifactDir = Path.Combine(stagingRoot, publish.ArtifactRelativePath);
            Directory.CreateDirectory(artifactDir);

            var step = await BuildHelper.PublishAsync(
                project.Name, project.Folder, publish.Build, artifactDir, project.NoWarn);

            var publishResult = new PublishResult
            {
                Label    = publish.Label,
                Artifact = publish.ArtifactRelativePath,
                Targets  = consumers,
                Result   = step
            };
            result.PublishResults.Add(publishResult);
            published[publish.ArtifactRelativePath] = publishResult;

            Console.WriteLine(step.Success
                ? $"  ✓ Published in {step.Duration?.TotalSeconds:F1}s."
                : "  ✗ Publish failed.");
        }

        // -- Targets -----------------------------------------------------------
        // Sourced from the plan, not re-derived from config: the plan already decided what
        // this box does, and the same ApplyStep objects are what peers receive.
        foreach (var targetPlan in targets)
        {
            var target = targetPlan.Target;
            Console.WriteLine($"\n  -- Target: {targetPlan.Step.Label} ----------------------------------");

            // A failed publish fails every target that was waiting on it, each with its own
            // entry so the report still lists what did not go live.
            var publishResult = published[targetPlan.ArtifactRelativePath];
            if (!publishResult.Success)
            {
                result.TargetResults.Add(new TargetDeployResult
                {
                    TargetLabel  = targetPlan.Step.Label,
                    Type         = target.Type,
                    Success      = false,
                    ErrorMessage = $"Publish failed ({publishResult.Label}) - nothing to apply."
                });
                Console.WriteLine("  ✗ Skipped - its publish failed.");
                continue;
            }

            // -- Apply ---------------------------------------------------------
            // applyViaAgent = true: server-scoped steps are not this process's to run. They go
            // to this box's agent after every project is built; nothing is recorded here so the
            // report gets one entry per target, added at handoff.
            if (plan.Self.ApplyViaAgent == true && targetPlan.Scope == DeployScope.Server)
            {
                Console.WriteLine("  → Handed to this box's agent once every project is built.");
                continue;
            }

            // Self's plan is the authority on what runs here. An srv: directive that excludes
            // this box empties it (bar Global steps), and the build above still had to happen -
            // the artifacts are what the peers are waiting for.
            if (!plan.Self.Steps.Contains(targetPlan.Step))
            {
                result.TargetResults.Add(new TargetDeployResult
                {
                    TargetLabel = $"{targetPlan.Step.Label}  [built, not applied here]",
                    Type        = target.Type,
                    Success     = true
                });
                Console.WriteLine("  – Built but not applied here (srv: excludes this server).");
                continue;
            }

            // The step's artifact path is stored relative so the same object can travel to
            // a peer; rebasing resolves it against this box's staging folder.
            var targetResult = await handlers[target.Type]
                .ApplyAsync(targetPlan.Step.RebasedTo(stagingRoot));
            result.TargetResults.Add(targetResult);

            // Recorded with its step so that, with applyViaAgent = null, an access-denied
            // failure can be handed to the agent afterwards.
            attempts.Add((targetPlan.Step, result, targetResult));

            if (!targetResult.Success)
                Console.WriteLine($"  ✗ {targetResult.ErrorMessage}");
            else
                Console.WriteLine($"  ✓ Target complete in " +
                    $"{targetResult.DeploySteps.Sum(s => s.Duration?.TotalSeconds ?? 0):F1}s deploy " +
                    $"(from {publishResult.Label}).");
        }

        return result;
    }

    /// <summary>
    /// Whether anything so far went wrong, without requiring anything to have gone right -
    /// a project whose only targets are going to the agent has no target results yet.
    /// </summary>
    private static bool NothingFailedYet(DeployReport report) => report.Results.All(r =>
        !r.TestsFailed &&
        r.PreBuildResults.All(s => s.Success) &&
        r.PublishResults.All(p => p.Success) &&
        r.TargetResults.All(t => t.Success));

    private static ProjectDeployResult ProjectResult(DeployReport report, string project)
        => report.Results.First(r => r.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// applyViaAgent = null, second attempt. Says what this account could not do, then hands
    /// exactly those targets to the agent if this host's servers[] entry gives it a share.
    /// A handed-off target's inline failure is overwritten by the handoff outcome - the
    /// agent's own result.json is the record of what happened next.
    /// </summary>
    private static void HandDeniedToAgent(
        RolloutPlan plan,
        List<(ApplyStep Step, ProjectDeployResult Project, TargetDeployResult Result)> denied,
        string stagingRoot,
        int keepRuns,
        DeployReport report)
    {
        Console.WriteLine("\n-- Access denied -----------------------------------------------");
        Console.WriteLine($"  ✗ {denied.Count} target(s) failed with access denied while running as {AccessDenied.CurrentIdentity()}:");
        foreach (var (step, _, result) in denied)
            Console.WriteLine($"      {step.Project,-22} {result.TargetLabel}");
        Console.WriteLine();
        Console.WriteLine("    Controlling IIS, stopping services and writing under protected paths need a local");
        Console.WriteLine("    Administrator. Either run the CI runner service as one (or as SYSTEM), or install the");
        Console.WriteLine("    agent on this box (dytools-deploy install-agent) and give this host's servers[] entry");
        Console.WriteLine("    an incomingShare, so these steps can be handed to it - it runs as SYSTEM.");

        if (!plan.SelfCanHandOff)
        {
            Console.WriteLine($"\n  No incomingShare on this host's servers[] entry ('{plan.Self.ServerName}') -- nothing to hand off to.");
            return;
        }

        var steps   = denied.Select(d => d.Step).ToList();
        var shipped = SelfViaAgent.Ship(plan, steps, stagingRoot, keepRuns, report);
        var error   = report.Propagation.Last().ErrorMessage;

        foreach (var (step, project, result) in denied)
        {
            var index = project.TargetResults.IndexOf(result);
            project.TargetResults[index] = SelfViaAgent.HandedOff(step, shipped, error);
        }
    }

    // -- Pre-build -------------------------------------------------------------

    private static async Task<bool> RunPreBuildAsync(DiscoveredProject project, ProjectDeployResult result)
    {
        var preBuild    = project.Config?.PreBuild;
        var packageJson = Path.Combine(project.Folder, "package.json");

        if (preBuild is { Count: > 0 })
        {
            foreach (var command in preBuild)
            {
                var parts      = command.Split(' ', 2, StringSplitOptions.TrimEntries);
                var stepResult = await ProcessRunner.RunAsync(
                    $"preBuild: {command}", parts[0],
                    parts.Length > 1 ? parts[1] : string.Empty,
                    workingDirectory: project.Folder);
                result.PreBuildResults.Add(stepResult);

                if (!stepResult.Success)
                {
                    result.TargetResults.Add(new TargetDeployResult
                    {
                        TargetLabel = "Pre-build", Success = false,
                        ErrorMessage = $"Pre-build command failed: {command}"
                    });
                    Console.WriteLine($"  ✗ Pre-build failed on: {command}");
                    
                    var path = Environment.GetEnvironmentVariable("PATH") ?? "(not set)";
                    Console.WriteLine($"  [DEBUG] PATH at time of failure:");
                    foreach (var entry in path.Split(';'))
                        Console.WriteLine($"  [DEBUG]   {entry}");
                    
                    return false;
                }
            }
        }
        else if (File.Exists(packageJson))
        {
            Console.WriteLine("  [preBuild] package.json found -- running npm install.");
            var npmResult = await ProcessRunner.RunAsync("npm install", "npm", "install", project.Folder);
            result.PreBuildResults.Add(npmResult);

            if (!npmResult.Success)
            {
                result.TargetResults.Add(new TargetDeployResult
                {
                    TargetLabel = "Pre-build", Success = false,
                    ErrorMessage = "Default npm install failed."
                });
                return false;
            }
        }
        else
        {
            Console.WriteLine("  [preBuild] No pre-build steps required.");
        }

        return true;
    }

    // -- Unit tests ------------------------------------------------------------

    private static async Task<bool> RunTestsAsync(
        DiscoveredProject project,
        ProjectRegistry registry,
        ProjectDeployResult result,
        bool skipTests)
    {
        // Checked before runTests, and recorded rather than merely printed: an ungated deploy
        // is exactly the thing someone reads result.json afterwards to find out about.
        if (skipTests)
        {
            result.TestResults.Add(ProcessRunner.Synthetic(
                "unit tests", "Skipped by skiptests directive / --skip-tests."));
            Console.WriteLine("  [Tests] Skipped (skiptests directive).");
            return true;
        }

        if (project.Config is { RunTests: false })
        {
            Console.WriteLine("  [Tests] Skipped (runTests: false).");
            return true;
        }

        // Collect all test projects: this project + all transitive deps
        // Use a dict keyed by csproj path to avoid duplicates
        var testProjects = new Dictionary<string, DiscoveredProject>(StringComparer.OrdinalIgnoreCase);

        void AddIfHasTests(DiscoveredProject p)
        {
            if (p.UnitTestCsprojPath is not null && !testProjects.ContainsKey(p.UnitTestCsprojPath))
                testProjects[p.UnitTestCsprojPath] = p;
        }

        AddIfHasTests(project);
        foreach (var dep in project.TransitiveDependencies)
            AddIfHasTests(dep);

        if (testProjects.Count == 0)
        {
            Console.WriteLine("  [Tests] No test projects found -- skipping.");
            return true;
        }

        Console.WriteLine($"  [Tests] {testProjects.Count} test project(s) to evaluate:");
        foreach (var (csprojPath, owner) in testProjects)
            Console.WriteLine($"    {Path.GetFileNameWithoutExtension(csprojPath)} (for {owner.Name})");

        var allPassed = true;

        foreach (var (csprojPath, owner) in testProjects)
        {
            var testName = Path.GetFileNameWithoutExtension(csprojPath);

            // Already passed this run - skip
            if (registry.PassedTestProjects.Contains(csprojPath))
            {
                result.TestResults.Add(ProcessRunner.Synthetic(
                    $"test {testName}", "Already passed earlier in this run -- skipped."));
                Console.WriteLine($"  [Tests] ✓ {testName} (already passed -- skipped)");
                continue;
            }

            // Already failed this run - record and respect abort setting
            if (registry.FailedTestProjects.Contains(csprojPath))
            {
                var failStep = ProcessRunner.Synthetic(
                    $"test {testName}", "Already failed earlier in this run.", success: false);
                result.TestResults.Add(failStep);
                Console.WriteLine($"  [Tests] ✗ {testName} (already failed -- skipped)");

                if (owner.Config?.AbortOnUnitTestFailure ?? true)
                    allPassed = false;
                continue;
            }

            // Run the tests
            var noWarnArg  = !string.IsNullOrWhiteSpace(project.NoWarn)
                ? $" /nowarn:{project.NoWarn}" : string.Empty;
            // MsBuildSwitches matters here as much as on the build: dotnet test runs a full
            // build first, and without them it would spin up the very node-reuse and shared
            // compiler daemons the build path is careful to avoid.
            var testResult = await ProcessRunner.RunDotnetAsync(
                $"test {testName}",
                $"test \"{csprojPath}\" -c Release {ResourceGovernor.MsBuildSwitches} " +
                $"--logger \"console;verbosity=normal\"{noWarnArg}");
            result.TestResults.Add(testResult);

            if (testResult.Success)
            {
                registry.PassedTestProjects.Add(csprojPath);
                Console.WriteLine($"  [Tests] ✓ {testName}");
            }
            else
            {
                registry.FailedTestProjects.Add(csprojPath);
                Console.WriteLine($"  [Tests] ✗ {testName} FAILED");

                if (owner.Config?.AbortOnUnitTestFailure ?? true)
                    allPassed = false;
                else
                    Console.WriteLine($"  [Tests] abortOnUnitTestFailure=false for {owner.Name} -- continuing.");
            }
        }

        if (!allPassed)
        {
            result.TestsFailed = true;
            result.TargetResults.Add(new TargetDeployResult
            {
                TargetLabel  = "Tests",
                Success      = false,
                ErrorMessage = "One or more test projects failed. Deploy aborted."
            });
            Console.WriteLine("  ✗ Test failure -- deploy aborted for this project.");
        }

        return allPassed;
    }

    // -- Utilities -------------------------------------------------------------

    private static string GetCommitSha()
    {
        try
        {
            return ProcessRunner.RunAsync("git rev-parse", "git", "rev-parse HEAD")
                .GetAwaiter().GetResult().Stdout.Trim();
        }
        catch { return "unknown"; }
    }

    /// <summary>
    /// Full HEAD commit message (subject + body), the source of pub:/wait: directives.
    /// Returns empty on failure - a missing message simply means no directives.
    /// </summary>
    private static string GetCommitMessage()
    {
        try
        {
            return ProcessRunner.RunAsync("git log", "git", "log -1 --format=%B")
                .GetAwaiter().GetResult().Stdout.Trim();
        }
        catch { return string.Empty; }
    }

    private static void WriteReport(DeployReport report)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  DEPLOY SUMMARY");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Commit:    {report.CommitSha}");
        Console.WriteLine($"  Started:   {report.StartedAt:HH:mm:ss}");
        Console.WriteLine($"  Completed: {report.CompletedAt:HH:mm:ss}");
        Console.WriteLine($"  Duration:  {report.Duration?.TotalSeconds:F1}s");
        Console.WriteLine($"  Status:    {(report.Success ? "✓ SUCCESS" : "✗ FAILED")}");
        Console.WriteLine($"  Projects:  {report.SucceededProjects}/{report.TotalProjectsSelected} succeeded" +
            $", {report.SkippedProjects} skipped");

        if (report.ChangedFiles.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Changed files:");
            foreach (var f in report.ChangedFiles)
                Console.WriteLine($"    {f}");
        }

        Console.WriteLine();
        Console.WriteLine("  Results:");

        foreach (var proj in report.Results)
        {
            var projIcon = proj.Success ? "✓" : "✗";
            Console.WriteLine($"    {projIcon} {proj.ProjectName}");

            if (proj.PreBuildResults.Any(r => !r.Success))
                Console.WriteLine("        ✗ Pre-build failed");

            // Test results
            var failedTests = proj.TestResults.Where(r => !r.Success).ToList();
            var passedTests = proj.TestResults.Where(r => r.Success).ToList();
            if (proj.TestResults.Count > 0)
            {
                Console.WriteLine($"        Tests: {passedTests.Count} passed, {failedTests.Count} failed");
                foreach (var t in failedTests)
                    Console.WriteLine($"          ✗ {t.StepName}");
            }

            foreach (var p in proj.PublishResults.Where(p => !p.Success))
                Console.WriteLine($"        ✗ Publish failed: {p.Label}");

            foreach (var t in proj.TargetResults)
            {
                var tIcon    = t.Success ? "✓" : "✗";
                var rollback = t.RolledBack ? " [ROLLED BACK]" : string.Empty;
                var via      = t.AppliedBy is null ? string.Empty : $" [via {t.AppliedBy}]";
                Console.WriteLine($"        {tIcon} {t.TargetLabel}{rollback}{via}");

                if (!t.Success && t.ErrorMessage is not null)
                    Console.WriteLine($"           Error: {t.ErrorMessage}");

                foreach (var step in t.DeploySteps.Where(s => !s.Success))
                    Console.WriteLine($"           ✗ {step.StepName}: exit {step.ExitCode}");
            }
        }

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }
}