using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;

namespace Dytools.DeployTool.Wizard;

/// <summary>
/// Line-by-line menu editor over a DeployConfig. Each screen lists its fields with a letter key
/// to edit and drills into lists (projects, deployments, servers) by number. Returns true if the
/// user saved, false if they quit without saving. Removal is R# (D collides with field keys).
/// </summary>
internal static class ConfigEditor
{
    private static bool _eof;

    public static bool Edit(DeployConfig cfg)
    {
        _eof = false;
        while (true)
        {
            if (_eof) return false;

            Console.WriteLine();
            Console.WriteLine("Deploy Config");
            Console.WriteLine($"  F - Projects folder        : {cfg.ProjectsFolder}");
            Console.WriteLine($"  T - Tests folder           : {Show(cfg.UnitTestsFolder)}");
            Console.WriteLine($"  X - Test project suffix    : {Show(cfg.UnitTestProjectSuffix)}");
            Console.WriteLine($"  N - noWarn codes           : {Show(cfg.NoWarn)}");
            Console.WriteLine($"  O - Require pub: to deploy : {cfg.DoNotPublishIfNoPubInCommitMessage}");
            Console.WriteLine($"  R - Resource limits        : {ResourceSummary(cfg.Resources)}");
            Console.WriteLine($"  P - Projects               : {Names(cfg.Projects.Select(p => p.Name))}");
            Console.WriteLine($"  V - Servers                : {Names(cfg.Servers.Select(s => s.Name))}");
            Console.WriteLine("  S - Save and exit          Q - Quit without saving");

            switch (Key("Edit item"))
            {
                case "f": cfg.ProjectsFolder = Prompt.Ask("Projects folder", cfg.ProjectsFolder); break;
                case "t": cfg.UnitTestsFolder = Prompt.EditOptional("Tests folder", cfg.UnitTestsFolder); break;
                case "x": cfg.UnitTestProjectSuffix = Prompt.EditOptional("Test project suffix", cfg.UnitTestProjectSuffix); break;
                case "n": cfg.NoWarn = Prompt.EditOptional("noWarn codes", cfg.NoWarn); break;
                case "o": cfg.DoNotPublishIfNoPubInCommitMessage =
                    Prompt.Confirm("Require a pub: directive to deploy?", cfg.DoNotPublishIfNoPubInCommitMessage); break;
                case "r": EditResources(cfg); break;
                case "p": EditProjects(cfg); break;
                case "v": EditServers(cfg); break;
                case "s": return true;
                case "q": if (Prompt.Confirm("Quit without saving?", false)) return false; break;
            }
        }
    }

    // -- Projects -------------------------------------------------------------

    private static void EditProjects(DeployConfig cfg)
    {
        while (true)
        {
            if (_eof) return;

            Console.WriteLine();
            Console.WriteLine("Deploy Config > Projects");
            for (var i = 0; i < cfg.Projects.Count; i++)
                Console.WriteLine($"  {i + 1} - {cfg.Projects[i].Name}  " +
                    $"[{Names(cfg.Projects[i].Targets.Select(t => t.Type.ToString()))}]");
            Console.WriteLine("  A - Add project     R# - remove (e.g. R1)     B - Back");

            var (cmd, idx) = ReadList();
            switch (cmd)
            {
                case ListCmd.Back: return;
                case ListCmd.Add:
                    var np = new ProjectConfig { Name = Prompt.AskRequired("New project name"), Targets = [] };
                    cfg.Projects.Add(np);
                    EditProject(cfg, np);
                    break;
                case ListCmd.Remove: if (Valid(idx, cfg.Projects.Count)) cfg.Projects.RemoveAt(idx); break;
                case ListCmd.Open:   if (Valid(idx, cfg.Projects.Count)) EditProject(cfg, cfg.Projects[idx]); break;
            }
        }
    }

