using System.Xml.Linq;
using Dytools.DeployTool.Models.Config;

namespace Dytools.DeployTool.Helpers;

// -----------------------------------------------------------------------------
// DiscoveredProject
// -----------------------------------------------------------------------------

/// <summary>
/// Rich project descriptor built once at startup from disk and config.
/// Covers ALL projects found under projectsFolder - both deployable projects
/// declared in deploy-config.json and library projects (Core, Infrastructure, etc.)
/// that are only referenced as dependencies.
///
/// Every handler reads from this object instead of doing its own file system
/// or csproj discovery at deploy time.
/// </summary>
public sealed class DiscoveredProject
{
    /// <summary>Project name - matches the folder name and .csproj stem.</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path to the project's source folder.</summary>
    public required string Folder { get; init; }

    /// <summary>Repo-root-relative path using forward slashes. e.g. "src/Admin"</summary>
    public required string RelativeFolder { get; init; }

    /// <summary>Absolute path to the .csproj file.</summary>
    public required string CsprojPath { get; init; }

    /// <summary>
    /// Assembly name from &lt;AssemblyName&gt; in the .csproj.
    /// Falls back to the project name if the element is absent.
    /// </summary>
    public required string AssemblyName { get; init; }

    /// <summary>
    /// Absolute path to the application icon resolved from &lt;ApplicationIcon&gt;
    /// in the .csproj. Null if the element is absent or the file does not exist.
    /// Handlers use this as the icon fallback when velopack.icon is not set.
    /// </summary>
    public string? CsprojIconPath { get; init; }

    /// <summary>
    /// Absolute path to the unit test .csproj for this project.
    /// Resolved at startup from: explicit unitTestProject override → convention lookup.
    /// Null if no test project is found or runTests is false.
    /// </summary>
    public string? UnitTestCsprojPath { get; init; }

    /// <summary>
    /// Full transitive closure of all .csproj ProjectReference dependencies.
    /// Each entry is another DiscoveredProject (including transitive library projects).
    /// Empty for projects with no references.
    /// </summary>
    public IReadOnlyList<DiscoveredProject> TransitiveDependencies { get; init; } = [];

    /// <summary>
    /// The deploy-config.json ProjectConfig for this project.
    /// Null for library projects that have no deploy targets.
    /// </summary>
    public ProjectConfig? Config { get; init; }

    /// <summary>True if this project appears in deploy-config.json (not necessarily deployable - may be disabled).</summary>
    public bool IsConfigured => Config is not null;

    /// <summary>True if this project has deploy targets and is not disabled.</summary>
    public bool IsDeployable => Config is { Disabled: false } && Config.Targets.Count > 0;

    /// <summary>
    /// Pre-merged noWarn codes for this project: root config noWarn + project-level noWarn,
    /// deduplicated. Handlers merge this further with target-level build.noWarn at publish time.
    /// Null if neither level specifies any codes.
    /// </summary>
    public string? NoWarn { get; init; }
}

// -----------------------------------------------------------------------------
// ProjectRegistry
// -----------------------------------------------------------------------------

/// <summary>
/// Single source of truth for all project knowledge for the current deploy run.
/// Built once at startup in Program.cs and passed to every component.
///
/// Eliminates scattered csproj parsing across handlers and resolvers.
/// </summary>
public sealed class ProjectRegistry
{
    /// <summary>All projects discovered under projectsFolder (deployable + libraries).</summary>
    public IReadOnlyList<DiscoveredProject> AllProjects { get; init; } = [];

