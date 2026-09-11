using System.Text.Json;
using Dytools.DeployTool;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Models.Plan;
using Dytools.DeployTool.Models.Reporting;
using Dytools.DeployTool.Services;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers the primary handing steps to the agent on its own box. Fire and forget: the run
/// folder lands, and that is the whole contract - so the assertions are about what landed.
/// </summary>
[TestClass]
public sealed class SelfViaAgentTests
{
    private const string RunId = "20260911-090000-abc1234";

    private string _root = string.Empty;
    private string _stagingRoot = string.Empty;
    private string _incoming = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root        = Path.Combine(Path.GetTempPath(), "DeployToolTests", Guid.NewGuid().ToString("N"));
        _stagingRoot = Path.Combine(_root, "primary-staging", RunId);
        _incoming    = Path.Combine(_root, "deploy", "incoming");
        Directory.CreateDirectory(_incoming);

        var artifact = Path.Combine(_stagingRoot, "artifacts", "Web-release");
        Directory.CreateDirectory(artifact);
        File.WriteAllText(Path.Combine(artifact, "app.dll"), "payload");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* temp dir */ }
    }

    private static ApplyStep Iis(string label = @"IIS / C:\inetpub\Web") => new()
    {
        Project = "Web", Type = DeployType.Iis, Artifact = "artifacts/Web-release", Label = label,
        Iis = new IisConfig { SiteName = "Web", AppPool = "Web", DeployPath = @"C:\inetpub\Web" }
    };

    private RolloutPlan Plan(params ApplyStep[] steps) => new()
    {
        RunId = RunId, CommitSha = "abc1234", WaitSeconds = 3600,
        ServerPlans =
        [
            new ServerPlan
            {
                ServerName = "web1", IsSelf = true, ApplyViaAgent = null,
                IncomingShare = _incoming, Steps = steps.ToList()
            }
        ]
    };

    private string RunFolder => Path.Combine(_incoming, RunId);

    private DeployManifest Manifest() => JsonSerializer.Deserialize<DeployManifest>(
        File.ReadAllText(Path.Combine(RunFolder, DeployManifest.FileName)), JsonOptions.Read)!;

    [TestMethod]
    public void Ship_LandsARunFolder_DueNow_WithJustTheGivenSteps()
    {
        var handed = Iis("IIS / handed");
        var kept   = Iis("IIS / kept inline");
        var report = new DeployReport { RunId = RunId, ServerName = "web1" };
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var shipped = SelfViaAgent.Ship(Plan(handed, kept), [handed], _stagingRoot, keepRuns: 5, report);

        Assert.IsTrue(shipped);
        Assert.IsTrue(File.Exists(Path.Combine(RunFolder, "artifacts", "Web-release", "app.dll")));

        var m = Manifest();
        Assert.AreEqual(1, m.Steps.Count, "only what was handed over travels");
        Assert.AreEqual("IIS / handed", m.Steps[0].Label);
        Assert.AreEqual("web1", m.Server);
        Assert.AreEqual("web1", m.Primary);
        Assert.IsTrue(m.NotBeforeUtc >= before && m.NotBeforeUtc <= DateTimeOffset.UtcNow, "no soak window on self");

        var p = report.Propagation.Single();
        Assert.IsTrue(p.Success);
        Assert.AreEqual(RunFolder, p.RunFolder);
        Assert.AreEqual(1, p.StepCount);
        Assert.IsFalse(File.Exists(Path.Combine(RunFolder, "result.json")), "fire and forget - nothing waits for the agent");
    }

    [TestMethod]
    public void Ship_RunFolderAlreadyPresent_FailsAndIsRecorded()
    {
        Directory.CreateDirectory(RunFolder);
        var step   = Iis();
        var report = new DeployReport { RunId = RunId, ServerName = "web1" };

        var shipped = SelfViaAgent.Ship(Plan(step), [step], _stagingRoot, keepRuns: 5, report);

        Assert.IsFalse(shipped);
        var p = report.Propagation.Single();
        Assert.IsFalse(p.Success);
        StringAssert.Contains(p.ErrorMessage, "already exists");
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "deploy", "staging", RunId)), "no partial staging left behind");
    }

    [TestMethod]
    public void HandedOff_ReflectsTheHandoff_NotTheApply()
    {
        var step = Iis();

        var ok = SelfViaAgent.HandedOff(step, shipped: true, error: null);
        Assert.IsTrue(ok.Success);
        Assert.AreEqual(SelfViaAgent.AppliedByLabel, ok.AppliedBy);
        Assert.IsNull(ok.ErrorMessage);
        Assert.AreEqual(step.Label, ok.TargetLabel);

        var bad = SelfViaAgent.HandedOff(step, shipped: false, error: "share offline");
        Assert.IsFalse(bad.Success);
        StringAssert.Contains(bad.ErrorMessage, "share offline");
    }
}
