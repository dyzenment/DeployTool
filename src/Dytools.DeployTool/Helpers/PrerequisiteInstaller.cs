using System.Net.Http;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Installs prerequisites listed in deploy-config.json that are not handled by the YAML.
///
/// Responsibility split:
///   YAML-managed  : git, net9, net10 (via actions/setup-dotnet@v4)
///   Tool-managed  : nodejs, vpk, and anything added in the future
///
/// Each installer checks whether the tool is already present before attempting
/// installation, so this is safe to run on every deploy - no unnecessary installs.
///
/// To add a new prerequisite type:
///   1. Add a string constant to KnownPrerequisites
///   2. Add an install method following the pattern below
///   3. Add a case to the switch in InstallAsync
/// </summary>
public static class PrerequisiteInstaller
{
    /// <summary>
    /// Prerequisites managed by the GitHub Actions YAML.
    /// Skipped here to avoid duplicate or conflicting installs.
    /// </summary>
    private static readonly HashSet<string> YamlManaged =
        new(StringComparer.OrdinalIgnoreCase) { "git", "net9", "net10" };

    /// <summary>
    /// Installs all prerequisites from the config that aren't YAML-managed,
    /// plus vpk if any velopack targets are being deployed (regardless of whether
    /// it appears in the prerequisites list).
    /// </summary>
    /// <param name="prerequisites">The "prerequisites" array from deploy-config.json.</param>
    /// <param name="needsVpk">True if any project being deployed has a velopack target.</param>
    public static async Task<List<StepResult>> InstallAsync(
        IEnumerable<string> prerequisites,
        bool needsVpk)
    {
        var results    = new List<StepResult>();
        var prereqList = prerequisites.ToList();
        var vpkInList  = prereqList.Any(p => p.Equals("vpk", StringComparison.OrdinalIgnoreCase));

        foreach (var prereq in prereqList)
        {
            if (YamlManaged.Contains(prereq))
            {
                Console.WriteLine($"  [Prerequisites] '{prereq}' is YAML-managed -- skipping.");
                continue;
            }

            var result = prereq.ToLowerInvariant() switch
            {
                "nodejs" => await InstallNodeJsAsync(),
                "vpk"    => await InstallVpkAsync(),

                _ => ProcessRunner.Synthetic(
                    $"Unknown prerequisite: {prereq}",
                    $"No installer defined for '{prereq}'. Skipping.",
                    success: true)
            };

            results.Add(result);
        }

        if (needsVpk && !vpkInList)
        {
            Console.WriteLine("  [Prerequisites] Velopack target detected -- ensuring vpk is installed.");
            results.Add(await InstallVpkAsync());
        }

        return results;
    }

    // -- Installers ------------------------------------------------------------