    /// <summary>Fast lookup by project name (case-insensitive).</summary>
    public IReadOnlyDictionary<string, DiscoveredProject> ByName { get; init; } =
        new Dictionary<string, DiscoveredProject>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fast lookup by repo-root-relative folder path (forward slashes, case-insensitive).
    /// Used by the resolver for path prefix matching.
    /// </summary>
    public IReadOnlyDictionary<string, DiscoveredProject> ByRelativeFolder { get; init; } =
        new Dictionary<string, DiscoveredProject>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Unit test csproj paths that have already passed in this deploy run.
    /// Prevents the same test project from running multiple times when it is a
    /// transitive dependency of several deployable projects.
    /// </summary>
    public HashSet<string> PassedTestProjects { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Unit test csproj paths that have failed in this deploy run.
    /// </summary>
    public HashSet<string> FailedTestProjects { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    // -- Builder ---------------------------------------------------------------

    /// <summary>
    /// Scans the filesystem and config to build the full project registry.
    /// Performs all csproj parsing, dependency graph resolution, icon discovery,
    /// and unit test project lookup in one pass.
    /// </summary>
    public static ProjectRegistry Build(DeployConfig config, string repoRoot)
    {
        var absoluteProjectsRoot = Path.GetFullPath(Path.Combine(repoRoot, config.ProjectsFolder));

        // -- 1. Discover all csproj files --------------------------------------
        // Build a map from absolute folder → raw data before resolving refs
        var rawByFolder = new Dictionary<string, RawProject>(StringComparer.OrdinalIgnoreCase);

        foreach (var csprojPath in Directory.GetFiles(
            absoluteProjectsRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
            var name   = Path.GetFileNameWithoutExtension(csprojPath);
            rawByFolder[folder] = new RawProject(name, folder, csprojPath);
        }

        // -- 2. Parse direct references (absolute folder → set of absolute folders) --
        var directRefs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, raw) in rawByFolder)
            directRefs[folder] = ParseDirectReferenceFolders(raw.CsprojPath);

        // -- 3. Build config lookup --------------------------------------------
        var configByName = config.Projects
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        // -- 4. Create DiscoveredProject stubs (without transitive deps yet) ---
        var stubByFolder = new Dictionary<string, DiscoveredProject>(StringComparer.OrdinalIgnoreCase);

        foreach (var (folder, raw) in rawByFolder)
        {
            var relFolder   = Path.GetRelativePath(repoRoot, folder).Replace('\\', '/');
            configByName.TryGetValue(raw.Name, out var projectConfig);

            var csprojIconPath = ResolveApplicationIcon(raw.CsprojPath, folder);
            var unitTestPath   = ResolveUnitTestProject(
                raw.Name, projectConfig, config, repoRoot);

            stubByFolder[folder] = new DiscoveredProject
            {
                Name               = raw.Name,
                Folder             = folder,
                RelativeFolder     = relFolder,
                CsprojPath         = raw.CsprojPath,
                AssemblyName       = ReadAssemblyName(raw.CsprojPath) ?? raw.Name,
                CsprojIconPath     = csprojIconPath,
                UnitTestCsprojPath = unitTestPath,
                Config             = projectConfig,
                NoWarn             = MergeNoWarn(config.NoWarn, projectConfig?.NoWarn)
            };
        }

        // -- 5. Resolve transitive closures and produce final objects ----------
        var finalByFolder = new Dictionary<string, DiscoveredProject>(StringComparer.OrdinalIgnoreCase);

        DiscoveredProject Resolve(string folder, HashSet<string> visiting)
        {
            if (finalByFolder.TryGetValue(folder, out var existing)) return existing;

            if (!stubByFolder.TryGetValue(folder, out var stub))
            {
                // Reference to a project outside projectsFolder - create a minimal stub
                var name = Path.GetFileName(folder);
                var rel  = Path.GetRelativePath(repoRoot, folder).Replace('\\', '/');
                var dp   = new DiscoveredProject
                {
                    Name = name, Folder = folder, RelativeFolder = rel,
                    CsprojPath = folder, AssemblyName = name
                };
                finalByFolder[folder] = dp;
                return dp;
            }

            if (visiting.Contains(folder))
                return stub; // cycle - return stub without deps

            visiting.Add(folder);

            var transitive = new List<DiscoveredProject>();
            if (directRefs.TryGetValue(folder, out var refs))
            {
                foreach (var refFolder in refs)
                {
                    var dep = Resolve(refFolder, visiting);
                    transitive.Add(dep);
                    // Add dep's own transitive deps (flatten the closure)
                    foreach (var t in dep.TransitiveDependencies)
                        if (!transitive.Contains(t))
                            transitive.Add(t);
                }
            }

            visiting.Remove(folder);

            var final = new DiscoveredProject
            {
                Name                   = stub.Name,
                Folder                 = stub.Folder,
                RelativeFolder         = stub.RelativeFolder,
                CsprojPath             = stub.CsprojPath,
                AssemblyName           = stub.AssemblyName,
                CsprojIconPath         = stub.CsprojIconPath,
                UnitTestCsprojPath     = stub.UnitTestCsprojPath,
                Config                 = stub.Config,
                NoWarn                 = stub.NoWarn,
                TransitiveDependencies = transitive
            };

            finalByFolder[folder] = final;
            return final;
        }

        foreach (var folder in rawByFolder.Keys)
            Resolve(folder, []);

        var allProjects = finalByFolder.Values.ToList();

        return new ProjectRegistry
        {
            AllProjects        = allProjects,
            ByName             = allProjects.ToDictionary(p => p.Name, p => p,
                                     StringComparer.OrdinalIgnoreCase),
            ByRelativeFolder   = allProjects.ToDictionary(p => p.RelativeFolder, p => p,
                                     StringComparer.OrdinalIgnoreCase)
        };
    }

    // -- Private helpers -------------------------------------------------------

    private record RawProject(string Name, string Folder, string CsprojPath);

    private static HashSet<string> ParseDirectReferenceFolders(string csprojPath)
    {
        var csprojDir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
        var refs      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var doc = XDocument.Load(csprojPath);
            foreach (var el in doc.Descendants("ProjectReference"))
            {
                var include = el.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;

                var includePath = include.Replace('\\', Path.DirectorySeparatorChar)
                                         .Replace('/', Path.DirectorySeparatorChar);
                var refCsproj   = Path.GetFullPath(Path.Combine(csprojDir, includePath));
                var refFolder   = Path.GetDirectoryName(refCsproj);
                if (!string.IsNullOrWhiteSpace(refFolder))
                    refs.Add(refFolder);
            }
        }
        catch { /* malformed csproj - skip */ }

        return refs;
    }

