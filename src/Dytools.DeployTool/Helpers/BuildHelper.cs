using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Shared build/publish logic used by all deploy type handlers.
/// Routes to dotnet publish (.NET 5+) or msbuild (.NET Framework) based on the
/// project's target framework. Centralizes server-safe resource flags so they
/// apply consistently across all build types.
/// </summary>
public static class BuildHelper
{
    /// <summary>
    /// Builds and publishes a project, routing to msbuild or dotnet publish as appropriate.
    /// </summary>
    /// <param name="projectName">Project name (matches .csproj filename).</param>
    /// <param name="projectPath">Absolute path to the project folder.</param>
    /// <param name="build">Build configuration from the target. Defaults apply if null.</param>
    /// <param name="outputDir">Absolute path where publish output should land.</param>
    /// <param name="projectNoWarn">
    /// Pre-merged root+project noWarn from DiscoveredProject.NoWarn.
    /// Further merged with build.NoWarn (target level) before passing to the build tool.
    /// </param>
    public static Task<StepResult> PublishAsync(
        string projectName,
        string projectPath,
        BuildConfig? build,
        string outputDir,
        string? projectNoWarn = null)
    {
        build ??= new BuildConfig();
        var csproj = Path.Combine(projectPath, $"{projectName}.csproj");

        // Resolve which TFM to build for:
        //   1. Explicit build.targetFramework from config
        //   2. Single TFM declared in the csproj
        //   3. null - only safe for single-target projects; dotnet/msbuild will pick
        var tfm = ResolveTargetFramework(build, csproj);

        return IsFrameworkTfm(tfm, csproj)
            ? BuildFrameworkAsync(csproj, build, tfm, outputDir, projectNoWarn, projectPath)
            : BuildDotnetAsync(projectName, projectPath, build, tfm, outputDir, projectNoWarn);
    }

    // -- .NET Framework (msbuild) ----------------------------------------------

    private static Task<StepResult> BuildFrameworkAsync(
        string csproj, BuildConfig build, string? tfm, string outputDir,
        string? projectNoWarn, string projectPath)
    {
        var nowarn = MergeNoWarn(projectNoWarn, build.NoWarn);

        var args = new List<string>
        {
            $"\"{csproj}\"",
            "/t:Build",
            $"/p:Configuration={build.Configuration}",
            $"/p:OutputPath=\"{outputDir}\"",
            ResourceGovernor.MsBuildSwitches
        };

        if (!string.IsNullOrWhiteSpace(tfm))
            args.Add($"/p:TargetFramework={tfm}");

        if (!string.IsNullOrWhiteSpace(nowarn))
            args.Add($"/p:NoWarn=\"{nowarn}\"");

        return ProcessRunner.RunAsync("msbuild build", "msbuild", string.Join(" ", args), projectPath);
    }

    // -- .NET Core / .NET 5+ (dotnet publish) ---------------------------------

    private static Task<StepResult> BuildDotnetAsync(
        string projectName, string projectPath, BuildConfig build, string? tfm,
        string outputDir, string? projectNoWarn)
    {
        var csproj = Path.Combine(projectPath, $"{projectName}.csproj");

        var args = new List<string>
        {
            $"\"{csproj}\"",
            $"-c {build.Configuration}",
            $"-o \"{outputDir}\"",
            ResourceGovernor.MsBuildSwitches
        };

        if (!string.IsNullOrWhiteSpace(tfm))
            args.Add($"-f {tfm}");

        if (!string.IsNullOrWhiteSpace(build.Runtime))
            args.Add($"-r {build.Runtime}");

        if (build.SelfContained.HasValue)
            args.Add($"--self-contained {build.SelfContained.Value.ToString().ToLowerInvariant()}");

        if (build.SingleFile.HasValue)
            args.Add($"-p:PublishSingleFile={build.SingleFile.Value.ToString().ToLowerInvariant()}");

        var merged = MergeNoWarn(projectNoWarn, build.NoWarn);
        if (!string.IsNullOrWhiteSpace(merged))
            args.Add($"/nowarn:{merged}");

        return ProcessRunner.RunDotnetAsync("dotnet publish", $"publish {string.Join(" ", args)}", projectPath);
    }

    // -- TFM resolution --------------------------------------------------------

    /// <summary>
    /// Resolves the target framework moniker to build for, in priority order:
    ///   1. Explicit build.targetFramework from deploy config
    ///   2. Single TFM declared in the csproj (TargetFramework or sole TargetFrameworks entry)
    ///   3. Null - only safe for single-target projects; dotnet/msbuild will pick
    /// For multi-target projects, build.targetFramework must be set explicitly.
    /// </summary>
    private static string? ResolveTargetFramework(BuildConfig build, string csprojPath)
    {
        if (!string.IsNullOrWhiteSpace(build.TargetFramework))
            return build.TargetFramework;

        try
        {
            var doc = System.Xml.Linq.XDocument.Load(csprojPath);

            // Single TFM - most common case
            var single = doc.Descendants("TargetFramework").FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(single)) return single.Trim();

            // Multiple TFMs - only infer if there's exactly one entry
            var multi = doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value;
            if (!string.IsNullOrWhiteSpace(multi))
            {
                var tfms = multi.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tfms.Length == 1) return tfms[0];

                Console.WriteLine(
                    $"  [Build] Warning: '{Path.GetFileName(csprojPath)}' has multiple target frameworks " +
                    $"({multi}) but no targetFramework is set in the build config. " +
                    "Add \"targetFramework\": \"<tfm>\" to the target's build block.");
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Returns true if the resolved TFM targets .NET Framework rather than .NET Core / .NET 5+.
    ///
    /// Old-style csproj (non-SDK): presence of TargetFrameworkVersion is definitive.
    /// SDK-style: TFM prefix determines - net481, net48, net472, net462, net45, net40, net35, net20.
    /// </summary>
    private static bool IsFrameworkTfm(string? tfm, string csprojPath)
    {
        // Old-style csproj always uses TargetFrameworkVersion - its presence alone means Framework
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(csprojPath);
            if (doc.Descendants("TargetFrameworkVersion").Any()) return true;
        }
        catch { }

        if (string.IsNullOrWhiteSpace(tfm)) return false;

        return tfm.StartsWith("net4", StringComparison.OrdinalIgnoreCase)
            || tfm.StartsWith("net3", StringComparison.OrdinalIgnoreCase)
            || tfm.StartsWith("net2", StringComparison.OrdinalIgnoreCase);
    }

    // -- noWarn merge ----------------------------------------------------------

    /// <summary>
    /// Combines two comma-separated noWarn strings, removing duplicates.
    /// Returns null if both inputs are empty.
    /// </summary>
    public static string? MergeNoWarn(string? a, string? b)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var src in new[] { a, b })
            if (!string.IsNullOrWhiteSpace(src))
                foreach (var code in src.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    codes.Add(code);

        return codes.Count > 0 ? string.Join(",", codes) : null;
    }
}