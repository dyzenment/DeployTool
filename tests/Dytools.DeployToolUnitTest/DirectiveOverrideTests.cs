using Dytools.DeployTool.Resolvers;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers command-line directive overrides: CommitDirectives.FromValues (building directives
/// from --pub/--wait values) and OverlaidWith (layering CLI over commit-message directives).
/// </summary>
[TestClass]
public sealed class DirectiveOverrideTests
{
    // -- FromValues ------------------------------------------------------------

    [TestMethod]
    public void FromValues_NullPubAndWait_IsEmpty()
    {
        var d = CommitDirectives.FromValues(null, null);

        Assert.IsFalse(d.HasPub);
        Assert.IsNull(d.PubPatterns);
        Assert.IsNull(d.WaitSeconds);
    }

    [TestMethod]
    public void FromValues_PubUsesSamePipeGlobSyntaxAsCommit()
    {
        var d = CommitDirectives.FromValues("Web|Proc*", null);

        CollectionAssert.AreEqual(new[] { "Web", "Proc*" }, d.PubPatterns!.ToArray());
        Assert.IsTrue(d.MatchesPub("Web"));
        Assert.IsTrue(d.MatchesPub("Processor"));
        Assert.IsFalse(d.MatchesPub("Admin"));
    }

    [TestMethod]
    public void FromValues_PubStar_MatchesEverything()
    {
        var d = CommitDirectives.FromValues("*", null);

        Assert.IsTrue(d.HasPub);
        Assert.IsTrue(d.MatchesPub("AnythingAtAll"));
    }

    [TestMethod]
    public void FromValues_PubNone_IsExplicitButMatchesNothing()
    {
        var d = CommitDirectives.FromValues("none", null);

        Assert.IsTrue(d.HasPub, "--pub none is still an explicit selection directive");
        Assert.IsFalse(d.MatchesPub("Web"));
    }

    [TestMethod]
    public void FromValues_WaitZeroIsDistinctFromAbsent()
    {
        Assert.AreEqual(0, CommitDirectives.FromValues(null, 0).WaitSeconds);
        Assert.IsNull(CommitDirectives.FromValues(null, null).WaitSeconds);
    }

    // -- OverlaidWith ----------------------------------------------------------

    [TestMethod]
    public void Overlay_CliPubReplacesCommitPub()
    {
        var commit = CommitDirectives.Parse("fix login pub:Web");
        var cli    = CommitDirectives.FromValues("Processor", null);

        var result = commit.OverlaidWith(cli);

        CollectionAssert.AreEqual(new[] { "Processor" }, result.PubPatterns!.ToArray());
        Assert.IsFalse(result.MatchesPub("Web"), "the CLI pub fully replaces the commit pub");
        Assert.IsTrue(result.MatchesPub("Processor"));
    }

    [TestMethod]
    public void Overlay_AbsentCliPubKeepsCommitPub()
    {
        var commit = CommitDirectives.Parse("pub:Web|Proc*");
        var cli    = CommitDirectives.FromValues(null, 0);   // only overrides wait

        var result = commit.OverlaidWith(cli);

        CollectionAssert.AreEqual(new[] { "Web", "Proc*" }, result.PubPatterns!.ToArray());
        Assert.AreEqual(0, result.WaitSeconds);
    }

    [TestMethod]
    public void Overlay_CliCanSupplyPubWhenCommitHasNone()
    {
        var commit = CommitDirectives.Parse("just a normal commit with no directives");
        var cli    = CommitDirectives.FromValues("*", null);

        var result = commit.OverlaidWith(cli);

        Assert.IsTrue(result.HasPub);
        Assert.IsTrue(result.MatchesPub("Web"));
    }

    [TestMethod]
    public void Overlay_CliWaitOverridesCommitWait()
    {
        var commit = CommitDirectives.Parse("pub:* wait:3600");
        var cli    = CommitDirectives.FromValues(null, 0);

        Assert.AreEqual(0, commit.OverlaidWith(cli).WaitSeconds);
    }

    [TestMethod]
    public void Overlay_AbsentCliWaitKeepsCommitWait()
    {
        var commit = CommitDirectives.Parse("pub:* wait:90");
        var cli    = CommitDirectives.FromValues("Web", null);   // only overrides pub

        Assert.AreEqual(90, commit.OverlaidWith(cli).WaitSeconds);
    }

    [TestMethod]
    public void Overlay_EmptyCliOverNothing_YieldsNothing()
    {
        var result = CommitDirectives.None.OverlaidWith(CommitDirectives.FromValues(null, null));

        Assert.IsFalse(result.HasPub);
        Assert.IsNull(result.WaitSeconds);
    }
}