    private static void EditProject(DeployConfig cfg, ProjectConfig p)
    {
        while (true)
        {
            if (_eof) return;

            Console.WriteLine();
            Console.WriteLine($"Deploy Config > Project: {p.Name}");
            Console.WriteLine($"  N - Name                 : {p.Name}");
            Console.WriteLine($"  R - Run tests            : {p.RunTests}");
            Console.WriteLine($"  A - Abort on test fail   : {p.AbortOnUnitTestFailure}");
            Console.WriteLine($"  I - Disabled             : {p.Disabled}");
            Console.WriteLine($"  U - Explicit test project: {Show(p.UnitTestProject)}");
            Console.WriteLine($"      implicit by convention: {ImplicitTest(cfg, p)}");
            Console.WriteLine($"  D - Dependent projects   : {Names(p.DependentProjects)}");
            Console.WriteLine($"  T - Deployments          : {Deployments(p.Targets)}");
            Console.WriteLine("  B - Back");

            switch (Key("Edit item"))
            {
                case "n": p.Name = Prompt.Ask("Name", p.Name); break;
                case "r": p.RunTests = Prompt.Confirm("Run tests?", p.RunTests); break;
                case "a": p.AbortOnUnitTestFailure = Prompt.Confirm("Abort deploy on a test failure?", p.AbortOnUnitTestFailure); break;
                case "i": p.Disabled = Prompt.Confirm("Disabled (skip this project)?", p.Disabled); break;
                case "u": p.UnitTestProject = Prompt.EditOptional("Explicit test project (name or path)", p.UnitTestProject); break;
                case "d":
                    var next = CsvList("Dependent projects (comma-separated; * = solution-wide)", p.DependentProjects);
                    p.DependentProjects.Clear();
                    p.DependentProjects.AddRange(next);
                    break;
                case "t": EditDeployments(p); break;
                case "b": return;
            }
        }
    }

    // -- Deployments (targets) ------------------------------------------------

    private static void EditDeployments(ProjectConfig p)
    {
        while (true)
        {
            if (_eof) return;

            Console.WriteLine();
            Console.WriteLine($"Deploy Config > {p.Name} > Deployments");
            for (var i = 0; i < p.Targets.Count; i++)
                Console.WriteLine($"  {i + 1} - {TargetSummary(p.Targets[i])}");
            Console.WriteLine("  A - Add deployment     R# - remove     B - Back");

            var (cmd, idx) = ReadList();
            switch (cmd)
            {
                case ListCmd.Back: return;
                case ListCmd.Add:
                    var t = new TargetConfig { Build = new BuildConfig { Configuration = "Release", Runtime = "win-x64" } };
                    SetType(t, PickType());
                    p.Targets.Add(t);
                    EditDeployment(t);
                    break;
                case ListCmd.Remove: if (Valid(idx, p.Targets.Count)) p.Targets.RemoveAt(idx); break;
                case ListCmd.Open:   if (Valid(idx, p.Targets.Count)) EditDeployment(p.Targets[idx]); break;
            }
        }
    }

    private static void EditDeployment(TargetConfig t)
    {
        while (true)
        {
            if (_eof) return;
            EnsureType(t);

            Console.WriteLine();
            Console.WriteLine($"Deploy Config > Deployment: {t.Type}");
            Console.WriteLine($"  Y - Type                 : {t.Type}");
            Console.WriteLine($"  R - Build runtime (RID)  : {Show(t.Build?.Runtime)}");
            Console.WriteLine($"  K - Rollback on failure  : {t.Rollback}");
            switch (t.Type)
            {
                case DeployType.Iis:      PrintIis(t.Iis!); break;
                case DeployType.Folder:   PrintFolder(t.Folder!); break;
                case DeployType.Velopack: PrintVelopack(t.Velopack!); break;
            }
            Console.WriteLine("  B - Back");

            var key = Key("Edit item");
            switch (key)
            {
                case "b": return;
                case "y": SetType(t, PickType()); break;
                case "r": t.Build ??= new BuildConfig { Configuration = "Release" };
                          t.Build.Runtime = Prompt.EditOptional("Build runtime", t.Build.Runtime); break;
                case "k": t.Rollback = Prompt.Confirm("Roll back on failure?", t.Rollback); break;
                default:
                    switch (t.Type)
                    {
                        case DeployType.Iis:      EditIisField(t.Iis!, key); break;
                        case DeployType.Folder:   EditFolderField(t.Folder!, key); break;
                        case DeployType.Velopack: EditVelopackField(t.Velopack!, key); break;
                    }
                    break;
            }
        }
    }

    private static DeployType PickType() => Prompt.AskChoice("Deploy type",
    [
        (DeployType.Iis,      "IIS website"),
        (DeployType.Folder,   "Folder / Windows service"),
        (DeployType.Velopack, "Velopack release"),
    ]);

    private static void SetType(TargetConfig t, DeployType type)
    {
        t.Type     = type;
        t.Iis      = type == DeployType.Iis      ? t.Iis      ?? new IisConfig()      : null;
        t.Folder   = type == DeployType.Folder   ? t.Folder   ?? new FolderConfig()   : null;
        t.Velopack = type == DeployType.Velopack ? t.Velopack ?? new VelopackConfig() : null;
    }

    private static void EnsureType(TargetConfig t) => SetType(t, t.Type);

