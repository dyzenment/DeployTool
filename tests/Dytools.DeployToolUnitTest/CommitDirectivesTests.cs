using Dytools.DeployTool.Resolvers;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers the pub:/wait: commit directives. These cannot be exercised end-to-end without
/// creating commits, so the parser is kept pure and verified here instead.
/// </summary>
[TestClass]
public sealed class CommitDirectivesTests
{
    // -- Absence ---------------------------------------------------------------

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("Added loading information to splash")]
    public void NoDirective_YieldsNoPubAndNoWait(string? message)
    {
        var directives = CommitDirectives.Parse(message);

        Assert.IsFalse(directives.HasPub, "a message without pub: must not select anything");
        Assert.IsNull(directives.PubPatterns);
        Assert.IsNull(directives.WaitSeconds);
    }

    // -- pub: parsing ----------------------------------------------------------

    [TestMethod]
    public void Pub_SingleProject_ParsesOnePattern()
    {
        var directives = CommitDirectives.Parse("fix login pub:Web");

        Assert.IsTrue(directives.HasPub);
        CollectionAssert.AreEqual(new[] { "Web" }, directives.PubPatterns!.ToArray());
    }

    [TestMethod]
    public void Pub_PipeSeparated_ParsesEachPattern()
    {
        var directives = CommitDirectives.Parse("pub:Web|Proc*");

        CollectionAssert.AreEqual(new[] { "Web", "Proc*" }, directives.PubPatterns!.ToArray());
    }

    [TestMethod]
    public void Pub_IsCaseInsensitive()
    {
        Assert.IsTrue(CommitDirectives.Parse("PUB:Web").HasPub);
        Assert.IsTrue(CommitDirectives.Parse("Pub:Web").HasPub);
    }

    [TestMethod]
    public void Pub_FoundInMultiLineBody()
    {
        var message = "Refactor the geocoder\n\nLots of detail here.\npub:WebApi|WebApp\n";
        var directives = CommitDirectives.Parse(message);

        CollectionAssert.AreEqual(new[] { "WebApi", "WebApp" }, directives.PubPatterns!.ToArray());
    }

    [TestMethod]
    public void Pub_StopsAtWhitespace_SoTrailingProseIsNotSwallowed()
    {
        var directives = CommitDirectives.Parse("pub:Web and some other notes");

        CollectionAssert.AreEqual(new[] { "Web" }, directives.PubPatterns!.ToArray());
    }

    // -- pub: matching ---------------------------------------------------------

    [TestMethod]
    public void PubStar_MatchesEveryProject()
    {
        var directives = CommitDirectives.Parse("pub:*");

        Assert.IsTrue(directives.MatchesPub("Web"));
        Assert.IsTrue(directives.MatchesPub("Processor"));
        Assert.IsTrue(directives.MatchesPub("AnythingAtAll"));
    }

    [TestMethod]
    public void PubNone_MatchesNothing_WithoutASpecialCase()
    {
        var directives = CommitDirectives.Parse("pub:none");

        Assert.IsTrue(directives.HasPub, "pub:none is still an explicit directive");
        Assert.IsFalse(directives.MatchesPub("Web"));
        Assert.IsFalse(directives.MatchesPub("Processor"));
        Assert.IsFalse(directives.MatchesPub("NewFreightExecutive"));
    }

    [TestMethod]
    public void PubPrefixGlob_MatchesOnlyByPrefix()
    {
        var directives = CommitDirectives.Parse("pub:Proc*");

        Assert.IsTrue(directives.MatchesPub("Processor"));
        Assert.IsTrue(directives.MatchesPub("Proc"));
        Assert.IsFalse(directives.MatchesPub("CrystalProcessor"),
            "Proc* is a prefix glob - it must not match a name that merely contains Proc");
        Assert.IsFalse(directives.MatchesPub("WebApi"));
    }

    [TestMethod]
    public void PubMatching_IsCaseInsensitive()
    {
        var directives = CommitDirectives.Parse("pub:web");

        Assert.IsTrue(directives.MatchesPub("Web"));
        Assert.IsTrue(directives.MatchesPub("WEB"));
    }

    [TestMethod]
    public void PubExact_DoesNotMatchByPrefixWithoutAGlob()
    {
        var directives = CommitDirectives.Parse("pub:Web");

        Assert.IsTrue(directives.MatchesPub("Web"));
        Assert.IsFalse(directives.MatchesPub("WebApi"),
            "an unglobbed pattern must be an exact name match");
        Assert.IsFalse(directives.MatchesPub("WebApp"));
    }

    [TestMethod]
    public void PubMultiplePatterns_MatchesIfAnyMatches()
    {
        var directives = CommitDirectives.Parse("pub:Web|Proc*");

        Assert.IsTrue(directives.MatchesPub("Web"));
        Assert.IsTrue(directives.MatchesPub("Processor"));
        Assert.IsFalse(directives.MatchesPub("Admin"));
    }

    [TestMethod]
    public void NoPubDirective_MatchesNothing()
    {
        var directives = CommitDirectives.Parse("just a normal commit");

        Assert.IsFalse(directives.MatchesPub("Web"));
    }

    // -- wait: parsing ---------------------------------------------------------

    [TestMethod]
    [DataRow("wait:0", 0)]
    [DataRow("wait:60", 60)]
    [DataRow("wait:3600", 3600)]
    [DataRow("fix thing wait:90 more notes", 90)]
    public void Wait_ParsesSeconds(string message, int expected)
    {
        Assert.AreEqual(expected, CommitDirectives.Parse(message).WaitSeconds);
    }

    [TestMethod]
    public void Wait_ZeroIsDistinctFromAbsent()
    {
        Assert.AreEqual(0, CommitDirectives.Parse("pub:* wait:0").WaitSeconds,
            "wait:0 means deploy to peers immediately - it must not read as 'no directive'");
        Assert.IsNull(CommitDirectives.Parse("pub:*").WaitSeconds);
    }

    [TestMethod]
    public void Wait_NonNumericIsIgnored()
    {
        Assert.IsNull(CommitDirectives.Parse("wait:soon").WaitSeconds);
    }

    [TestMethod]
    public void PubAndWait_CoexistInOneMessage()
    {
        var directives = CommitDirectives.Parse("hotfix pub:WebApi wait:0");

        CollectionAssert.AreEqual(new[] { "WebApi" }, directives.PubPatterns!.ToArray());
        Assert.AreEqual(0, directives.WaitSeconds);
    }

    // -- srv: ------------------------------------------------------------------

    [TestMethod]
    public void NoSrvDirectiveMeansEveryServer()
    {
        // Absence and "srv:*" have to behave identically, or every existing config would
        // suddenly deploy nowhere.
        var directives = CommitDirectives.Parse("just a normal commit message");

        Assert.IsFalse(directives.HasSrv);
        Assert.IsTrue(directives.MatchesSrv("S1"));
        Assert.IsTrue(directives.MatchesSrv("anything-at-all"));
    }

    [TestMethod]
    public void SrvSelectsByNameAndGlob()
    {
        var directives = CommitDirectives.Parse("hotfix srv:S2|web*");

        Assert.IsTrue(directives.HasSrv);
        Assert.IsTrue(directives.MatchesSrv("S2"));
        Assert.IsTrue(directives.MatchesSrv("web01"));
        Assert.IsTrue(directives.MatchesSrv("WEB99"), "matching is case-insensitive");
        Assert.IsFalse(directives.MatchesSrv("S1"));
        Assert.IsFalse(directives.MatchesSrv("db01"));
    }

    [TestMethod]
    public void SrvAlsoMatchesTheHostname()
    {
        // Both the label and the hostname appear in the output someone reads before writing
        // the directive, so neither is obviously "the name" to type.
        var directives = CommitDirectives.Parse("srv:EC2AMAZ-BKBMCL6");

        Assert.IsTrue(directives.MatchesSrv("S1", "EC2AMAZ-BKBMCL6"));
        Assert.IsFalse(directives.MatchesSrv("S2", "EC2AMAZ-FFQRJ6U"));
    }

    [TestMethod]
    public void SrvStarSelectsEverything()
    {
        var directives = CommitDirectives.Parse("srv:*");

        Assert.IsTrue(directives.HasSrv);
        Assert.IsTrue(directives.MatchesSrv("S1"));
        Assert.IsTrue(directives.MatchesSrv("S2"));
    }

    [TestMethod]
    public void SrvAndPubCoexistInOneMessage()
    {
        var directives = CommitDirectives.Parse("hotfix pub:Proc* srv:S2 wait:0");

        Assert.IsTrue(directives.MatchesPub("Processor"));
        Assert.IsFalse(directives.MatchesPub("Web"));
        Assert.IsTrue(directives.MatchesSrv("S2"));
        Assert.IsFalse(directives.MatchesSrv("S1"));
        Assert.AreEqual(0, directives.WaitSeconds);
    }

    [TestMethod]
    public void CommandLineSrvOverridesTheCommit()
    {
        var commit    = CommitDirectives.Parse("srv:S1");
        var overrides = CommitDirectives.FromValues(null, null, null, srvRaw: "S2");

        var merged = commit.OverlaidWith(overrides);

        Assert.IsTrue(merged.MatchesSrv("S2"));
        Assert.IsFalse(merged.MatchesSrv("S1"));
    }

    [TestMethod]
    public void AnAbsentCommandLineSrvLeavesTheCommitAlone()
    {
        // The same three-state rule the other directives follow: not specified must not read
        // as "every server" and silently widen a deliberately narrowed rollout.
        var commit = CommitDirectives.Parse("srv:S1");
        var merged = commit.OverlaidWith(CommitDirectives.FromValues("Web", null));

        Assert.IsTrue(merged.MatchesSrv("S1"));
        Assert.IsFalse(merged.MatchesSrv("S2"));
    }
}
