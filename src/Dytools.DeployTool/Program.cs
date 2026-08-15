using System.Diagnostics;
using System.Text.Json;
using Dytools.DeployTool;
using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Plan;
using Dytools.DeployTool.Models.Reporting;
using Dytools.DeployTool.Resolvers;
using Dytools.DeployTool.Services;
using Dytools.DeployTool.Wizard;

internal class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Lower this process's priority immediately so the deploy pipeline doesn't compete
        // with live application processes on the production server. ProcessRunner applies the
        // same priority to every child process spawned during the build.
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;

        // `init` (alias --makeconfig) scaffolds/edits a deploy-config.json; `edit <file>` opens an
        // existing one. Checked before the --config requirement below so they run in a fresh repo.
        if (args.Length > 0 && args[0] is "init" or "--init" or "--makeconfig")
            return ConfigWizard.RunInit(args[1..]);
        if (args.Length > 0 && args[0] == "edit")
            return ConfigWizard.RunEdit(args[1..]);

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
        var serverName = Environment.MachineName;

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
        Console.WriteLine($"  Server: {serverName}");

        // -- Directives --------------------------------------------------------
        // Base directives come from the HEAD commit message; --pub/--wait on the command
        // line override them field by field (for hand/test runs).

        var commitMessage    = GetCommitMessage();
        var commitDirectives = CommitDirectives.Parse(commitMessage);

        var cliOverrides = CommitDirectives.FromValues(
            parsedArgs.GetOptional("pub"),
            parsedArgs.GetOptionalInt("wait"));
        var directives = commitDirectives.OverlaidWith(cliOverrides);

        var subject = commitMessage.Split('\n').FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(subject))
            Console.WriteLine($"  Message: {subject}");
        if (cliOverrides.HasPub || cliOverrides.WaitSeconds.HasValue)
            Console.WriteLine("  (directives overridden from command line)");
        if (directives.HasPub)
            Console.WriteLine($"  Directive pub:  {string.Join(" | ", directives.PubPatterns!)}");
        if (directives.WaitSeconds.HasValue)
            Console.WriteLine($"  Directive wait: {directives.WaitSeconds.Value}s");

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

        var handlers = new Dictionary<DeployType, IDeployTypeHandler>
        {
            [DeployType.Velopack] = new VelopackHandler(),
            [DeployType.Iis]      = new IisHandler(),
            [DeployType.Folder]   = new FolderHandler()
        };

        // -- Plan --------------------------------------------------------------
        // The entire rollout -- every server, every step -- decided here in one shot from
        // one set of inputs, so peer plans are never recomputed later or on the peer itself.

        var plan = Planner.Plan(
            runId, commitSha, projectsToDeploy, deployConfig, handlers,
            serverName, directives, selection.Reason);

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

        var stagingRoot = Path.Combine(Path.GetTempPath(), "DeployTool", "staging", runId);
        Directory.CreateDirectory(stagingRoot);

        try
        {
            // -- Deploy each project (sequential) ---------------------------------

            foreach (var project in projectsToDeploy)
            {
                Console.WriteLine();
                Console.WriteLine($"══ Project: {project.Name} ══════════════════════════════════════");

                var projectResult = await DeployProjectAsync(
                    project, plan, handlers, registry, stagingRoot);
                report.Results.Add(projectResult);
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

    // -- Plan output -----------------------------------------------------------

    private static void PrintPlan(RolloutPlan plan)
    {
        foreach (var server in plan.ServerPlans)
        {
            var role = server.IsSelf ? "self, primary" : "peer";
            Console.WriteLine($"  {server.ServerName}  [{role}]  {server.Steps.Count} step(s)");
            foreach (var step in server.Steps)
                Console.WriteLine($"      {step.Project,-22} {step.Label}");
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
        string stagingRoot)
    {
        var result = new ProjectDeployResult { ProjectName = project.Name };

        // -- Pre-build ---------------------------------------------------------
        Console.WriteLine("  -- Pre-build --------------------------------------------");
        if (!await RunPreBuildAsync(project, result)) return result;

        // -- Unit tests --------------------------------------------------------
        Console.WriteLine("  -- Unit Tests -------------------------------------------");
        if (!await RunTestsAsync(project, registry, result)) return result;

        // -- Targets -----------------------------------------------------------
        // Sourced from the plan, not re-derived from config: the plan already decided what
        // this box does, and the same ApplyStep objects are what peers receive.
        foreach (var targetPlan in plan.Targets.Where(t => t.ProjectName == project.Name))
        {
            var target = targetPlan.Target;
            var label  = $"{target.Type} / {target.Build?.Runtime ?? "default-rid"}";
            Console.WriteLine($"\n  -- Target: {label} ----------------------------------");

            var artifactDir = Path.Combine(stagingRoot, targetPlan.ArtifactRelativePath);
            Directory.CreateDirectory(artifactDir);

            // -- Publish -------------------------------------------------------
            // Identical for every deploy type, so it lives here rather than in a handler.
            var publishResult = await BuildHelper.PublishAsync(
                project.Name, project.Folder, target.Build, artifactDir, project.NoWarn);

            if (!publishResult.Success)
            {
                result.TargetResults.Add(new TargetDeployResult
                {
                    TargetLabel   = targetPlan.Step.Label,
                    Type          = target.Type,
                    Success       = false,
                    PublishResult = publishResult,
                    ErrorMessage  = "dotnet publish failed."
                });
                Console.WriteLine("  ✗ dotnet publish failed.");
                continue;
            }

            // -- Apply ---------------------------------------------------------
            // The step's artifact path is stored relative so the same object can travel to
            // a peer; rebasing resolves it against this box's staging folder.
            var targetResult = await handlers[target.Type]
                .ApplyAsync(targetPlan.Step.RebasedTo(stagingRoot));
            targetResult.PublishResult = publishResult;
            result.TargetResults.Add(targetResult);

            if (!targetResult.Success)
                Console.WriteLine($"  ✗ {targetResult.ErrorMessage}");
            else
                Console.WriteLine($"  ✓ Target complete in " +
                    $"{targetResult.PublishResult?.Duration?.TotalSeconds:F1}s publish + " +
                    $"{targetResult.DeploySteps.Sum(s => s.Duration?.TotalSeconds ?? 0):F1}s deploy.");
        }

        return result;
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
        ProjectDeployResult result)
    {
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
            var testResult = await ProcessRunner.RunDotnetAsync(
                $"test {testName}",
                $"test \"{csprojPath}\" -c Release --logger \"console;verbosity=normal\"{noWarnArg}");
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

            foreach (var t in proj.TargetResults)
            {
                var tIcon    = t.Success ? "✓" : "✗";
                var rollback = t.RolledBack ? " [ROLLED BACK]" : string.Empty;
                Console.WriteLine($"        {tIcon} {t.TargetLabel}{rollback}");

                if (!t.Success && t.ErrorMessage is not null)
                    Console.WriteLine($"           Error: {t.ErrorMessage}");

                foreach (var step in t.DeploySteps.Where(s => !s.Success))
                    Console.WriteLine($"           ✗ {step.StepName}: exit {step.ExitCode}");
            }
        }

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
    }
}