    private static void PrintIis(IisConfig i)
    {
        Console.WriteLine($"  1 - Site name            : {Show(i.SiteName)}");
        Console.WriteLine($"  2 - Deploy path (slot A) : {i.DeployPath}");
        Console.WriteLine($"  3 - Second slot (B)      : {Show(i.SecondaryDeployPath)}");
        Console.WriteLine($"  4 - App pool             : {i.AppPool}");
        Console.WriteLine($"  5 - Warmup URL           : {Show(i.WarmupUrl)}");
        Console.WriteLine($"  6 - Stop site too        : {i.StopSite}");
        Console.WriteLine($"  7 - Load balancer        : {LoadBalancerSummary(i.LoadBalancer)}");
    }

    private static void EditIisField(IisConfig i, string key)
    {
        switch (key)
        {
            case "1": i.SiteName = Prompt.EditOptional("Site name", i.SiteName); break;
            case "2": i.DeployPath = Prompt.Ask("Deploy path (slot A)", i.DeployPath); break;
            case "3": i.SecondaryDeployPath = Prompt.EditOptional("Second slot (B, enables blue-green)", i.SecondaryDeployPath); break;
            case "4": i.AppPool = Prompt.Ask("App pool", i.AppPool); break;
            case "5": i.WarmupUrl = Prompt.EditOptional("Warmup URL", i.WarmupUrl); break;
            case "6": i.StopSite = Prompt.Confirm("Stop the site as well as the app pool?", i.StopSite); break;
            case "7": EditLoadBalancer(i); break;
        }
    }

    // -- Load balancer --------------------------------------------------------

    private static void EditLoadBalancer(IisConfig i)
    {
        while (true)
        {
            if (_eof) return;

            Console.WriteLine();
            Console.WriteLine("Deploy Config > Deployment: Iis > Load balancer");

            if (i.LoadBalancer is null)
            {
                Console.WriteLine("  (none - this instance is never taken out of rotation)");
                Console.WriteLine("  A - Add load balancer block     B - Back");
                switch (Key("Edit item"))
                {
                    case "a": i.LoadBalancer = new LoadBalancerConfig(); break;
                    case "b" or "": return;
                }
                continue;
            }

            var lb = i.LoadBalancer;
            Console.WriteLine($"  1 - Precheck  : {PrecheckSummary(lb.Precheck)}");
            Console.WriteLine($"  2 - Drain     : {(lb.Drain is null ? "(none)" : PhaseSummary(lb.Drain))}");
            Console.WriteLine($"  3 - Restore   : {(lb.Restore is null ? "(none)" : PhaseSummary(lb.Restore))}");
            Console.WriteLine("  C - Remove the whole block     B - Back");

            switch (Key("Edit item"))
            {
                case "1": EditPrecheck(lb); break;
                // Suggested statuses differ by phase: drained instances report unhealthy,
                // restored ones report healthy again.
                case "2": lb.Drain   = EditPhase(lb.Drain,   "Drain",   500); break;
                case "3": lb.Restore = EditPhase(lb.Restore, "Restore", 200); break;
                case "c": if (Prompt.Confirm("Remove the load balancer block?", false)) i.LoadBalancer = null; break;
                case "b" or "": return;
            }
        }
    }

    private static void EditPrecheck(LoadBalancerConfig lb)
    {
        while (true)
        {
            if (_eof) return;

            Console.WriteLine();
            Console.WriteLine("Deploy Config > Deployment: Iis > Load balancer > Precheck");
            Console.WriteLine("  Decides whether the drain/restore phases apply at all. Without it they always");
            Console.WriteLine("  run - which fails on a box that is already offline, because the drain call");
            Console.WriteLine("  cannot be delivered. Anything but the live status (including no answer at");
            Console.WriteLine("  all) means 'already out of rotation' and both phases are skipped.");

            if (lb.Precheck is null)
            {
                Console.WriteLine("  (none - drain and restore always run)");
                Console.WriteLine("  A - Add precheck     B - Back");
                switch (Key("Edit item"))
                {
                    case "a": lb.Precheck = new LoadBalancerPrecheck(); break;
                    case "b" or "": return;
                }
                continue;
            }

            var p = lb.Precheck;
            Console.WriteLine($"  1 - URL (use localhost)  : {Show(p.Url)}");
            Console.WriteLine($"  2 - In-rotation status   : {p.LiveStatus}");
            Console.WriteLine($"  3 - Timeout              : {p.TimeoutSeconds}s");
            Console.WriteLine($"  4 - Interval             : {p.IntervalSeconds}s");
            Console.WriteLine("  C - Remove precheck     B - Back");

            switch (Key("Edit item"))
            {
                case "1": p.Url = Prompt.EditOptional($"Precheck URL {TokenHint}", p.Url); break;
                case "2": p.LiveStatus = Prompt.AskInt("Status meaning 'in rotation'", p.LiveStatus); break;
                case "3": p.TimeoutSeconds = Prompt.AskInt("Timeout (s)", p.TimeoutSeconds); break;
                case "4": p.IntervalSeconds = Prompt.AskInt("Interval (s)", p.IntervalSeconds); break;
                case "c": if (Prompt.Confirm("Remove the precheck?", false)) lb.Precheck = null; break;
                case "b" or "": return;
            }
        }
    }

