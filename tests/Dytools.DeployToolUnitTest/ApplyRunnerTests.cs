using System.Text.Json;
using Dytools.DeployTool;
using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Models.Reporting;
using Dytools.DeployTool.Services;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers the peer half of a rollout: which run folders get applied, when, and what stops
/// one being applied twice.
///
/// These run against a real incoming folder on disk, because the behaviour under test is
/// entirely about files: a manifest without a result.json is pending, a run whose soak window
/// has not passed is not, and the marker written at the end is the only thing between a
/// finished rollout and a poller re-applying it every minute forever.
///
/// Folder is the deploy type used throughout - it is the one that works on any OS, so the
/// scheduling logic is asserted without needing IIS.
/// </summary>
[TestClass]
public sealed class ApplyRunnerTests
{
    private static readonly Dictionary<DeployType, IDeployTypeHandler> Handlers = new()
    {
        [DeployType.Folder] = new FolderHandler()
    };

    private string _root = string.Empty;
    private string _incoming = string.Empty;
    private string _live = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _root     = Path.Combine(Path.GetTempPath(), "DeployToolTests", Guid.NewGuid().ToString("N"));
        _incoming = Path.Combine(_root, "incoming");
        _live     = Path.Combine(_root, "live");
        Directory.CreateDirectory(_incoming);
    }

    [TestCleanup]
    public void TearDown() => FileHelper.TryDeleteDirectory(_root);

    // -- Fixtures --------------------------------------------------------------

    /// <summary>
    /// Writes a complete run folder exactly as Propagator would leave one: artifact tree,
    /// relative artifact path in the manifest, no result.json.
    /// </summary>
    private string StageRun(
        string runId,
        DateTimeOffset notBeforeUtc,
        string payload = "build",
        string destination = "App")
    {
        var runFolder   = Path.Combine(_incoming, runId);
        var artifactRel = "artifacts/App-folder";
        var artifactDir = Path.Combine(runFolder, "artifacts", "App-folder");

        Directory.CreateDirectory(artifactDir);
        File.WriteAllText(Path.Combine(artifactDir, "app.txt"), payload);

        var manifest = new DeployManifest
        {
            RunId        = runId,
            CommitSha    = "abc1234",
            Primary      = "web01",
            Server       = "web02",
            CreatedUtc   = DateTimeOffset.UtcNow,
            NotBeforeUtc = notBeforeUtc,
            Steps =
            {
                new ApplyStep
                {
                    Project  = "App",
                    Type     = DeployType.Folder,
                    Artifact = artifactRel,
                    Label    = "Folder / App",
                    Folder   = new FolderConfig { DestinationPath = Path.Combine(_live, destination) }
                }
            }
        };

        File.WriteAllText(
            Path.Combine(runFolder, DeployManifest.FileName),
            JsonSerializer.Serialize(manifest, JsonOptions.Write));

        return runFolder;
    }

    private static bool HasResult(string runFolder)
        => File.Exists(Path.Combine(runFolder, ReportWriter.FileName));

    private static DeployReport ReadResult(string runFolder)
        => ReportWriter.TryRead(Path.Combine(runFolder, ReportWriter.FileName))
           ?? throw new AssertFailedException($"No {ReportWriter.FileName} in {runFolder}.");

    // -- Due runs --------------------------------------------------------------

    [TestMethod]
    public async Task AppliesADueRunAndWritesItsMarker()
    {
        var runFolder = StageRun("20260101-100000-aaa", DateTimeOffset.UtcNow.AddMinutes(-1));

        var exit = await ApplyRunner.RunAsync(_incoming, Handlers);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("build", File.ReadAllText(Path.Combine(_live, "App", "app.txt")));
        Assert.IsTrue(HasResult(runFolder));

        var report = ReadResult(runFolder);
        Assert.IsTrue(report.Success);
        Assert.AreEqual("20260101-100000-aaa", report.RunId);

        // The peer records the name it was planned for, not the machine it happens to be:
        // that is what makes result.json readable next to the manifest that produced it.
        Assert.AreEqual("web02", report.ServerName);
    }

    [TestMethod]
    public async Task ResultJsonStopsARunBeingAppliedTwice()
    {
        var runFolder = StageRun("20260101-100000-aaa", DateTimeOffset.UtcNow.AddMinutes(-1));

        await ApplyRunner.RunAsync(_incoming, Handlers);

        // Simulate someone changing what is live between polls. A second apply must not put
        // the artifact back - the run is finished, and the poller fires every minute forever.
        File.WriteAllText(Path.Combine(_live, "App", "app.txt"), "changed by hand");
        var firstCompletedAt = ReadResult(runFolder).CompletedAt;

        var exit = await ApplyRunner.RunAsync(_incoming, Handlers);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("changed by hand", File.ReadAllText(Path.Combine(_live, "App", "app.txt")));
        Assert.AreEqual(firstCompletedAt, ReadResult(runFolder).CompletedAt);
    }

    // -- Soak window -----------------------------------------------------------

    [TestMethod]
    public async Task LeavesARunAloneUntilItsSoakWindowPasses()
    {
        var runFolder = StageRun("20260101-100000-aaa", DateTimeOffset.UtcNow.AddHours(1));

        var exit = await ApplyRunner.RunAsync(_incoming, Handlers);

        Assert.AreEqual(0, exit, "a run that is still soaking is not a failure.");
        Assert.IsFalse(Directory.Exists(_live));

        // Critically: no marker. Writing one would mean the run never applies at all.
        Assert.IsFalse(HasResult(runFolder));
    }

    [TestMethod]
    public async Task AppliesTheSameRunOnceItsWindowHasPassed()
    {
        StageRun("20260101-100000-aaa", DateTimeOffset.UtcNow.AddMilliseconds(400));

        await ApplyRunner.RunAsync(_incoming, Handlers);
        Assert.IsFalse(Directory.Exists(_live));

        await Task.Delay(600);
        var exit = await ApplyRunner.RunAsync(_incoming, Handlers);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("build", File.ReadAllText(Path.Combine(_live, "App", "app.txt")));
    }

    // -- Ordering --------------------------------------------------------------

    [TestMethod]
    public async Task AppliesABacklogOldestFirstSoTheNewestBuildEndsUpLive()
    {
        // A peer that was offline over a weekend comes back to several pending runs. Applying
        // newest-first would leave the *oldest* build on disk.
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        StageRun("20260101-100000-aaa", due, payload: "oldest");
        StageRun("20260103-100000-ccc", due, payload: "newest");
        StageRun("20260102-100000-bbb", due, payload: "middle");

        var exit = await ApplyRunner.RunAsync(_incoming, Handlers);

        Assert.AreEqual(0, exit);
        Assert.AreEqual("newest", File.ReadAllText(Path.Combine(_live, "App", "app.txt")));
    }

    // -- Bad input -------------------------------------------------------------

    [TestMethod]
    public async Task RejectsAnUnreadableManifestOnceRatherThanRetryingForever()
    {
        var runFolder = Path.Combine(_incoming, "20260101-100000-bad");
        Directory.CreateDirectory(runFolder);
        File.WriteAllText(Path.Combine(runFolder, DeployManifest.FileName), "{ not json");

        var exit = await ApplyRunner.RunAsync(_incoming, Handlers);

        Assert.AreEqual(1, exit);

        // The marker is the point: without it the poller re-reads and re-fails this every
        // minute, and the reason is never written down anywhere.
        Assert.IsTrue(HasResult(runFolder));
        Assert.IsFalse(ReadResult(runFolder).Success);
    }

    [TestMethod]
    public async Task IgnoresFoldersThatAreNotRuns()
    {
        Directory.CreateDirectory(Path.Combine(_incoming, "notes"));
        Directory.CreateDirectory(Path.Combine(_incoming, "staging-leftovers"));

        var exit = await ApplyRunner.RunAsync(_incoming, Handlers);

        Assert.AreEqual(0, exit);
        Assert.IsFalse(HasResult(Path.Combine(_incoming, "notes")));
    }

    [TestMethod]
    public async Task MissingIncomingFolderIsTheSteadyStateNotAFailure()
    {
        // The poller reaches this every minute on a box that has never received a run.
        var exit = await ApplyRunner.RunAsync(Path.Combine(_root, "never-created"), Handlers);

        Assert.AreEqual(0, exit);
    }

    [TestMethod]
    public async Task RecordsAFailureWhenNoHandlerKnowsTheStepType()
    {
        // An older binary picking up a run written by a newer primary. It must fail loudly and
        // once, not skip the step and report success.
        var runFolder = StageRun("20260101-100000-aaa", DateTimeOffset.UtcNow.AddMinutes(-1));

        var exit = await ApplyRunner.RunAsync(_incoming, new Dictionary<DeployType, IDeployTypeHandler>());

        Assert.AreEqual(1, exit);
        Assert.IsTrue(HasResult(runFolder));

        var target = ReadResult(runFolder).Results.Single().TargetResults.Single();
        Assert.IsFalse(target.Success);
        StringAssert.Contains(target.ErrorMessage!, "No handler");
    }

    [TestMethod]
    public async Task AnEmptyManifestCompletesRatherThanStayingPendingForever()
    {
        // A peer whose every step was Global scope. Nothing to do is a success - but it still
        // needs its marker.
        var runFolder = Path.Combine(_incoming, "20260101-100000-empty");
        Directory.CreateDirectory(runFolder);

        var manifest = new DeployManifest
        {
            RunId        = "20260101-100000-empty",
            Primary      = "web01",
            Server       = "web02",
            NotBeforeUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        File.WriteAllText(
            Path.Combine(runFolder, DeployManifest.FileName),
            JsonSerializer.Serialize(manifest, JsonOptions.Write));

        var exit = await ApplyRunner.RunAsync(_incoming, Handlers);

        Assert.AreEqual(0, exit);
        Assert.IsTrue(HasResult(runFolder));
    }

    // -- Formatting ------------------------------------------------------------

    [TestMethod]
    public void RemainingSoakTimeReadsInTheLargestSensibleUnit()
    {
        Assert.AreEqual("45s",   ApplyRunner.FormatRemaining(TimeSpan.FromSeconds(45)));
        Assert.AreEqual("20m",   ApplyRunner.FormatRemaining(TimeSpan.FromMinutes(20)));
        Assert.AreEqual("1h 0m", ApplyRunner.FormatRemaining(TimeSpan.FromHours(1)));
        Assert.AreEqual("2h 30m", ApplyRunner.FormatRemaining(TimeSpan.FromMinutes(150)));

        // Days almost always mean a clock skew between primary and peer. "3d 4h" says so;
        // "4560m" does not.
        Assert.AreEqual("3d 4h", ApplyRunner.FormatRemaining(TimeSpan.FromHours(76)));
    }
}
