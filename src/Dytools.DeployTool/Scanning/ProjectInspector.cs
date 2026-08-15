using System.Xml.Linq;

namespace Dytools.DeployTool.Scanning;

/// <summary>
/// Reads a single .csproj and classifies it. Pure XML inspection - no build, no MSBuild eval.
/// Classification is a best-effort guess; the wizard lets the user correct it.
/// </summary>
public static class ProjectInspector
{
    public static ScannedProject Inspect(string csprojPath, string repoRoot)
    {
        var abs    = Path.GetFullPath(csprojPath);
        var folder = Path.GetDirectoryName(abs)!;
        var name   = Path.GetFileNameWithoutExtension(abs);
        var rel    = Path.GetRelativePath(repoRoot, folder).Replace('\\', '/');

        XDocument doc;
        try { doc = XDocument.Load(abs); }
        catch
        {
            return new ScannedProject
            {
                Name = name, CsprojPath = abs, Folder = folder,
                RelativeFolder = rel, Kind = ProjectKind.Unknown,
            };
        }

        return new ScannedProject
        {
            Name           = name,
            CsprojPath     = abs,
            Folder         = folder,
            RelativeFolder = rel,
            Kind           = Classify(doc),
            UsesVelopack   = ReferencesVelopack(doc),
            ReferencePaths = DirectReferencePaths(doc, folder),
        };
    }

    /// <summary>True if the project has a Velopack PackageReference.</summary>
    public static bool ReferencesVelopack(XDocument doc) =>
        doc.Descendants("PackageReference")
           .Any(e => (e.Attribute("Include")?.Value ?? string.Empty)
               .StartsWith("Velopack", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The classification heuristic, in priority order. Public so it can be unit-tested
    /// directly against a csproj XML document without touching the filesystem.
    /// </summary>
    public static ProjectKind Classify(XDocument doc)
    {
        var sdk        = doc.Root?.Attribute("Sdk")?.Value ?? string.Empty;
        var outputType = Value(doc, "OutputType");
        var packages   = doc.Descendants("PackageReference")
                            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
                            .ToList();

        // Tests first - a test project might also be an Exe / reference AspNetCore.
        // The framework can come from the SDK (e.g. Sdk="MSTest.Sdk/3.6.4") or a PackageReference.
        var isTest = Flag(doc, "IsTestProject")
            || sdk.Contains("Test", StringComparison.OrdinalIgnoreCase)
            || packages.Any(p =>
                p.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("Microsoft.Testing.", StringComparison.OrdinalIgnoreCase));
        if (isTest) return ProjectKind.Test;

        // Web - the SDK attribute is authoritative (Microsoft.NET.Sdk.Web / Blazor WASM), or the
        // project pulls in the ASP.NET Core shared framework. A plain-SDK library that merely
        // references an AspNetCore *NuGet package* (e.g. AspNetCore.OData) is NOT a web app.
        var isWeb = sdk.Contains("Web", StringComparison.OrdinalIgnoreCase)
            || doc.Descendants("FrameworkReference").Any(e => string.Equals(
                e.Attribute("Include")?.Value, "Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase));
        if (isWeb) return ProjectKind.Web;

        // Desktop - WPF / WinForms / a windowed exe.
        var isDesktop = Flag(doc, "UseWPF") || Flag(doc, "UseWindowsForms")
            || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase);
        if (isDesktop) return ProjectKind.Desktop;

        if (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase))
            return ProjectKind.Console;

        // SDK-style project with no OutputType defaults to a library.
        return ProjectKind.ClassLibrary;
    }

    private static IReadOnlyList<string> DirectReferencePaths(XDocument doc, string csprojFolder)
    {
        var list = new List<string>();
        foreach (var el in doc.Descendants("ProjectReference"))
        {
            var include = el.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include)) continue;

            var normalized = include.Replace('\\', Path.DirectorySeparatorChar)
                                    .Replace('/', Path.DirectorySeparatorChar);
            list.Add(Path.GetFullPath(Path.Combine(csprojFolder, normalized)));
        }
        return list;
    }

    private static string Value(XDocument doc, string element) =>
        doc.Descendants(element).FirstOrDefault()?.Value?.Trim() ?? string.Empty;

    private static bool Flag(XDocument doc, string element) =>
        Value(doc, element).Equals("true", StringComparison.OrdinalIgnoreCase);
}