    private static LoadBalancerPhase? EditPhase(LoadBalancerPhase? phase, string name, int suggestedStatus)
    {
        while (true)
        {
            if (_eof) return phase;

            Console.WriteLine();
            Console.WriteLine($"Deploy Config > Deployment: Iis > Load balancer > {name}");

            if (phase is null)
            {
                Console.WriteLine("  (none)");
                Console.WriteLine("  A - Add phase     B - Back");
                switch (Key("Edit item"))
                {
                    case "a": phase = new LoadBalancerPhase(); break;
                    case "b" or "": return null;
                }
                continue;
            }

            Console.WriteLine($"  1 - Notify           : {(phase.Notify is null ? "(none)" : NotifySummary(phase.Notify))}");
            Console.WriteLine($"  2 - Verify URL       : {Show(phase.VerifyUrl)}");
            Console.WriteLine($"  3 - Expect status    : {phase.ExpectStatus?.ToString() ?? "(none - verification skipped)"}");
            Console.WriteLine($"  4 - Verify timeout   : {phase.VerifyTimeoutSeconds}s");
            Console.WriteLine($"  5 - Verify interval  : {phase.VerifyIntervalSeconds}s");
            Console.WriteLine($"  6 - Wait seconds     : {phase.WaitSeconds}");
            if (name == "Drain" && phase.WaitSeconds == 0)
            {
                Console.WriteLine("      (a drain wait of 0 stops the site the instant the flag flips - the");
                Console.WriteLine("       balancer has not noticed yet, so live requests are dropped)");
            }
            Console.WriteLine("  C - Remove this phase     B - Back");

            switch (Key("Edit item"))
            {
                case "1": phase.Notify = EditNotify(phase.Notify, name); break;
                case "2": phase.VerifyUrl = Prompt.EditOptional($"Verify URL (localhost, not the VIP) {TokenHint}", phase.VerifyUrl); break;
                case "3": phase.ExpectStatus = EditOptionalInt("Expect status", phase.ExpectStatus, suggestedStatus); break;
                case "4": phase.VerifyTimeoutSeconds = Prompt.AskInt("Verify timeout (s)", phase.VerifyTimeoutSeconds); break;
                case "5": phase.VerifyIntervalSeconds = Prompt.AskInt("Verify interval (s)", phase.VerifyIntervalSeconds); break;
                case "6": phase.WaitSeconds = Prompt.AskInt("Wait seconds", phase.WaitSeconds); break;
                case "c": if (Prompt.Confirm($"Remove the {name.ToLowerInvariant()} phase?", false)) return null; break;
                case "b" or "": return phase;
            }
        }
    }

    private static NotifyHook? EditNotify(NotifyHook? hook, string phaseName)
    {
        while (true)
        {
            if (_eof) return hook;

            Console.WriteLine();
            Console.WriteLine($"Deploy Config > Deployment: Iis > Load balancer > {phaseName} > Notify");

            if (hook is null)
            {
                Console.WriteLine("  (none - the phase only verifies and/or waits)");
                Console.WriteLine("  A - Add notification     B - Back");
                switch (Key("Edit item"))
                {
                    case "a": hook = new NotifyHook { Type = PickNotifyType() }; break;
                    case "b" or "": return null;
                }
                continue;
            }

            Console.WriteLine($"  1 - Type            : {hook.Type}");
            switch (hook.Type)
            {
                case NotifyHookType.Http:
                    Console.WriteLine($"  2 - URL             : {Show(hook.Url)}");
                    Console.WriteLine($"  3 - Method          : {hook.Method}");
                    Console.WriteLine($"  4 - Body            : {Show(hook.Body)}");
                    Console.WriteLine($"  5 - Expect status   : {hook.ExpectStatus?.ToString() ?? "(any 2xx)"}");
                    break;
                case NotifyHookType.File:
                    Console.WriteLine($"  2 - Path            : {Show(hook.Path)}");
                    Console.WriteLine($"  3 - Action          : {hook.Action}");
                    break;
                case NotifyHookType.Command:
                    Console.WriteLine($"  2 - Executable      : {Show(hook.Executable)}");
                    Console.WriteLine($"  3 - Arguments       : {Show(hook.Arguments)}");
                    break;
            }
            Console.WriteLine($"  T - Timeout         : {hook.TimeoutSeconds}s");
            if (hook.Headers is { Count: > 0 })
                Console.WriteLine($"      Headers         : {hook.Headers.Count} set (edit these in the JSON)");
            Console.WriteLine("  C - Remove notification     B - Back");

            var key = Key("Edit item");
            switch (key)
            {
                case "1": hook.Type = PickNotifyType(); break;
                case "t": hook.TimeoutSeconds = Prompt.AskInt("Timeout (s)", hook.TimeoutSeconds); break;
                case "c": if (Prompt.Confirm("Remove the notification?", false)) return null; break;
                case "b" or "": return hook;
                default: EditNotifyTypeField(hook, key); break;
            }
        }
    }

