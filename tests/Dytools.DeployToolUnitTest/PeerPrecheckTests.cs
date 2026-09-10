using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Services;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers the check that runs before the build.
///
/// Its whole value is being right about *which* thing is broken. The SMB error for an
/// unresolvable host is the same one Windows returns for a missing share, so a naive report
/// sends people to re-create a share that already exists - which is exactly what happened
/// before this existed. Each check therefore has to fail on its own terms and stop, rather
/// than letting the next one produce a misleading answer.
///
/// A peer's "share" here is a local directory, the same trick PropagatorTests uses: the path
/// checks are identical, and no second machine is involved.
/// </summary>
[TestClass]
public sealed class PeerPrecheckTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void SetUp()
        => _root = Path.Combine(Path.GetTempPath(), "DeployToolTests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void TearDown() => FileHelper.TryDeleteDirectory(_root);

    /// <summary>A peer laid out the way install-agent leaves one.</summary>
    private string ReadyPeer()
    {
        var deployRoot = Path.Combine(_root, "peer", "deploy");
        Directory.CreateDirectory(Path.Combine(deployRoot, AgentLayout.IncomingFolderName));
        Directory.CreateDirectory(Path.Combine(deployRoot, AgentLayout.StagingFolderName));
        return Path.Combine(deployRoot, AgentLayout.IncomingFolderName);
    }

    private static PeerTarget Peer(string? share, string? user = null, string? password = null)
        => new("peer1", share, user, password);

    private static async Task<(bool Ok, string Output)> RunAsync(params PeerTarget[] peers)
    {
        var console  = new StringWriter();
        var original = Console.Out;

        try
        {
            Console.SetOut(console);
            var ok = await PeerPrecheck.RunAsync(peers);
            return (ok, console.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    // -- Passing ---------------------------------------------------------------

    [TestMethod]
    public async Task APeerWithIncomingAndStagingPasses()
    {
        var (ok, output) = await RunAsync(Peer(ReadyPeer()));

        Assert.IsTrue(ok);
        StringAssert.Contains(output, "writable");
    }

    [TestMethod]
    public async Task NoPeersIsSuccess()
        => Assert.IsTrue(await PeerPrecheck.RunAsync([]));

    [TestMethod]
    public async Task TheWriteProbeLeavesNothingBehind()
    {
        // It creates and deletes a directory under staging. A probe that leaked would
        // accumulate one entry per deploy on a share nobody tidies.
        var incoming = ReadyPeer();
        var staging  = Path.Combine(Path.GetDirectoryName(incoming)!, AgentLayout.StagingFolderName);

        await RunAsync(Peer(incoming));

        Assert.AreEqual(0, Directory.GetFileSystemEntries(staging).Length);
    }

    // -- Failing, each for its own reason --------------------------------------

    [TestMethod]
    public async Task AMissingIncomingShareIsReportedAsConfigurationNotConnectivity()
    {
        var (ok, output) = await RunAsync(Peer(null));

        Assert.IsFalse(ok);
        StringAssert.Contains(output, "no incomingShare configured");
        StringAssert.Contains(output, "install-agent");
    }

    [TestMethod]
    public async Task AnUnresolvableHostSaysSo_NotThatTheShareIsMissing()
    {
        // The distinction this whole class exists for. Windows reports both as error 67, and
        // "the share does not exist" for a share that does exist wastes an afternoon.
        var (ok, output) = await RunAsync(
            Peer(@"\\deploytool-no-such-host-42.invalid\deploy\incoming"));

        Assert.IsFalse(ok);
        StringAssert.Contains(output, "does not resolve");
        StringAssert.Contains(output, "broadcast");

        // Must not have gone on to guess about the share it never reached.
        Assert.IsFalse(output.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task AServerWithNoShareNamedIsRejected()
    {
        var (ok, output) = await RunAsync(Peer(@"\\somehost"));

        Assert.IsFalse(ok);
        StringAssert.Contains(output, "no share");
    }

    [TestMethod]
    public async Task AMissingIncomingFolderPointsAtInstallAgent()
    {
        var (ok, output) = await RunAsync(Peer(Path.Combine(_root, "nothing-here")));

        Assert.IsFalse(ok);
        StringAssert.Contains(output, "does not exist");
        StringAssert.Contains(output, "install-agent");
    }

    [TestMethod]
    public async Task IncomingWithoutStagingFailsBecauseTheHandoffNeedsBoth()
    {
        // Propagation stages beside incoming and moves across; without staging on the same
        // share the move stops being atomic. Half a layout is not a usable peer.
        var incoming = Path.Combine(_root, "peer", "deploy", AgentLayout.IncomingFolderName);
        Directory.CreateDirectory(incoming);

        var (ok, output) = await RunAsync(Peer(incoming));

        Assert.IsFalse(ok);
        StringAssert.Contains(output, AgentLayout.StagingFolderName);
    }

    // -- Reporting -------------------------------------------------------------

    [TestMethod]
    public async Task EveryPeerIsCheckedEvenAfterOneFails()
    {
        // Reporting only the first failure would mean one round trip per broken peer.
        var (ok, output) = await RunAsync(
            Peer(null),
            Peer(ReadyPeer()));

        Assert.IsFalse(ok);
        StringAssert.Contains(output, "no incomingShare configured");
        StringAssert.Contains(output, "writable");
    }
}