    private static string? ReadAssemblyName(string csprojPath)
    {
        try
        {
            return XDocument.Load(csprojPath)
                .Descendants("AssemblyName")
                .FirstOrDefault()?.Value;
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolves &lt;ApplicationIcon&gt; from the .csproj to an absolute path.
    /// Returns null if the element is absent or the file does not exist.
    /// </summary>
    private static string? ResolveApplicationIcon(string csprojPath, string projectFolder)
    {
        try
        {
            var doc     = XDocument.Load(csprojPath);
            var appIcon = doc.Descendants("ApplicationIcon").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(appIcon)) return null;

            var resolved = Path.GetFullPath(Path.Combine(projectFolder, appIcon));
            return File.Exists(resolved) ? resolved : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Resolves the unit test .csproj path for a project.
    /// Priority:
    ///   1. Explicit unitTestProject on ProjectConfig (name or repo-root-relative path)
    ///   2. Convention: {unitTestsFolder}/{name}{unitTestProjectSuffix}/{name}{unitTestProjectSuffix}.csproj
    ///   3. Null - no test project found
    /// Returns null if runTests is false or no test project can be located.
    /// </summary>
    private static string? ResolveUnitTestProject(
        string projectName,
        ProjectConfig? projectConfig,
        DeployConfig config,
        string repoRoot)
    {
        // Opt-out
        if (projectConfig is { RunTests: false }) return null;

        // Explicit override on the project config
        if (!string.IsNullOrWhiteSpace(projectConfig?.UnitTestProject))
        {
            var raw = projectConfig.UnitTestProject;

            // Could be a name ("AdminUnitTest") or a path ("tests/AdminUnitTest/AdminUnitTest.csproj")
            if (raw.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                // Treat as repo-root-relative path
                var abs = Path.GetFullPath(Path.Combine(repoRoot, raw.Replace('/', Path.DirectorySeparatorChar)));
                return File.Exists(abs) ? abs : null;
            }
            else
            {
                // Treat as project name - look in unitTestsFolder by convention
                return FindByConvention(raw, config, repoRoot);
            }
        }

        // Convention lookup
        if (string.IsNullOrWhiteSpace(config.UnitTestsFolder) ||
            string.IsNullOrWhiteSpace(config.UnitTestProjectSuffix))
            return null;

        return FindByConvention(projectName, config, repoRoot);
    }

    private static string? FindByConvention(string baseName, DeployConfig config, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(config.UnitTestsFolder) ||
            string.IsNullOrWhiteSpace(config.UnitTestProjectSuffix))
            return null;

        var testProjectName = baseName + config.UnitTestProjectSuffix;
        var csprojPath = Path.GetFullPath(Path.Combine(
            repoRoot,
            config.UnitTestsFolder,
            testProjectName,
            testProjectName + ".csproj"));

        return File.Exists(csprojPath) ? csprojPath : null;
    }

    /// <summary>
    /// Merges two comma-separated noWarn strings, deduplicating codes.
    /// Returns null if both inputs are empty.
    /// </summary>
    private static string? MergeNoWarn(string? a, string? b)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in new[] { a, b })
            if (!string.IsNullOrWhiteSpace(src))
                foreach (var code in src.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    codes.Add(code);
        return codes.Count > 0 ? string.Join(",", codes) : null;
    }
}