    private static void EditNotifyTypeField(NotifyHook hook, string key)
    {
        switch (hook.Type)
        {
            case NotifyHookType.Http:
                switch (key)
                {
                    case "2": hook.Url = Prompt.EditOptional($"URL {TokenHint}", hook.Url); break;
                    case "3": hook.Method = Prompt.Ask("Method", hook.Method); break;
                    case "4": hook.Body = Prompt.EditOptional($"Body {TokenHint}", hook.Body); break;
                    case "5": hook.ExpectStatus = EditOptionalInt("Expect status", hook.ExpectStatus, 200); break;
                }
                break;

            case NotifyHookType.File:
                switch (key)
                {
                    case "2": hook.Path = Prompt.EditOptional($"Marker file path {TokenHint}", hook.Path); break;
                    case "3": hook.Action = Prompt.AskChoice("Action",
                        [(FileHookAction.Create, "Create the marker"), (FileHookAction.Delete, "Delete the marker")]); break;
                }
                break;

            case NotifyHookType.Command:
                switch (key)
                {
                    case "2": hook.Executable = Prompt.EditOptional($"Executable {TokenHint}", hook.Executable); break;
                    case "3": hook.Arguments = Prompt.EditOptional($"Arguments {TokenHint}", hook.Arguments); break;
                }
                break;
        }
    }

    private static NotifyHookType PickNotifyType() => Prompt.AskChoice("Notification type",
    [
        (NotifyHookType.Http,    "HTTP call (flip a health flag)"),
        (NotifyHookType.File,    "Create/delete a marker file the probe looks for"),
        (NotifyHookType.Command, "Run a command or script"),
    ]);

    private const string TokenHint = "({hostname}, {site}, {project}, %ENV_VAR%)";

    private static string LoadBalancerSummary(LoadBalancerConfig? lb)
    {
        if (lb is null) return "(none)";

        var parts = new List<string>();
        if (lb.Precheck is not null) parts.Add("precheck");
        if (lb.Drain    is not null) parts.Add($"drain {PhaseSummary(lb.Drain)}");
        if (lb.Restore  is not null) parts.Add($"restore {PhaseSummary(lb.Restore)}");
        return parts.Count == 0 ? "(block present but empty)" : string.Join("; ", parts);
    }

    private static string PrecheckSummary(LoadBalancerPrecheck? p)
        => p is null ? "(none - drain/restore always run)" : $"{Show(p.Url)} == {p.LiveStatus}";

    private static string PhaseSummary(LoadBalancerPhase p)
    {
        var bits = new List<string>();
        if (p.Notify is not null) bits.Add(NotifySummary(p.Notify));
        if (p is { VerifyUrl: not null, ExpectStatus: not null }) bits.Add($"verify {p.ExpectStatus}");
        if (p.WaitSeconds > 0) bits.Add($"wait {p.WaitSeconds}s");
        return bits.Count == 0 ? "(does nothing)" : string.Join(" + ", bits);
    }

    private static string NotifySummary(NotifyHook h) => h.Type switch
    {
        NotifyHookType.Http    => $"{h.Method.ToUpperInvariant()} {Show(h.Url)}",
        NotifyHookType.File    => $"{h.Action.ToString().ToLowerInvariant()} {Show(h.Path)}",
        NotifyHookType.Command => $"run {Show(h.Executable)}",
        _                      => h.Type.ToString(),
    };

    // -- Resource limits ------------------------------------------------------

