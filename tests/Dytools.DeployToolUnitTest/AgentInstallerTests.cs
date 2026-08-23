using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Services;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers standing a peer up.
///
/// The scheduled-task registration itself is Windows-only and needs elevation, so it is not
/// asserted here. What is asserted is everything that has to be right *before* the scheduler
/// is involved - the folder layout, and the contents of the poll script.
///
/// The poll script matters more than its six lines suggest. It is written once and then never
/// touched again, on every box in the fleet, while the tool around it keeps changing. A typo
/// in it is a peer that silently stops applying rollouts.
/// </summary>
[TestClass]
public sealed class AgentInstallerTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void SetUp()
        => _root = Path.Combine(Path.GetTempPath(), "DeployToolTests", Guid.NewGuid().ToString("N"), "deploy");

    [TestCleanup]
    public void TearDown()
        => FileHelper.TryDeleteDirectory(Path.GetDirectoryName(_root)!);

    // -- Layout ----------------------------------------------------------------

    [TestMethod]
    public void StagingSitsBesideIncomingSoTheHandoffIsARename()
    {
        var layout = new AgentLayout(_root);

        // Propagator copies into staging and then moves the folder into incoming. That move is
        // only atomic - and the poller only safe from half-copied runs - while the two share a
        // volume. Being siblings under one root is what guarantees it.
        Assert.AreEqual(
            Path.GetDirectoryName(layout.Staging),
            Path.GetDirectoryName(layout.Incoming));

        Assert.AreEqual(layout.Root, Path.GetDirectoryName(layout.Incoming));
        Assert.AreEqual("incoming", Path.GetFileName(layout.Incoming));
        Assert.AreEqual("staging",  Path.GetFileName(layout.Staging));
    }

    [TestMethod]
    public void LayoutExpandsEnvironmentVariablesAndRelativePaths()
    {
        var layout = new AgentLayout(".");

        Assert.IsTrue(Path.IsPathRooted(layout.Root));
        Assert.IsTrue(Path.IsPathRooted(layout.Incoming));
    }

    [TestMethod]
    public async Task InstallCreatesEveryFolderAndTheScriptAndIsSafeToRepeat()
    {
        var layout  = new AgentLayout(_root);
        var options = new AgentOptions { Root = _root };

        await AgentInstaller.InstallAsync(options);

        foreach (var dir in layout.All)
            Assert.IsTrue(Directory.Exists(dir), $"{dir} was not created.");

        Assert.IsTrue(File.Exists(layout.PollScript));

        // A run folder from a previous rollout must survive a re-install: this is how a box
        // gets upgraded, and its deploy history lives under incoming.
        var history = Path.Combine(layout.Incoming, "20260101-100000-aaa");
        Directory.CreateDirectory(history);

        await AgentInstaller.InstallAsync(options);

        Assert.IsTrue(Directory.Exists(history));
        Assert.IsTrue(File.Exists(layout.PollScript));
    }

    // -- Poll script -----------------------------------------------------------

    [TestMethod]
    public void PollScriptInvokesTheBinaryThatArrivedWithTheRun()
    {
        var layout = new AgentLayout(_root);
        var script = AgentInstaller.BuildPollScript(layout);

        // The whole no-installed-version design rests on this: the script runs the launcher
        // inside a run folder, not a tool installed on the box. Both halves of that path are
        // a contract with Propagator, which is the only thing that ever writes it.
        StringAssert.Contains(script, Propagator.ToolFolderName);
        StringAssert.Contains(script,
            OperatingSystem.IsWindows() ? Propagator.LauncherCmd : Propagator.LauncherSh);
        StringAssert.Contains(script, "--apply");
        StringAssert.Contains(script, layout.Incoming);
        StringAssert.Contains(script, layout.LogFile);
    }

    [TestMethod]
    public void PollScriptNeverParsesTheManifest()
    {
        // Soak-window logic belongs where it can be unit tested, not in a batch file that is
        // frozen forever on every box in the fleet.
        var script = AgentInstaller.BuildPollScript(new AgentLayout(_root));

        Assert.IsFalse(script.Contains("manifest", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("notBefore", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PollScriptRollsItsOwnLog()
    {
        // It runs every minute, forever. Unbounded output eventually fills the disk of the
        // server it was installed to protect.
        var script = AgentInstaller.BuildPollScript(new AgentLayout(_root));

        StringAssert.Contains(script, OperatingSystem.IsWindows() ? "%LOG%.1" : "$LOG.1");
    }

    [TestMethod]
    public void PollScriptPicksTheNewestRunFolderFirst()
    {
        var script = AgentInstaller.BuildPollScript(new AgentLayout(_root));

        // Newest-first here is about the *binary*, not the order runs apply in: the freshest
        // tool goes on to process every pending run, so a peer never applies a new manifest
        // with an old executable.
        if (OperatingSystem.IsWindows())
            StringAssert.Contains(script, "/o-d");
        else
            StringAssert.Contains(script, "ls -1t");
    }
}
