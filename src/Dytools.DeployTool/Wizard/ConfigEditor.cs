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
        }
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

            Console.WriteLine();
            Console.WriteLine("Deploy Config > Servers");
            for (var i = 0; i < cfg.Servers.Count; i++)
                Console.WriteLine($"  {i + 1} - {cfg.Servers[i].Name}  ({cfg.Servers[i].Hostname})");
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
                        Hostname = Prompt.AskRequired("Hostname"),
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
            Console.WriteLine("  B - Back");

            switch (Key("Edit item"))
            {
                case "n": s.Name = Prompt.Ask("Name", s.Name); break;
                case "h": s.Hostname = Prompt.Ask("Hostname", s.Hostname); break;
                case "i": s.IncomingShare = Prompt.EditOptional("Incoming share", s.IncomingShare); break;
                case "b": return;
            }
        }
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