    private static void EditResources(DeployConfig cfg)
    {
        while (true)
        {
            if (_eof) return;

            Console.WriteLine();
            Console.WriteLine("Deploy Config > Resource limits");
            Console.WriteLine("  Applied to this process at startup; priority and environment both inherit,");
            Console.WriteLine("  so every process the deploy spawns is covered - MSBuild, the compiler, tests.");

            if (cfg.Resources is null)
            {
                Console.WriteLine($"  (defaults: {ResourceSummary(null)})");
                Console.WriteLine("  A - Set explicitly     B - Back");
                switch (Key("Edit item"))
                {
                    case "a": cfg.Resources = new ResourceConfig(); break;
                    case "b" or "": return;
                }
                continue;
            }

            var r = cfg.Resources;
            Console.WriteLine($"  1 - Priority              : {r.Priority}");
            Console.WriteLine($"  2 - Build cores           : {(r.MaxCpuCount > 0 ? r.MaxCpuCount.ToString() : "unlimited")}");
            Console.WriteLine($"  3 - Disable build servers : {r.DisableBuildServers}");
            Console.WriteLine($"  4 - Workstation GC        : {r.WorkstationGc}");
            Console.WriteLine($"  5 - Background I/O (Win)  : {r.BackgroundIo}");
            Console.WriteLine("  C - Back to defaults     B - Back");

            switch (Key("Edit item"))
            {
                case "1": r.Priority = Prompt.AskChoice("Process priority",
                    [
                        (ProcessPriority.BelowNormal, "Below normal - yields to live apps, still makes progress"),
                        (ProcessPriority.Idle,        "Idle - spare cycles only, slowest and quietest"),
                        (ProcessPriority.Normal,      "Normal - competes with live apps"),
                    ]); break;
                case "2": r.MaxCpuCount = Prompt.AskInt("Build cores (0 = unlimited)", r.MaxCpuCount); break;
                case "3": r.DisableBuildServers = Prompt.Confirm(
                    "Disable MSBuild node reuse / server / shared compiler?", r.DisableBuildServers); break;
                case "4": r.WorkstationGc = Prompt.Confirm("Force workstation GC on spawned builds?", r.WorkstationGc); break;
                case "5": r.BackgroundIo = Prompt.Confirm(
                    "Background I/O mode? (Windows only, noticeably slower)", r.BackgroundIo); break;
                case "c": if (Prompt.Confirm("Drop back to defaults?", false)) cfg.Resources = null; break;
                case "b" or "": return;
            }
        }
    }

    private static string ResourceSummary(ResourceConfig? r)
    {
        r ??= new ResourceConfig();
        var cores = r.MaxCpuCount > 0 ? $"{r.MaxCpuCount} core(s)" : "all cores";
        var io    = r.BackgroundIo ? ", background i/o" : string.Empty;
        return $"{r.Priority}, {cores}{(r.DisableBuildServers ? ", no build servers" : string.Empty)}{io}";
    }

    /// <summary>Optional int with "-" to clear, so verification can be switched off from here.</summary>
    private static int? EditOptionalInt(string label, int? current, int suggested)
    {
        Console.Write($"{label} [{current?.ToString() ?? $"none, e.g. {suggested}"}] (blank=keep, - to clear): ");
        var line = Console.ReadLine();
        if (line is null) { _eof = true; return current; }
        if (line.Length == 0) return current;

        var s = line.Trim();
        if (s == "-") return null;
        if (int.TryParse(s, out var v)) return v;

        Console.WriteLine("  (enter a number, or - to clear)");
        return current;
    }

    private static void PrintFolder(FolderConfig f)
    {
        Console.WriteLine($"  1 - Destination path     : {f.DestinationPath}");
        Console.WriteLine($"  2 - Windows service      : {Show(f.ServiceName)}");
    }

    private static void EditFolderField(FolderConfig f, string key)
    {
        switch (key)
        {
            case "1": f.DestinationPath = Prompt.Ask("Destination path", f.DestinationPath); break;
            case "2": f.ServiceName = Prompt.EditOptional("Windows service", f.ServiceName); break;
        }
    }

    private static void PrintVelopack(VelopackConfig v)
    {
        Console.WriteLine($"  1 - packId               : {v.PackId}");
        Console.WriteLine($"  2 - Delivery             : {v.Delivery}");
        Console.WriteLine($"  3 - Source               : {(v.Source is null ? "(none)" : v.Source.Type.ToString())}");
    }

    private static void EditVelopackField(VelopackConfig v, string key)
    {
        switch (key)
        {
            case "1": v.PackId = Prompt.Ask("packId", v.PackId); break;
            case "2": v.Delivery = Prompt.AskChoice("Delivery",
                [ (VelopackDelivery.PackOnly, "Pack only"),
                  (VelopackDelivery.DownloadAndPack, "Download & pack"),
                  (VelopackDelivery.DownloadPackAndUpload, "Download, pack & upload") ]); break;
            case "3": EditVelopackSource(v); break;
        }
    }

