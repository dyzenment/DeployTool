using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Scanning;

namespace Dytools.DeployTool.Wizard;

/// <summary>
/// `dytools-deploy init` and `edit`. init seeds a config (scan a solution, or blank) and opens the
/// editor; --save writes the seed straight out without the editor. edit loads an existing config
/// into the editor. Everything is built from the real DeployConfig models, so output can't drift.
/// </summary>
public static class ConfigWizard
{
    private const string SchemaUrl =
        "https://raw.githubusercontent.com/dyzenment/DeployTool/main/deploy-config.schema.json";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    // -- init -----------------------------------------------------------------

    public static int RunInit(string[] args)
    {
        var sample      = args.Contains("--sample");
        var glob        = args.Contains("--all-csproj");
        var save        = args.Contains("--save");
        var solutionArg = ValueOf(args, "--solution");
        // --solution (and --all-csproj) imply scan mode on their own - no need for --auto too.
        var scan        = args.Any(a => a is "--scan" or "--auto") || solutionArg is not null;
        var outPath     = SaveValue(args) ?? Positional(args) ?? "deploy-config.json";

        DeployConfig config;
        if (sample)
        {
            config = BuildSample();
        }
        else if (scan || glob)
        {
            var result = ScanForConfig(glob, solutionArg);
            if (result is null) return 1;               // no solution found
            config = ConfigScaffolder.FromScan(result);
            PrintScanSummary(result, config);
        }
        else
        {
            // Plain `init`: offer to pre-fill from the solution, else start from a blank config.
            var result = Prompt.Confirm("Scan your solution to pre-fill the config?", true)
                ? ScanForConfig(glob: false, solutionArg: null)
                : null;
            if (result is not null)
            {
                config = ConfigScaffolder.FromScan(result);
                PrintScanSummary(result, config);
            }
            else
            {
                config = new DeployConfig { ProjectsFolder = "src" };
            }
        }

        // --save (and the machine-only --sample) skip the editor and write the seed as-is.
        if (!sample && !save)
        {
            if (!ConfigEditor.Edit(config))
            {
                Console.WriteLine("Quit - nothing written.");
                return 0;
            }
        }

        WriteConfig(config, outPath);
        Console.WriteLine($"\nWrote {Path.GetFullPath(outPath)}");
        return 0;
    }

    // -- edit -----------------------------------------------------------------

    public static int RunEdit(string[] args)
    {
        var path = Positional(args);
        if (path is null)
        {
            Console.WriteLine("Usage: dytools-deploy edit <deploy-config.json>");
            return 1;
        }
        if (!File.Exists(path))
        {
            Console.WriteLine($"File not found: {path}");
            return 1;
        }

        DeployConfig config;
        try
        {
            config = JsonSerializer.Deserialize<DeployConfig>(File.ReadAllText(path), ReadOptions)
                     ?? new DeployConfig();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not read {path}: {e.Message}");
            return 1;
        }

        if (!ConfigEditor.Edit(config))
        {
            Console.WriteLine("Quit - no changes written.");
            return 0;
        }

        WriteConfig(config, path);
        Console.WriteLine($"\nWrote {Path.GetFullPath(path)}");
        return 0;
    }

    // -- Scan mode ------------------------------------------------------------

    private static ScanResult? ScanForConfig(bool glob, string? solutionArg)
    {
        var cwd = Directory.GetCurrentDirectory();

        if (glob)
        {
            Console.WriteLine($"Scanning every .csproj under {cwd} …");
            return SolutionScanner.ScanAllCsproj(cwd);
        }

        string solution;
        if (solutionArg is not null)
        {
            if (!File.Exists(solutionArg))
            {
                Console.WriteLine($"Solution not found: {solutionArg}");
                return null;
            }
            solution = solutionArg;
        }
        else
        {
            var found = SolutionScanner.FindSolutions(cwd);
            if (found.Count == 0)
            {
                Console.WriteLine(
                    $"No .sln or .slnx found in {cwd}.\n" +
                    "Run from your solution folder, pass --solution <file>, or use --all-csproj.");
                return null;
            }
            solution = found.Count == 1
                ? found[0]
                : Prompt.AskChoice("Multiple solutions found - pick one:",
                    found.Select(f => (f, Path.GetFileName(f))).ToArray());
        }

        Console.WriteLine($"Scanning {Path.GetFileName(solution)} …");
        return SolutionScanner.ScanSolution(solution);
    }