    private static async Task<StepResult> InstallNodeJsAsync()
    {
        var isWindows = OperatingSystem.IsWindows();
        var check = await ProcessRunner.RunAsync(
            "Check Node.js", isWindows ? "where" : "which", "node");

        if (check.Success)
        {
            Console.WriteLine($"  [Prerequisites] Node.js already installed: {check.Stdout.Trim()}");
            return check;
        }

        // Check if already installed to runner directory from a previous run
        var nodeDir = @"C:\actions-runner\nodejs";
        var nodeExe = Path.Combine(nodeDir, "node.exe");
        if (File.Exists(nodeExe))
        {
            Console.WriteLine($"  [Prerequisites] Node.js found at {nodeDir} -- adding to PATH.");
            Environment.SetEnvironmentVariable("PATH",
                Environment.GetEnvironmentVariable("PATH") + ";" + nodeDir);
            
            // Tell Node where its own modules live regardless of CWD
            // This fixes zip-extracted Node where npm resolves modules relative to CWD
            Environment.SetEnvironmentVariable(
                "npm_config_prefix",
                nodeDir);
            return ProcessRunner.Synthetic("Node.js already installed", nodeExe);
        }

        // Fetch latest LTS version from nodejs.org
        Console.WriteLine("  [Prerequisites] Fetching latest Node.js LTS version...");
        using var http = new System.Net.Http.HttpClient();
        var json = await http.GetStringAsync("https://nodejs.org/dist/index.json");

        var ltsMatch = System.Text.RegularExpressions.Regex.Match(
            json, @"""version""\s*:\s*""(v[\d.]+)""[^}]+""lts""\s*:\s*""[^""]+""");
        if (!ltsMatch.Success)
            return ProcessRunner.Synthetic("Fetch Node.js version",
                "Could not determine latest LTS version.", success: false);

        var version = ltsMatch.Groups[1].Value;
        var zipName = $"node-{version}-win-x64.zip";
        var zipUrl  = $"https://nodejs.org/dist/{version}/{zipName}";
        var zipPath = Path.Combine(Path.GetTempPath(), zipName);

        Console.WriteLine($"  [Prerequisites] Downloading Node.js {version} (zip)...");
        var downloadResult = await ProcessRunner.RunAsync(
            $"Download Node.js {version}",
            "powershell",
            $"-Command \"Invoke-WebRequest -Uri '{zipUrl}' -OutFile '{zipPath}' -UseBasicParsing\"");
        if (!downloadResult.Success) return downloadResult;

        // Extract zip - the zip contains a single top-level folder node-vX.Y.Z-win-x64
        var extractTemp = Path.Combine(Path.GetTempPath(), "node-extract");
        if (Directory.Exists(extractTemp)) Directory.Delete(extractTemp, recursive: true);

        var extractResult = await ProcessRunner.RunAsync(
            $"Extract Node.js {version}",
            "powershell",
            $"-Command \"Expand-Archive -Path '{zipPath}' -DestinationPath '{extractTemp}' -Force\"");

        File.Delete(zipPath);
        if (!extractResult.Success) return extractResult;

        // Move the inner folder to the final location
        var inner = Directory.GetDirectories(extractTemp).FirstOrDefault();
        if (inner is null || !File.Exists(Path.Combine(inner, "node.exe")))
            return ProcessRunner.Synthetic("Locate node.exe",
                "node.exe not found after extraction.", success: false);

        if (Directory.Exists(nodeDir)) Directory.Delete(nodeDir, recursive: true);
        Directory.Move(inner, nodeDir);
        Directory.Delete(extractTemp, recursive: false);

        Environment.SetEnvironmentVariable("PATH",
            Environment.GetEnvironmentVariable("PATH") + ";" + nodeDir);

        // Tell Node where its own modules live regardless of CWD
        // This fixes zip-extracted Node where npm resolves modules relative to CWD
        Environment.SetEnvironmentVariable(
            "npm_config_prefix",
            nodeDir);
        
        Console.WriteLine($"  [Prerequisites] Node.js {version} installed at {nodeDir}");
        return ProcessRunner.Synthetic($"Install Node.js {version}", nodeDir);
    }

    // Minimum vpk version required. 0.0.1299+ has --prefix support for az.
    private static readonly Version MinVpkVersion = new(0, 0, 1299);

    private static async Task<StepResult> InstallVpkAsync()
    {
        var toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools");

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!currentPath.Contains(toolsDir, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PATH", currentPath + ";" + toolsDir);
            Console.WriteLine($"  [Prerequisites] Added dotnet tools dir to PATH: {toolsDir}");
        }

        var isWindows = OperatingSystem.IsWindows();
        var check     = await ProcessRunner.RunAsync(
            "Check vpk", isWindows ? "where" : "which", "vpk");

        if (check.Success)
        {
            // vpk is on PATH - verify the version meets our minimum
            var versionCheck      = await ProcessRunner.RunAsync("Check vpk version", "vpk", "-h");
            var installedVersion  = ParseVpkVersion(versionCheck.Stdout + versionCheck.Stderr);

            if (installedVersion is not null && installedVersion >= MinVpkVersion)
            {
                Console.WriteLine($"  [Prerequisites] vpk {installedVersion} already installed (>= {MinVpkVersion} required).");
                return check;
            }

            Console.WriteLine(installedVersion is not null
                ? $"  [Prerequisites] vpk {installedVersion} is below minimum {MinVpkVersion} -- updating."
                : $"  [Prerequisites] Could not determine vpk version -- updating to be safe.");
        }

        return await ProcessRunner.RunDotnetAsync(
            "Install/update vpk (dotnet global tool)",
            "tool update -g vpk --prerelease");
    }

    /// <summary>
    /// Parses the vpk version from its help/info output.
    /// vpk -h outputs a line like: "Velopack CLI 0.0.1589-ga2c5a97 (prerelease)"
    /// </summary>
    private static Version? ParseVpkVersion(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            output,
            @"Velopack CLI\s+(\d+\.\d+\.\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success && Version.TryParse(match.Groups[1].Value, out var v) ? v : null;
    }
}