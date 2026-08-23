using System.Text.Json;
using Dytools.DeployTool;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Models.Plan;
using Dytools.DeployTool.Services;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers the handoff to a peer: what lands, where, when it may be applied, and what is
/// left behind when it goes wrong.
///
/// A peer's "share" here is just a local directory. That is the point - the propagator only
/// ever needs a reachable path, so the whole delivery is exercised without SMB, a second
/// machine, or a network.
/// </summary>
[TestClass]
public sealed class PropagatorTests
{
    private string _root = string.Empty;
    private string _stagingRoot = string.Empty;
    private string _peerDeployRoot = string.Empty;
    private string _peerIncoming = string.Empty;

    private const string RunId = "20260819-120000-abc1234";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "DeployToolTests", Guid.NewGuid().ToString("N"));

        // The primary's own run folder, holding freshly published output.
        _stagingRoot = Path.Combine(_root, "primary-staging", RunId);

        // The peer side: \\PEER\deploy\{staging,incoming}, modelled as plain folders.
        _peerDeployRoot = Path.Combine(_root, "peer", "deploy");
        _peerIncoming   = Path.Combine(_peerDeployRoot, "incoming");
        Directory.CreateDirectory(_peerIncoming);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* temp dir; a leak here must not fail a test */ }
    }

    // -- Fixtures --------------------------------------------------------------

    /// <summary>Publishes a fake artifact into the primary's staging folder.</summary>
    private void Publish(string relativeArtifact, string fileName = "app.dll", string content = "payload")
    {
        var dir = Path.Combine(_stagingRoot, relativeArtifact.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, fileName), content);
        File.WriteAllText(Path.Combine(dir, "sub", "nested.txt"), content);
    }

    private static ApplyStep Step(string project, string artifact) => new()
    {
        Project  = project,
        Type     = DeployType.Folder,
        Artifact = artifact,
        Label    = $"Folder / C:\\Apps\\{project}",
        Folder   = new FolderConfig { DestinationPath = $@"C:\Apps\{project}", ServiceName = project }
    };

    private RolloutPlan PlanWithPeer(
        IEnumerable<ApplyStep> peerSteps,
        string? incomingShare = null,
        int waitSeconds = 3600) => new()
    {
        RunId       = RunId,
        CommitSha   = "abc1234def5678",
        WaitSeconds = waitSeconds,
        ServerPlans =
        [
            new ServerPlan { ServerName = "S1", IsSelf = true, Steps = peerSteps.ToList() },
            new ServerPlan
            {
                ServerName    = "S2",
                IsSelf        = false,
                IncomingShare = incomingShare ?? _peerIncoming,
                Steps         = peerSteps.ToList()
            }
        ]
    };

    private string PeerRunFolder => Path.Combine(_peerIncoming, RunId);

    private DeployManifest ReadManifest()
    {
        var json = File.ReadAllText(Path.Combine(PeerRunFolder, DeployManifest.FileName));
        return JsonSerializer.Deserialize<DeployManifest>(json, JsonOptions.Read)!;
    }

    // -- Delivery --------------------------------------------------------------

    [TestMethod]
    public void Propagate_LandsArtifactsAndManifestInTheIncomingRunFolder()
    {
        Publish("artifacts/Web-folder");
        var plan = PlanWithPeer([Step("Web", "artifacts/Web-folder")]);

        var results = Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].Success, results[0].ErrorMessage);
        Assert.AreEqual(PeerRunFolder, results[0].RunFolder);

        Assert.IsTrue(File.Exists(Path.Combine(PeerRunFolder, DeployManifest.FileName)));
        Assert.IsTrue(File.Exists(Path.Combine(PeerRunFolder, "artifacts", "Web-folder", "app.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(PeerRunFolder, "artifacts", "Web-folder", "sub", "nested.txt")),
            "Nested artifact content must travel too.");
    }

    [TestMethod]
    public void Propagate_LeavesNoStagingFolderBehindOnSuccess()
    {
        Publish("artifacts/Web-folder");
        var plan = PlanWithPeer([Step("Web", "artifacts/Web-folder")]);

        Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        // The move must be a rename, not a copy: nothing may remain under staging.
        var staged = Path.Combine(_peerDeployRoot, "staging", RunId);
        Assert.IsFalse(Directory.Exists(staged), "Staging folder survived the move.");
    }

    [TestMethod]
    public void Propagate_CopiesOnlyArtifactsThePeersStepsReference()
    {
        Publish("artifacts/Web-folder");
        Publish("artifacts/Admin-velopack");   // Global scope - never enters a peer plan

        var plan = PlanWithPeer([Step("Web", "artifacts/Web-folder")]);

        Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        Assert.IsTrue(Directory.Exists(Path.Combine(PeerRunFolder, "artifacts", "Web-folder")));
        Assert.IsFalse(Directory.Exists(Path.Combine(PeerRunFolder, "artifacts", "Admin-velopack")),
            "A peer must not receive artifacts no step of its own references.");
    }

    [TestMethod]
    public void Propagate_CopiesASharedArtifactOnceWhenTwoStepsReferenceIt()
    {
        Publish("artifacts/Web-folder");
        var plan = PlanWithPeer([
            Step("Web", "artifacts/Web-folder"),
            Step("Web", "artifacts/Web-folder")
        ]);

        var results = Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        Assert.IsTrue(results[0].Success, results[0].ErrorMessage);
        Assert.AreEqual(2, results[0].StepCount);
    }

    // -- The soak deadline -----------------------------------------------------

    [TestMethod]
    public void Propagate_StampsNotBeforeAsPrimarySuccessPlusTheWait()
    {
        Publish("artifacts/Web-folder");
        var plan      = PlanWithPeer([Step("Web", "artifacts/Web-folder")], waitSeconds: 1800);
        var succeeded = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

        var results = Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, succeeded);

        var expected = succeeded.AddSeconds(1800);
        Assert.AreEqual(expected, results[0].NotBeforeUtc);
        Assert.AreEqual(expected, ReadManifest().NotBeforeUtc,
            "The deadline the peer reads must be the one the primary reported.");
    }

    [TestMethod]
    public void Propagate_WithZeroWaitLetsThePeerApplyImmediately()
    {
        Publish("artifacts/Web-folder");
        var plan      = PlanWithPeer([Step("Web", "artifacts/Web-folder")], waitSeconds: 0);
        var succeeded = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

        Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, succeeded);

        Assert.AreEqual(succeeded, ReadManifest().NotBeforeUtc);
    }

    // -- Manifest contents -----------------------------------------------------

    [TestMethod]
    public void Propagate_ManifestCarriesTheRunIdentityAndBothServerNames()
    {
        Publish("artifacts/Web-folder");
        var plan = PlanWithPeer([Step("Web", "artifacts/Web-folder")]);

        Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        var manifest = ReadManifest();
        Assert.AreEqual(RunId, manifest.RunId);
        Assert.AreEqual("abc1234def5678", manifest.CommitSha);
        Assert.AreEqual("S1", manifest.Primary);
        Assert.AreEqual("S2", manifest.Server);
    }

    [TestMethod]
    public void Propagate_ManifestKeepsArtifactPathsRelativeToTheRunFolder()
    {
        Publish("artifacts/Web-folder");
        var plan = PlanWithPeer([Step("Web", "artifacts/Web-folder")]);

        Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        var step = ReadManifest().Steps.Single();
        Assert.AreEqual("artifacts/Web-folder", step.Artifact,
            "An absolute path would name a folder on the primary, which the peer has no reason to have.");

        // And it rebases to real content once the peer resolves it against its own run folder.
        Assert.IsTrue(Directory.Exists(step.RebasedTo(PeerRunFolder).Artifact));
    }

    [TestMethod]
    public void Propagate_ManifestSurvivesARoundTripWithTheStepsIntact()
    {
        Publish("artifacts/Web-folder");
        var plan = PlanWithPeer([Step("Web", "artifacts/Web-folder")]);

        Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        var step = ReadManifest().Steps.Single();
        Assert.AreEqual("Web", step.Project);
        Assert.AreEqual(DeployType.Folder, step.Type);
        Assert.IsNotNull(step.Folder);
        Assert.AreEqual(@"C:\Apps\Web", step.Folder!.DestinationPath);
        Assert.AreEqual("Web", step.Folder.ServiceName);
    }

    // -- Failures --------------------------------------------------------------

    [TestMethod]
    public void Propagate_RecordsAFailureRatherThanThrowingWhenIncomingShareIsMissing()
    {
        Publish("artifacts/Web-folder");
        var plan = PlanWithPeer([Step("Web", "artifacts/Web-folder")], incomingShare: "");

        var results = Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        Assert.IsFalse(results[0].Success);
        StringAssert.Contains(results[0].ErrorMessage!, "incomingShare");
    }

    [TestMethod]
    public void Propagate_FailsWhenAPublishedArtifactIsMissingFromStaging()
    {
        // Nothing published: the run folder for this step does not exist.
        var plan = PlanWithPeer([Step("Web", "artifacts/Web-folder")]);

        var results = Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        Assert.IsFalse(results[0].Success);
        Assert.IsFalse(Directory.Exists(PeerRunFolder), "A failed delivery must not appear in incoming.");
    }

    [TestMethod]
    public void Propagate_CleansUpItsStagingFolderWhenDeliveryFails()
    {
        var plan = PlanWithPeer([Step("Web", "artifacts/Web-folder")]);   // artifact missing

        Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        var staged = Path.Combine(_peerDeployRoot, "staging", RunId);
        Assert.IsFalse(Directory.Exists(staged),
            "A partial staging folder would poison the next attempt at this run id.");
    }

    [TestMethod]
    public void Propagate_RefusesToOverwriteARunFolderThePeerAlreadyHas()
    {
        Publish("artifacts/Web-folder");
        Directory.CreateDirectory(PeerRunFolder);
        File.WriteAllText(Path.Combine(PeerRunFolder, "marker.txt"), "in flight");

        var plan    = PlanWithPeer([Step("Web", "artifacts/Web-folder")]);
        var results = Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        Assert.IsFalse(results[0].Success);
        Assert.AreEqual("in flight", File.ReadAllText(Path.Combine(PeerRunFolder, "marker.txt")));
    }

    [TestMethod]
    public void Propagate_ReportsEachPeerIndependently()
    {
        Publish("artifacts/Web-folder");
        var steps = new[] { Step("Web", "artifacts/Web-folder") };

        var plan = new RolloutPlan
        {
            RunId       = RunId,
            WaitSeconds = 0,
            ServerPlans =
            [
                new ServerPlan { ServerName = "S1", IsSelf = true, Steps = steps.ToList() },
                new ServerPlan { ServerName = "S2", IncomingShare = _peerIncoming, Steps = steps.ToList() },
                new ServerPlan { ServerName = "S3", IncomingShare = "", Steps = steps.ToList() }
            ]
        };

        var results = Propagator.Propagate(plan, _stagingRoot, keepRuns: 5, DateTimeOffset.Now);

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.Single(r => r.ServerName == "S2").Success);
        Assert.IsFalse(results.Single(r => r.ServerName == "S3").Success,
            "One unreachable peer must not stop the others being delivered.");
    }

    // -- Retention -------------------------------------------------------------

    [TestMethod]
    public void Propagate_PrunesRunFoldersBeyondKeepRuns()
    {
        // Four older runs, oldest first by name - run ids sort chronologically by design.
        foreach (var old in new[] { "20260819-100000-aaa", "20260819-101000-bbb",
                                    "20260819-102000-ccc", "20260819-103000-ddd" })
            Directory.CreateDirectory(Path.Combine(_peerIncoming, old));

        Publish("artifacts/Web-folder");
        var plan = PlanWithPeer([Step("Web", "artifacts/Web-folder")]);

        Propagator.Propagate(plan, _stagingRoot, keepRuns: 3, DateTimeOffset.Now);

        var kept = Directory.GetDirectories(_peerIncoming).Select(Path.GetFileName).Order().ToList();
        CollectionAssert.AreEqual(
            new[] { "20260819-102000-ccc", "20260819-103000-ddd", RunId },
            kept,
            "Retention must keep the newest runs, including the one just delivered.");
    }

    [TestMethod]
    public void Propagate_WithKeepRunsZeroPrunesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_peerIncoming, "20260819-100000-aaa"));
        Publish("artifacts/Web-folder");
        var plan = PlanWithPeer([Step("Web", "artifacts/Web-folder")]);

        Propagator.Propagate(plan, _stagingRoot, keepRuns: 0, DateTimeOffset.Now);

        Assert.AreEqual(2, Directory.GetDirectories(_peerIncoming).Length,
            "keepRuns 0 means retention is off, not 'delete everything'.");
    }
}
