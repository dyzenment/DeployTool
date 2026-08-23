using Dytools.DeployTool;
using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers which servers[] entry a box decides it is.
///
/// The failure this guards against is quiet: a box that matches nothing still deploys itself
/// perfectly well and simply propagates to nobody, while a box that matches the *wrong* entry
/// applies another server's plan. Neither shows up as a red build, so the matching rules are
/// pinned here rather than discovered in production.
/// </summary>
[TestClass]
public sealed class HostIdentityTests
{
    private static ServerConfig Server(string name, string hostname, string? share = null)
        => new() { Name = name, Hostname = hostname, IncomingShare = share };

    private static readonly List<ServerConfig> Fleet =
    [
        Server("web01", "WEB01.corp.local"),
        Server("web02", "WEB02.corp.local")
    ];

    // -- Resolve ---------------------------------------------------------------

    [TestMethod]
    public void Resolve_DefaultsToMachineName()
        => Assert.AreEqual(Environment.MachineName, HostIdentity.Resolve());

    [TestMethod]
    public void Resolve_CliOverrideWins()
    {
        Assert.AreEqual("web09", HostIdentity.Resolve("web09"));
        Assert.AreEqual("--server", HostIdentity.ResolveSource("web09"));
    }

    [TestMethod]
    public void Resolve_TrimsAndIgnoresBlankOverride()
    {
        Assert.AreEqual("web09", HostIdentity.Resolve("  web09  "));

        // A CI expression that expanded to nothing must read as "not specified" rather than
        // as an empty identity that matches nothing.
        Assert.AreEqual(Environment.MachineName, HostIdentity.Resolve("   "));
        Assert.AreEqual("machine name", HostIdentity.ResolveSource("   "));
    }

    [TestMethod]
    public void Resolve_FallsBackToEnvironmentVariable()
    {
        try
        {
            Environment.SetEnvironmentVariable(HostIdentity.EnvVariable, "web07");

            Assert.AreEqual("web07", HostIdentity.Resolve());
            Assert.AreEqual(HostIdentity.EnvVariable, HostIdentity.ResolveSource());

            // ...but the command line still outranks it.
            Assert.AreEqual("web09", HostIdentity.Resolve("web09"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostIdentity.EnvVariable, null);
        }
    }

    // -- Find ------------------------------------------------------------------

    [TestMethod]
    public void Find_MatchesHostnameExactly_CaseInsensitively()
    {
        var match = HostIdentity.Find(Fleet, "web02.CORP.local");

        Assert.IsNotNull(match);
        Assert.AreEqual("web02", match.Server.Name);
        Assert.AreEqual("hostname", match.Reason);
    }

    [TestMethod]
    public void Find_MatchesServerLabel()
    {
        // The label is what appears in logs and in --pub output, so it is what someone reaches
        // for when pinning a box by hand.
        var match = HostIdentity.Find(Fleet, "WEB01");

        Assert.IsNotNull(match);
        Assert.AreEqual("web01", match.Server.Name);
        Assert.AreEqual("name", match.Reason);
    }

    [TestMethod]
    public void Find_MatchesShortNameAgainstFqdn()
    {
        // The realistic case: Windows reports the NetBIOS name, config carries the FQDN.
        var fleet = new List<ServerConfig> { Server("primary", "WEB03.corp.local") };

        var match = HostIdentity.Find(fleet, "web03");

        Assert.IsNotNull(match);
        Assert.AreEqual("primary", match.Server.Name);
        Assert.AreEqual("short name", match.Reason);
    }

    [TestMethod]
    public void Find_MatchesFqdnAgainstShortHostname()
    {
        // ...and the same thing the other way round.
        var fleet = new List<ServerConfig> { Server("primary", "WEB03") };

        var match = HostIdentity.Find(fleet, "web03.corp.local");

        Assert.IsNotNull(match);
        Assert.AreEqual("short name", match.Reason);
    }

    [TestMethod]
    public void Find_ExactHostnameBeatsShortNameElsewhereInTheList()
    {
        // Both entries are short-name candidates for "web01". Ordering the passes is what
        // stops the lenient rule from stealing a box that has an exact match available.
        var fleet = new List<ServerConfig>
        {
            Server("staging", "web01.staging.local"),
            Server("prod",    "web01")
        };

        var match = HostIdentity.Find(fleet, "web01");

        Assert.IsNotNull(match);
        Assert.AreEqual("prod", match.Server.Name);
        Assert.AreEqual("hostname", match.Reason);
    }

    [TestMethod]
    public void Find_ReturnsNullWhenUnlisted()
        => Assert.IsNull(HostIdentity.Find(Fleet, "laptop-42"));

    [TestMethod]
    public void Find_ReturnsNullForEmptyFleet()
        => Assert.IsNull(HostIdentity.Find([], "web01"));

    [TestMethod]
    public void Find_IgnoresEntriesWithNoHostname()
    {
        // A half-filled entry must not become a wildcard that swallows every box: "" would
        // otherwise short-name-match "" for anything unnamed.
        var fleet = new List<ServerConfig> { Server("unconfigured", string.Empty) };

        Assert.IsNull(HostIdentity.Find(fleet, "web01"));
    }

    [TestMethod]
    public void Find_ThrowsWhenTwoEntriesClaimTheSameBox()
    {
        var fleet = new List<ServerConfig>
        {
            Server("web01-a", "WEB01.corp.local"),
            Server("web01-b", "web01.corp.local")
        };

        var ex = Assert.ThrowsException<DeployException>(() => HostIdentity.Find(fleet, "WEB01.corp.local"));

        StringAssert.Contains(ex.Message, "web01-a");
        StringAssert.Contains(ex.Message, "web01-b");
        StringAssert.Contains(ex.Message, HostIdentity.EnvVariable);
    }

    // -- Describe --------------------------------------------------------------

    [TestMethod]
    public void Describe_AlwaysReportsMachineNameAndTheOverrideVariable()
    {
        var described = HostIdentity.Describe();

        Assert.IsTrue(described.Any(d => d.Source == "machine name" && d.Value == Environment.MachineName));
        Assert.IsTrue(described.Any(d => d.Source.StartsWith(HostIdentity.EnvVariable, StringComparison.Ordinal)));
    }
}
