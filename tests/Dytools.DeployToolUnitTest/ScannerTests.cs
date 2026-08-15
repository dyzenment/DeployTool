using System.Xml.Linq;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Scanning;

namespace DeployToolUnitTest;

/// <summary>The csproj classification heuristic - the guesses that seed the scaffold.</summary>
[TestClass]
public sealed class ProjectInspectorTests
{
    private static ProjectKind Classify(string xml) => ProjectInspector.Classify(XDocument.Parse(xml));

    [TestMethod]
    public void Web_FromWebSdk() =>
        Assert.AreEqual(ProjectKind.Web, Classify("<Project Sdk=\"Microsoft.NET.Sdk.Web\" />"));

    [TestMethod]
    public void Library_WithAspNetCorePackage_IsNotWeb() =>   // e.g. AspNetCore.OData in a class library
        Assert.AreEqual(ProjectKind.ClassLibrary, Classify(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"Microsoft.AspNetCore.OData\" /></ItemGroup></Project>"));

    [TestMethod]
    public void Web_FromAspNetCoreFrameworkReference() =>     // plain-SDK web app
        Assert.AreEqual(ProjectKind.Web, Classify(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<FrameworkReference Include=\"Microsoft.AspNetCore.App\" /></ItemGroup></Project>"));

    [TestMethod]
    public void ReferencesVelopack_Detected() =>
        Assert.IsTrue(ProjectInspector.ReferencesVelopack(XDocument.Parse(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"Velopack\" /></ItemGroup></Project>")));

