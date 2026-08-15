using Dytools.DeployTool.Models.Config;

namespace Dytools.DeployTool.Scanning;

/// <summary>
/// Turns a <see cref="ScanResult"/> into a best-guess <see cref="DeployConfig"/> - the starting
/// point the wizard/editor then refines. Structure (which projects, their kinds, tests) is inferred
/// reliably; deploy destinations (paths, app pools, servers) are placeholders to fill in, because
/// they aren't in the source.
///
/// csproj ProjectReferences are deliberately NOT written into dependentProjects - the deploy tool
/// resolves those automatically. dependentProjects is left empty for the user to add extra triggers.
/// </summary>
public static class ConfigScaffolder
{
    public static DeployConfig FromScan(ScanResult scan)
    {
        var deployables = scan.Projects.Where(p => p.IsDeployable)
                              .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var tests       = scan.Projects.Where(p => p.IsTest).ToList();
        var nonTest     = scan.Projects.Where(p => !p.IsTest).ToList();

        var projectsFolder = CommonParent(nonTest.Select(p => p.RelativeFolder)) ?? "src";
        var testsFolder    = CommonParent(tests.Select(p => p.RelativeFolder));

        var suffix = GuessTestSuffix(deployables, tests, testsFolder, out var explicitTestByProject);

        var projects = deployables.Select(p => new ProjectConfig
        {
            Name            = p.Name,
            UnitTestProject = explicitTestByProject.GetValueOrDefault(p.Name),
            Targets         = [GuessTarget(p)],
        }).ToList();

        return new DeployConfig
        {
            ProjectsFolder        = projectsFolder,
            UnitTestsFolder       = testsFolder,
            UnitTestProjectSuffix = suffix,
            Projects              = projects,
        };
    }

    // -- Target guess ---------------------------------------------------------

    private static TargetConfig GuessTarget(ScannedProject p)
    {
        var build = new BuildConfig { Configuration = "Release", Runtime = "win-x64" };

        // A Velopack reference is an explicit signal the app ships that way - otherwise
        // Web -> IIS and everything else deployable (console, desktop) -> Folder.
        if (p.UsesVelopack)
            return new TargetConfig
            {
                Type = DeployType.Velopack,
                Build = build,
                Prerequisites = ["vpk"],
                Velopack = new VelopackConfig { PackId = p.Name, Delivery = VelopackDelivery.PackOnly },
            };

        return p.Kind == ProjectKind.Web
            ? new TargetConfig
            {
                Type = DeployType.Iis,
                Build = build,
                Iis = new IisConfig { SiteName = p.Name, DeployPath = $@"C:\inetpub\{p.Name}", AppPool = p.Name },
            }
            : new TargetConfig
            {
                Type = DeployType.Folder,
                Build = build,
                Folder = new FolderConfig { DestinationPath = $@"C:\Apps\{p.Name}" },
            };
    }

    // -- Test suffix guess ----------------------------------------------------

    /// <summary>
    /// If every discovered test project is "&lt;product&gt;&lt;suffix&gt;" with the SAME suffix and
    /// lives under the tests folder by that name, returns the shared suffix (convention lookup wins).
    /// Otherwise returns null and fills <paramref name="explicitTestByProject"/> with repo-relative
    /// paths so each mapping is pinned explicitly.
    /// </summary>
    private static string? GuessTestSuffix(
        List<ScannedProject> deployables,
        List<ScannedProject> tests,
        string? testsFolder,
        out Dictionary<string, string> explicitTestByProject)
    {
        explicitTestByProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var productNames = deployables.Select(p => p.Name).ToList();
        var mapped = new List<(ScannedProject Test, string Product, string Suffix)>();

        foreach (var test in tests)
        {
            // Longest product-name prefix wins, so "Admin" beats "Ad" for "AdminUnitTest".
            var product = productNames
                .Where(n => test.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)
                            && test.Name.Length > n.Length)
                .OrderByDescending(n => n.Length)
                .FirstOrDefault();
            if (product is null) continue;

            mapped.Add((test, product, test.Name[product.Length..]));
        }

        if (mapped.Count == 0) return null;

        // With a tests folder, take the dominant suffix as the convention and pin only the
        // outliers (different suffix, or a test living outside {testsFolder}/{name}) explicitly.
        if (testsFolder is not null)
        {
            var suffix = mapped.GroupBy(m => m.Suffix, StringComparer.OrdinalIgnoreCase)
                               .OrderByDescending(g => g.Count())
                               .First().Key;

            foreach (var (test, product, s) in mapped)
            {
                var followsConvention = s.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    && test.RelativeFolder.Equals($"{testsFolder}/{test.Name}",
                                                  StringComparison.OrdinalIgnoreCase);
                if (!followsConvention)
                    explicitTestByProject[product] = $"{test.RelativeFolder}/{test.Name}.csproj";
            }
            return suffix;
        }

        // No common tests folder - pin every mapping explicitly.
        foreach (var (test, product, _) in mapped)
            explicitTestByProject[product] = $"{test.RelativeFolder}/{test.Name}.csproj";
        return null;
    }

    // -- Folder inference -----------------------------------------------------

    /// <summary>
    /// The most common parent directory of the given repo-relative project folders. Real repos
    /// aren't perfectly uniform (a stray project under build/ shouldn't collapse the guess to ""),
    /// so the majority folder wins; outliers are the user's to fix in the editor.
    /// </summary>
    private static string? CommonParent(IEnumerable<string> projectRelativeFolders)
    {
        var parents = projectRelativeFolders
            .Select(ParentDir)
            .Where(p => p is not null)
            .Cast<string>()
            .ToList();

        if (parents.Count == 0) return null;

        return parents
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Length)
            .First().Key;
    }

    private static string? ParentDir(string relFolder)
    {
        var i = relFolder.LastIndexOf('/');
        return i < 0 ? string.Empty : relFolder[..i];
    }
}
