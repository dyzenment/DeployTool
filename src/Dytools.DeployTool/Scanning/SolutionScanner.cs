using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Dytools.DeployTool.Scanning;

/// <summary>
/// Discovers projects from a solution file (.slnx or classic .sln) or, in glob mode,
/// from every .csproj under a folder. The solution's directory is treated as the repo root.
/// </summary>
public static class SolutionScanner
{
    /// <summary>Solution files sitting directly in <paramref name="dir"/> (not recursive).</summary>
    public static IReadOnlyList<string> FindSolutions(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    /// <summary>Scan a specific solution file.</summary>
    public static ScanResult ScanSolution(string solutionPath)
    {
        var abs      = Path.GetFullPath(solutionPath);
        var repoRoot = Path.GetDirectoryName(abs)!;

        var projects = ParseSolutionProjects(abs)
            .Where(File.Exists)
            .Select(p => ProjectInspector.Inspect(p, repoRoot))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScanResult { RepoRoot = repoRoot, SolutionPath = abs, Projects = projects };
    }

    /// <summary>Glob every .csproj under a root (skipping bin/obj).</summary>
    public static ScanResult ScanAllCsproj(string repoRoot)
    {
        var root = Path.GetFullPath(repoRoot);
        var sep  = Path.DirectorySeparatorChar;

        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}bin{sep}") && !p.Contains($"{sep}obj{sep}"))
            .Select(p => ProjectInspector.Inspect(p, root))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScanResult { RepoRoot = root, SolutionPath = null, Projects = projects };
    }

    /// <summary>Absolute .csproj paths referenced by a .slnx (XML) or .sln (text).</summary>
    public static IReadOnlyList<string> ParseSolutionProjects(string solutionPath)
    {
        var abs = Path.GetFullPath(solutionPath);
        var dir = Path.GetDirectoryName(abs)!;
        var results = new List<string>();

        if (abs.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            XDocument doc;
            try { doc = XDocument.Load(abs); }
            catch { return results; }

            foreach (var el in doc.Descendants("Project"))
            {
                var path = el.Attribute("Path")?.Value;
                if (!string.IsNullOrWhiteSpace(path) &&
                    path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    results.Add(Resolve(dir, path));
            }
        }
        else // classic .sln:  Project("{type}") = "Name", "rel\path.csproj", "{guid}"
        {
            var rx = new Regex(
                "Project\\(\"\\{[^}]+\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+\\.csproj)\"",
                RegexOptions.IgnoreCase);

            string text;
            try { text = File.ReadAllText(abs); }
            catch { return results; }

            foreach (Match m in rx.Matches(text))
                results.Add(Resolve(dir, m.Groups[1].Value));
        }

        return results;
    }

    private static string Resolve(string baseDir, string relPath) =>
        Path.GetFullPath(Path.Combine(baseDir,
            relPath.Replace('\\', Path.DirectorySeparatorChar)
                   .Replace('/', Path.DirectorySeparatorChar)));
}