    [TestMethod]
    public void Console_FromExe() =>
        Assert.AreEqual(ProjectKind.Console, Classify(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>"));

    [TestMethod]
    public void Desktop_FromWinExe() =>
        Assert.AreEqual(ProjectKind.Desktop, Classify(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>"));

    [TestMethod]
    public void Desktop_FromUseWpf() =>
        Assert.AreEqual(ProjectKind.Desktop, Classify(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>"));

    [TestMethod]
    public void ClassLibrary_IsTheDefault() =>
        Assert.AreEqual(ProjectKind.ClassLibrary, Classify("<Project Sdk=\"Microsoft.NET.Sdk\" />"));

    [TestMethod]
    public void Test_FromTestSdkPackage() =>
        Assert.AreEqual(ProjectKind.Test, Classify(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>"));

    [TestMethod]
    public void Test_FromMSTestSdk_NoPackageReference() =>   // the AdminUnitTest real-world case
        Assert.AreEqual(ProjectKind.Test, Classify("<Project Sdk=\"MSTest.Sdk/3.6.4\" />"));

    [TestMethod]
    public void Test_WinsOverExe() =>                        // a test project that is also an Exe
        Assert.AreEqual(ProjectKind.Test, Classify(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup>" +
            "<ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>"));
}

/// <summary>The scan → DeployConfig guess: target kinds, folder inference, test-suffix mapping.</summary>
[TestClass]
public sealed class ConfigScaffolderTests
{
    private static ScannedProject P(string name, ProjectKind kind, string relFolder) => new()
    {
        Name = name, Kind = kind,
        CsprojPath = $"/repo/{relFolder}/{name}.csproj",
        Folder = $"/repo/{relFolder}",
        RelativeFolder = relFolder,
    };

    private static ScanResult Scan(params ScannedProject[] ps) =>
        new() { RepoRoot = "/repo", SolutionPath = null, Projects = ps };

    [TestMethod]
    public void Web_MapsToIis() =>
        Assert.AreEqual(DeployType.Iis,
            ConfigScaffolder.FromScan(Scan(P("Web", ProjectKind.Web, "src/Web")))
                .Projects.Single().Targets.Single().Type);

    [TestMethod]
    public void Desktop_MapsToFolder_NotVelopack() =>       // per decision: Velopack is opt-in only
        Assert.AreEqual(DeployType.Folder,
            ConfigScaffolder.FromScan(Scan(P("App", ProjectKind.Desktop, "src/App")))
                .Projects.Single().Targets.Single().Type);

    [TestMethod]
    public void Console_MapsToFolder() =>
        Assert.AreEqual(DeployType.Folder,
            ConfigScaffolder.FromScan(Scan(P("Svc", ProjectKind.Console, "src/Svc")))
                .Projects.Single().Targets.Single().Type);

    [TestMethod]
    public void VelopackReference_MapsToVelopack()
    {
        var p = new ScannedProject
        {
            Name = "App", Kind = ProjectKind.Console, UsesVelopack = true,
            CsprojPath = "/repo/src/App/App.csproj", Folder = "/repo/src/App", RelativeFolder = "src/App",
        };
        var target = ConfigScaffolder.FromScan(Scan(p)).Projects.Single().Targets.Single();
        Assert.AreEqual(DeployType.Velopack, target.Type);
        Assert.AreEqual("App", target.Velopack!.PackId);
    }

    [TestMethod]
    public void LibrariesAndTests_AreNotDeployed()
    {
        var cfg = ConfigScaffolder.FromScan(Scan(
            P("Web", ProjectKind.Web, "src/Web"),
            P("Core", ProjectKind.ClassLibrary, "src/Core"),
            P("WebUnitTest", ProjectKind.Test, "tests/WebUnitTest")));
        CollectionAssert.AreEqual(new[] { "Web" }, cfg.Projects.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public void ProjectsFolder_IsMostCommonParent_IgnoringOutliers()
    {
        var cfg = ConfigScaffolder.FromScan(Scan(
            P("A", ProjectKind.Console, "src/A"),
            P("B", ProjectKind.Console, "src/B"),
            P("C", ProjectKind.Console, "build/C")));
        Assert.AreEqual("src", cfg.ProjectsFolder);
    }

    [TestMethod]
    public void TestSuffix_ConventionForMajority_ExplicitForOutlier()
    {
        var cfg = ConfigScaffolder.FromScan(Scan(
            P("Web", ProjectKind.Web, "src/Web"),
            P("Api", ProjectKind.Web, "src/Api"),
            P("Tool", ProjectKind.Console, "src/Tool"),
            P("WebUnitTest", ProjectKind.Test, "tests/WebUnitTest"),
            P("ApiUnitTest", ProjectKind.Test, "tests/ApiUnitTest"),
            P("ToolUnitTest", ProjectKind.Test, "build/ToolUnitTest")));  // outlier folder

        Assert.AreEqual("tests", cfg.UnitTestsFolder);
        Assert.AreEqual("UnitTest", cfg.UnitTestProjectSuffix);
        Assert.IsNull(cfg.Projects.Single(p => p.Name == "Web").UnitTestProject,
            "convention-following tests need no explicit path");
        Assert.AreEqual("build/ToolUnitTest/ToolUnitTest.csproj",
            cfg.Projects.Single(p => p.Name == "Tool").UnitTestProject,
            "the outlier test is pinned explicitly");
    }
}

/// <summary>Solution-file parsing for both .slnx (XML) and classic .sln (text).</summary>
[TestClass]
public sealed class SolutionScannerTests
{
    [TestMethod]
    public void ParsesSlnx()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "s.slnx");
            File.WriteAllText(path,
                "<Solution><Folder Name=\"/src/\"><Project Path=\"src/A/A.csproj\" /></Folder>" +
                "<Project Path=\"B/B.csproj\" /></Solution>");

            var got = SolutionScanner.ParseSolutionProjects(path).Select(p => p.Replace('\\', '/')).ToList();

            Assert.AreEqual(2, got.Count);
            Assert.IsTrue(got.Any(p => p.EndsWith("src/A/A.csproj")));
            Assert.IsTrue(got.Any(p => p.EndsWith("B/B.csproj")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void ParsesClassicSln()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = Path.Combine(dir, "s.sln");
            File.WriteAllText(path,
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"A\", " +
                "\"src\\A\\A.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject\n");

            var got = SolutionScanner.ParseSolutionProjects(path);

            Assert.AreEqual(1, got.Count);
            Assert.IsTrue(got[0].Replace('\\', '/').EndsWith("src/A/A.csproj"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