    private static void PrintScanSummary(ScanResult scan, DeployConfig config)
    {
        var targetByName = config.Projects.ToDictionary(
            p => p.Name, p => p.Targets[0].Type, StringComparer.OrdinalIgnoreCase);
        var productNames = scan.Projects.Where(p => !p.IsTest).Select(p => p.Name).ToList();

        string? PrimaryOf(string testName) => productNames
            .Where(n => testName.StartsWith(n, StringComparison.OrdinalIgnoreCase) && testName.Length > n.Length)
            .OrderByDescending(n => n.Length)
            .FirstOrDefault();

        Console.WriteLine($"\nFound {scan.Projects.Count} project(s):");
        foreach (var p in scan.Projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var note = p.Kind switch
            {
                ProjectKind.Web or ProjectKind.Console or ProjectKind.Desktop
                    => $"-> deploy: {targetByName.GetValueOrDefault(p.Name)}",
                ProjectKind.Test
                    => PrimaryOf(p.Name) is { } pr ? $"-> tests: {pr}" : "-> tests: (no match)",
                ProjectKind.ClassLibrary => "(dependency, not deployed)",
                _ => "(unreadable csproj)",
            };
            Console.WriteLine($"  {p.Name,-28} {p.Kind,-13} {note}");
        }

        Console.Write($"\nInferred: projectsFolder \"{config.ProjectsFolder}\"");
        if (config.UnitTestsFolder is { } tf) Console.Write($", testsFolder \"{tf}\"");
        if (config.UnitTestProjectSuffix is { } sx) Console.Write($", test suffix \"{sx}\"");
        Console.WriteLine();
        Console.WriteLine(
            $"{config.Projects.Count} deployable, " +
            $"{scan.Projects.Count(p => p.Kind == ProjectKind.ClassLibrary)} library, " +
            $"{scan.Projects.Count(p => p.IsTest)} test.");
        Console.WriteLine("Deploy destinations (paths, app pools, servers) are placeholders - edit before deploying.");
    }

    // -- Serialization - real models + a prepended $schema key ----------------

    private static void WriteConfig(DeployConfig config, string path)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented          = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var body = JsonSerializer.SerializeToNode(config, opts)!.AsObject();

        var root = new JsonObject { ["$schema"] = SchemaUrl };
        foreach (var key in body.Select(kv => kv.Key).ToList())
        {
            var value = body[key];
            body.Remove(key);
            root[key] = value;
        }

        File.WriteAllText(path, root.ToJsonString(opts) + "\n");
    }

    // -- Sample (non-interactive) - exercises most of the model for smoke tests / demos --------

    private static DeployConfig BuildSample() => new()
    {
        ProjectsFolder                     = "src",
        UnitTestsFolder                    = "tests",
        UnitTestProjectSuffix              = "UnitTest",
        NoWarn                             = "CS8600,CS8618",
        DoNotPublishIfNoPubInCommitMessage = true,
        Servers =
        [
            new ServerConfig { Name = "web01", Hostname = "WEBSERVER01" },
            new ServerConfig { Name = "web02", Hostname = "WEBSERVER02", IncomingShare = @"\\WEBSERVER02\deploy\incoming" },
        ],
        Rollout  = new RolloutConfig { DelaySeconds = 600, KeepRuns = 5 },
        Projects =
        [
            new ProjectConfig
            {
                Name = "WebApp",
                DependentProjects = ["Core", "*"],
                Targets =
                [
                    new TargetConfig
                    {
                        Type = DeployType.Iis,
                        Rollback = true,
                        Build = new BuildConfig { Configuration = "Release", Runtime = "win-x64" },
                        Iis = new IisConfig
                        {
                            SiteName = "MyWeb",
                            DeployPath = @"C:\inetpub\MyWeb_A",
                            SecondaryDeployPath = @"C:\inetpub\MyWeb_B",
                            AppPool = "MyWebPool",
                            WarmupUrl = "http://localhost/health",
                        },
                    },
                ],
            },
            new ProjectConfig
            {
                Name = "Desktop",
                RunTests = false,
                Targets =
                [
                    new TargetConfig
                    {
                        Type = DeployType.Velopack,
                        Prerequisites = ["vpk"],
                        Build = new BuildConfig { Configuration = "Release", Runtime = "win-x64" },
                        Velopack = new VelopackConfig
                        {
                            PackId = "MyApp",
                            Delivery = VelopackDelivery.DownloadPackAndUpload,
                            Source = new VelopackSource
                            {
                                Type = VelopackSourceType.Az,
                                Account = "%AZ_ACCOUNT%",
                                Key = "%AZ_KEY%",
                                Container = "releases",
                                KeepMaxReleases = 10,
                            },
                        },
                    },
                ],
            },
        ],
    };

    // -- Arg helpers ----------------------------------------------------------

    private static string? ValueOf(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
    }

    private static string? SaveValue(string[] args)
    {
        var i = Array.IndexOf(args, "--save");
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
    }

    private static string? Positional(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (args[i] is "--solution" or "--save" && i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    i++;   // skip its value
                continue;
            }
            return args[i];
        }
        return null;
    }
}