    private static void EditVelopackSource(VelopackConfig v)
    {
        if (v.Source is null && !Prompt.Confirm("Add a release source/feed?", true)) return;
        v.Source ??= new VelopackSource();
        var s = v.Source;

        s.Type = Prompt.AskChoice("Source type",
        [
            (VelopackSourceType.Az, "Azure Blob"), (VelopackSourceType.S3, "Amazon S3"),
            (VelopackSourceType.GitHub, "GitHub"), (VelopackSourceType.Gitea, "Gitea"),
            (VelopackSourceType.Local, "Local folder"), (VelopackSourceType.Http, "HTTP (download only)"),
        ]);

        switch (s.Type)
        {
            case VelopackSourceType.Az:
                s.Account = Prompt.EditOptional("Account", s.Account);
                s.Container = Prompt.EditOptional("Container", s.Container);
                s.Key = Prompt.EditOptional("Access key (e.g. %AZ_KEY%)", s.Key); break;
            case VelopackSourceType.S3:
                s.Bucket = Prompt.EditOptional("Bucket", s.Bucket);
                s.Region = Prompt.EditOptional("Region", s.Region); break;
            case VelopackSourceType.GitHub or VelopackSourceType.Gitea:
                s.RepoUrl = Prompt.EditOptional("Repo URL", s.RepoUrl);
                s.Token = Prompt.EditOptional("Token (e.g. %GH_TOKEN%)", s.Token); break;
            case VelopackSourceType.Local:
                s.Path = Prompt.EditOptional("Path", s.Path); break;
            case VelopackSourceType.Http:
                s.Url = Prompt.EditOptional("URL", s.Url); break;
        }
    }

    // -- Servers --------------------------------------------------------------

    private static void EditServers(DeployConfig cfg)
    {
        while (true)
        {
            if (_eof) return;

            // Which entry is the box being edited on. Shown because the commonest fleet
            // mistake is a hostname that matches nothing: such a box deploys itself fine and
            // propagates to nobody, and nothing about a green run says so.
            var self = FindSelf(cfg);

            Console.WriteLine();
            Console.WriteLine("Deploy Config > Servers");
            for (var i = 0; i < cfg.Servers.Count; i++)
            {
                var here = ReferenceEquals(cfg.Servers[i], self) ? "   <- this box" : string.Empty;
                Console.WriteLine($"  {i + 1} - {cfg.Servers[i].Name}  ({cfg.Servers[i].Hostname}){here}");
            }

            if (cfg.Servers.Count > 0 && self is null)
                Console.WriteLine($"      (nothing here matches this box, '{Environment.MachineName}')");
            Console.WriteLine($"  D - Rollout               : {(cfg.Rollout is { } r ? $"{r.DelaySeconds}s soak, keep {r.KeepRuns}" : "(defaults)")}");
            Console.WriteLine("  A - Add server     R# - remove     B - Back");

            var raw = Key("Item");
            if (raw == "d") { EditRollout(cfg); continue; }

            var (cmd, idx) = Parse(raw);
            switch (cmd)
            {
                case ListCmd.Back: return;
                case ListCmd.Add:
                    cfg.Servers.Add(new ServerConfig
                    {
                        Name = Prompt.AskRequired("Server label"),
                        // Offered as the default only while no entry claims this box - otherwise
                        // the second server added would quietly duplicate the first's hostname.
                        Hostname = self is null
                            ? Prompt.Ask("Hostname", Environment.MachineName)
                            : Prompt.AskRequired("Hostname"),
                        IncomingShare = Prompt.AskOptional("Incoming share (peers only)"),
                    });
                    break;
                case ListCmd.Remove: if (Valid(idx, cfg.Servers.Count)) cfg.Servers.RemoveAt(idx); break;
                case ListCmd.Open:   if (Valid(idx, cfg.Servers.Count)) EditServer(cfg.Servers[idx]); break;
            }
        }
    }

    private static void EditServer(ServerConfig s)
    {
        while (true)
        {
            if (_eof) return;

            Console.WriteLine();
            Console.WriteLine($"Deploy Config > Server: {s.Name}");
            Console.WriteLine($"  N - Name             : {s.Name}");
            Console.WriteLine($"  H - Hostname         : {s.Hostname}");
            Console.WriteLine($"  I - Incoming share   : {Show(s.IncomingShare)}");
            Console.WriteLine($"  U - Share username   : {Show(s.Username)}");
            Console.WriteLine($"  P - Share password   : {Mask(s.Password)}");
            Console.WriteLine("  B - Back");

            switch (Key("Edit item"))
            {
                case "n": s.Name = Prompt.Ask("Name", s.Name); break;
                case "h": s.Hostname = Prompt.Ask("Hostname", s.Hostname); break;
                case "i": s.IncomingShare = Prompt.EditOptional("Incoming share", s.IncomingShare); break;
                case "u":
                    Console.WriteLine("  Local account ON THE PEER, e.g. WEB02\\deploysvc. Blank = connect as");
                    Console.WriteLine("  whoever the deploy already runs as (domain, or mirrored local accounts).");
                    s.Username = Prompt.EditOptional("Share username", s.Username);
                    break;
                case "p":
                    Console.WriteLine("  Use an environment reference, not the password itself - this file is");
                    Console.WriteLine("  in your repository. For example: %DEPLOY_SHARE_PASSWORD%");
                    s.Password = Prompt.EditOptional("Share password", s.Password);
                    break;
                case "b": return;
            }
        }
    }

