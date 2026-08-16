using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Plan;
using Dytools.DeployTool.Resolvers;
using Dytools.DeployTool.Services;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers the skiptests directive across all three of its entry points: the commit message,
/// the --skip-tests command-line switch, and the overlay that decides which of the two wins.
///
/// The three-state (bool?) shape is what makes the overlay work, so most of what is asserted
/// here is the difference between "unspecified" and "explicitly false".
/// </summary>
[TestClass]
public sealed class SkipTestsDirectiveTests
{
    // -- Commit message --------------------------------------------------------

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Fixed the loading spinner")]
    [DataRow("pub:Web wait:60")]
    public void NoDirective_LeavesSkipTestsUnspecified(string? message)
    {
        Assert.IsNull(CommitDirectives.Parse(message).SkipTests,
            "absence must stay null so a CLI override can still decide");
    }

    [TestMethod]
    [DataRow("hotfix skiptests")]
    [DataRow("hotfix skip-tests")]
    [DataRow("hotfix skip_tests")]
    [DataRow("SKIPTESTS all caps")]
    [DataRow("skiptests")]
    [DataRow("line one\nskiptests\nline three")]
    public void BareDirective_Skips(string message)
    {
        Assert.AreEqual(true, CommitDirectives.Parse(message).SkipTests);
    }

    [TestMethod]
    public void ExplicitTrue_Skips()
        => Assert.AreEqual(true, CommitDirectives.Parse("rebuild skiptests:true").SkipTests);

    [TestMethod]
    public void ExplicitFalse_RunsTheGate()
        => Assert.AreEqual(false, CommitDirectives.Parse("rebuild skiptests:false").SkipTests);

    [TestMethod]
    [DataRow("skiptestsuite was renamed")]
    [DataRow("noskiptests")]
    public void SubstringOfALongerWord_DoesNotTrigger(string message)
    {
        Assert.IsNull(CommitDirectives.Parse(message).SkipTests,
            "a word boundary must be required, or ordinary prose would disable the gate");
    }

    [TestMethod]
    public void CoexistsWithTheOtherDirectives()
    {
        var d = CommitDirectives.Parse("urgent fix pub:Web|Api wait:0 skiptests");

        CollectionAssert.AreEqual(new[] { "Web", "Api" }, d.PubPatterns!.ToArray());
        Assert.AreEqual(0, d.WaitSeconds);
        Assert.AreEqual(true, d.SkipTests);
    }

    // -- Command line ----------------------------------------------------------

    [TestMethod]
    public void FlagAbsent_IsUnspecified()
    {
        var args = Args.Parse(["--config", "deploy-config.json"]);

        Assert.IsNull(args.GetOptionalBool("skip-tests"));
    }

    [TestMethod]
    public void BareFlagAtEndOfLine_IsTrue()
    {
        var args = Args.Parse(["--config", "deploy-config.json", "--skip-tests"]);

        Assert.AreEqual(true, args.GetOptionalBool("skip-tests"));
    }

    [TestMethod]
    public void BareFlagFollowedByAnotherSwitch_IsTrue()
    {
        var args = Args.Parse(["--skip-tests", "--pub", "Web"]);

        Assert.AreEqual(true, args.GetOptionalBool("skip-tests"));
        Assert.AreEqual("Web", args.GetOptional("pub"), "the next switch must keep its own value");
    }

    [TestMethod]
    [DataRow("true")]
    [DataRow("TRUE")]
    public void ExplicitTrueValue_IsTrue(string value)
        => Assert.AreEqual(true, Args.Parse(["--skip-tests", value]).GetOptionalBool("skip-tests"));

    [TestMethod]
    public void ExplicitFalseValue_IsFalse()
        => Assert.AreEqual(false, Args.Parse(["--skip-tests", "false"]).GetOptionalBool("skip-tests"));

    [TestMethod]
    public void ExistingValueArgumentsStillParse()
    {
        var args = Args.Parse(["--config", "c.json", "--changed", "a|b", "--wait", "60", "--force-all", "true"]);

        Assert.AreEqual("c.json", args.GetRequired("config"));
        Assert.AreEqual("a|b", args.GetOptional("changed"));
        Assert.AreEqual(60, args.GetOptionalInt("wait"));
        Assert.IsTrue(args.GetFlag("force-all"));
    }

    // -- Overlay ---------------------------------------------------------------

    [TestMethod]
    public void CommitSkips_AndNoFlagGiven_StillSkips()
    {
        var result = CommitDirectives.Parse("hotfix skiptests")
            .OverlaidWith(CommitDirectives.FromValues(null, null, null));

        Assert.AreEqual(true, result.SkipTests,
            "a run without the flag must not silently countermand the commit");
    }

    [TestMethod]
    public void FlagFalse_CountermandsACommitThatSkips()
    {
        var result = CommitDirectives.Parse("hotfix skiptests")
            .OverlaidWith(CommitDirectives.FromValues(null, null, false));

        Assert.AreEqual(false, result.SkipTests);
    }

    [TestMethod]
    public void FlagTrue_SkipsEvenWhenTheCommitSaidNothing()
    {
        var result = CommitDirectives.Parse("ordinary commit")
            .OverlaidWith(CommitDirectives.FromValues(null, null, true));

        Assert.AreEqual(true, result.SkipTests);
    }

    [TestMethod]
    public void OverlayLeavesTheOtherDirectivesAlone()
    {
        var result = CommitDirectives.Parse("pub:Web wait:30")
            .OverlaidWith(CommitDirectives.FromValues(null, null, true));

        CollectionAssert.AreEqual(new[] { "Web" }, result.PubPatterns!.ToArray());
        Assert.AreEqual(30, result.WaitSeconds);
        Assert.AreEqual(true, result.SkipTests);
    }

    // -- Plan ------------------------------------------------------------------
    //
    // The plan is what the deploy actually reads at test time, so the directive has to survive
    // the trip through the planner rather than only parsing correctly.

    [TestMethod]
    public void UnspecifiedDirective_PlansTheGateOn()
    {
        Assert.IsFalse(PlanWith(CommitDirectives.None).SkipTests,
            "the gate stays on unless something explicitly asked otherwise");
    }

    [TestMethod]
    public void SkipDirective_ReachesThePlan()
    {
        Assert.IsTrue(PlanWith(CommitDirectives.Parse("hotfix skiptests")).SkipTests);
    }

    [TestMethod]
    public void ExplicitFalseDirective_PlansTheGateOn()
    {
        Assert.IsFalse(PlanWith(CommitDirectives.Parse("rebuild skiptests:false")).SkipTests);
    }

    private static RolloutPlan PlanWith(CommitDirectives directives)
    {
        var target = new TargetConfig
        {
            Type   = DeployType.Folder,
            Folder = new FolderConfig { DestinationPath = @"C:\Apps\Web", ServiceName = "MyWeb" }
        };

        var project = new DiscoveredProject
        {
            Name           = "Web",
            Folder         = "/repo/src/Web",
            RelativeFolder = "src/Web",
            CsprojPath     = "/repo/src/Web/Web.csproj",
            AssemblyName   = "Web",
            Config         = new ProjectConfig { Name = "Web", Targets = [target] }
        };

        var handlers = new Dictionary<DeployType, IDeployTypeHandler>
        {
            [DeployType.Folder] = new FolderHandler()
        };

        return Planner.Plan("run-1", "abc1234", [project], new DeployConfig(), handlers,
                            "ANYBOX", directives, "changed files");
    }
}