    /// <summary>
    /// The entry this box is, or null. Ambiguity is not the wizard's problem to solve - it will
    /// stop a real deploy, and showing no marker is a truthful way to render it here.
    /// </summary>
    /// <summary>
    /// Renders a configured password without printing it. An environment reference is shown
    /// as written - that is the whole point of using one, and seeing which variable is wired
    /// up is exactly what someone is here to check.
    /// </summary>
    private static string Mask(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return "(none)";

        return password.Contains('%') || password.Contains('$')
            ? password
            : "******  (literal - use %ENV_VAR% instead)";
    }

    private static ServerConfig? FindSelf(DeployConfig cfg)
    {
        try { return HostIdentity.Find(cfg.Servers, HostIdentity.Resolve())?.Server; }
        catch { return null; }
    }

    private static void EditRollout(DeployConfig cfg)
    {
        cfg.Rollout ??= new RolloutConfig();
        cfg.Rollout.DelaySeconds = Prompt.AskInt("Soak delay before peers apply (seconds)", cfg.Rollout.DelaySeconds);
        cfg.Rollout.KeepRuns = Prompt.AskInt("Runs to keep per server", cfg.Rollout.KeepRuns);
    }

    // -- Shared list handling & display helpers -------------------------------

    private enum ListCmd { None, Back, Add, Open, Remove }

    private static (ListCmd, int) ReadList() => Parse(Key("Item (number, A=add, R#=remove, B=back)"));

    private static (ListCmd cmd, int index) Parse(string s)
    {
        if (s is "" or "b") return (ListCmd.Back, -1);
        if (s == "a") return (ListCmd.Add, -1);
        if (s.Length > 0 && s[0] == 'r')
        {
            var rest = s[1..];
            if (rest.Length == 0) return (ListCmd.Remove, Prompt.AskInt("Remove which number", 0) - 1);
            if (int.TryParse(rest, out var ri)) return (ListCmd.Remove, ri - 1);
        }
        if (int.TryParse(s, out var i)) return (ListCmd.Open, i - 1);
        return (ListCmd.None, -1);
    }

    private static bool Valid(int idx, int count)
    {
        if (idx >= 0 && idx < count) return true;
        Console.WriteLine("  (no such number)");
        return false;
    }

    private static List<string> CsvList(string label, IEnumerable<string> current)
    {
        var cur = string.Join(", ", current);
        Console.Write($"{label} [{cur}] (blank=keep, - to clear): ");
        var s = Console.ReadLine();
        if (string.IsNullOrEmpty(s)) return current.ToList();
        s = s.Trim();
        return s == "-"
            ? []
            : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string Key(string label)
    {
        Console.Write($"{label}: ");
        var line = Console.ReadLine();
        if (line is null) { _eof = true; return ""; }
        return line.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// What the tool would resolve for this project's test by convention (read-only display):
    /// {testsFolder}/{name}{suffix}/{name}{suffix}.csproj. An explicit test above overrides it.
    /// </summary>
    private static string ImplicitTest(DeployConfig cfg, ProjectConfig p)
    {
        if (!p.RunTests) return "(tests off)";
        if (string.IsNullOrWhiteSpace(cfg.UnitTestsFolder) || string.IsNullOrWhiteSpace(cfg.UnitTestProjectSuffix))
            return "(needs a tests folder + suffix)";

        var name = p.Name + cfg.UnitTestProjectSuffix;
        var path = $"{cfg.UnitTestsFolder}/{name}/{name}.csproj";
        return p.UnitTestProject is null ? path : $"{path}  (overridden by explicit above)";
    }

    /// <summary>One deployment as "type (runtime -> destination)" so settings are visible at a glance.</summary>
    private static string TargetSummary(TargetConfig t)
    {
        var rid = string.IsNullOrEmpty(t.Build?.Runtime) ? "default rid" : t.Build!.Runtime;
        var dest = t.Type switch
        {
            DeployType.Iis      => t.Iis?.DeployPath,
            DeployType.Folder   => t.Folder?.DestinationPath,
            DeployType.Velopack => string.IsNullOrEmpty(t.Velopack?.PackId) ? null : $"packId {t.Velopack!.PackId}",
            _                   => null,
        };
        return string.IsNullOrEmpty(dest) ? $"{t.Type} ({rid})" : $"{t.Type} ({rid} -> {dest})";
    }

    private static string Deployments(IEnumerable<TargetConfig> targets)
    {
        var list = targets.ToList();
        return list.Count == 0 ? "(none)" : string.Join(", ", list.Select(TargetSummary));
    }

    private static string Show(string? s) => string.IsNullOrEmpty(s) ? "(none)" : s;

    private static string Names(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "(none)" : string.Join(", ", list);
    }
}
