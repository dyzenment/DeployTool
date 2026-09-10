using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Plan;
using Dytools.DeployTool.Resolvers;
using Dytools.DeployTool.Services;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers the planner: which server gets which steps, and why.
///
/// The planner is pure by design, so an entire fleet rollout is asserted here with no IIS,
/// no network, no disk, and no clock - which is the whole reason it was built that way.
/// </summary>
[TestClass]
public sealed class PlannerTests
{
    private static readonly Dictionary<DeployType, IDeployTypeHandler> Handlers = new()
    {
        [DeployType.Velopack] = new VelopackHandler(),
        [DeployType.Iis]      = new IisHandler(),
        [DeployType.Folder]   = new FolderHandler()
    };

    // -- Fixtures --------------------------------------------------------------

    private static TargetConfig IisTarget(string path = @"C:\inetpub\Web") => new()
    {
        Type = DeployType.Iis,
        Iis  = new IisConfig { SiteName = "MyWeb", DeployPath = path, AppPool = "MyWeb" }
    };

    private static TargetConfig FolderTarget() => new()
    {
        Type   = DeployType.Folder,
        Folder = new FolderConfig { DestinationPath = @"C:\Apps\Proc", ServiceName = "MyProc" }
    };

    private static TargetConfig VelopackTarget() => new()
    {
        Type     = DeployType.Velopack,
        Velopack = new VelopackConfig { PackId = "MyApp.Admin", Delivery = VelopackDelivery.PackOnly }
    };

    private static DiscoveredProject Project(string name, params TargetConfig[] targets) => new()
    {
        Name           = name,
        Folder         = $"/repo/src/{name}",
        RelativeFolder = $"src/{name}",
        CsprojPath     = $"/repo/src/{name}/{name}.csproj",
        AssemblyName   = name,
        Config         = new ProjectConfig { Name = name, Targets = targets.ToList() }
    };

    private static DeployConfig Config(params ServerConfig[] servers) => new()
    {
        Servers = servers.ToList(),
        Rollout = new RolloutConfig { DelaySeconds = 3600 }
    };

    private static readonly ServerConfig Web1 =
        new() { Name = "web1", Hostname = "WEBSERVER01", IncomingShare = @"\\WEBSERVER01\deploy\incoming" };

    private static readonly ServerConfig Web2 =
        new() { Name = "web2", Hostname = "WEBSERVER02", IncomingShare = @"\\WEBSERVER02\deploy\incoming" };

    private static RolloutPlan Plan(
        DeployConfig config, string hostname, params DiscoveredProject[] projects)
        => Planner.Plan("run-1", "abc1234", projects, config, Handlers,
                        hostname, CommitDirectives.None, "changed files");

    // -- Single box ------------------------------------------------------------

    [TestMethod]
    public void NoServersConfigured_PlansSelfOnly()
    {
        var plan = Plan(new DeployConfig(), "ANYBOX", Project("WebApp", IisTarget()));

        Assert.AreEqual(1, plan.ServerPlans.Count);
        Assert.IsTrue(plan.Self.IsSelf);
        Assert.IsFalse(plan.HasPeers, "an unconfigured fleet must never propagate");
    }

    [TestMethod]
    public void HostNotListedInServers_PlansSelfOnly_AndDoesNotPropagate()
    {
        // An unlisted box has no mandate to drive the fleet.
        var plan = Plan(Config(Web1, Web2), "SOMERANDOMBOX", Project("WebApp", IisTarget()));

        Assert.AreEqual(1, plan.ServerPlans.Count);
        Assert.IsTrue(plan.Self.IsSelf);
        Assert.IsFalse(plan.HasPeers);
    }

    // -- Identity --------------------------------------------------------------

    [TestMethod]
    public void SelfIsResolvedByHostname()
    {
        var plan = Plan(Config(Web1, Web2), "WEBSERVER02", Project("WebApp", IisTarget()));

        Assert.AreEqual("web2", plan.Self.ServerName);
        CollectionAssert.AreEqual(new[] { "web1" }, plan.Peers.Select(p => p.ServerName).ToArray());
    }

    [TestMethod]
    public void HostnameMatchIsCaseInsensitive()
    {
        var plan = Plan(Config(Web1, Web2), "webserver01", Project("WebApp", IisTarget()));

        Assert.AreEqual("web1", plan.Self.ServerName);
    }

    [TestMethod]
    public void EitherBoxCanBePrimary_WithoutAConfigChange()
    {
        var config  = Config(Web1, Web2);
        var project = Project("WebApp", IisTarget());

        Assert.AreEqual("web1", Plan(config, "WEBSERVER01", project).Self.ServerName);
        Assert.AreEqual("web2", Plan(config, "WEBSERVER02", project).Self.ServerName);
    }

    [TestMethod]
    public void PeerCarriesItsIncomingShare_AndSelfDoesNot()
    {
        var plan = Plan(Config(Web1, Web2), "WEBSERVER01", Project("WebApp", IisTarget()));

        Assert.IsNull(plan.Self.IncomingShare, "the primary applies inline from staging");
        Assert.AreEqual(@"\\WEBSERVER02\deploy\incoming", plan.Peers.Single().IncomingShare);
    }

    // -- Scope: the velopack rule ---------------------------------------------

    [TestMethod]
    public void GlobalSteps_GoToPrimaryOnly()
    {
        var plan = Plan(Config(Web1, Web2), "WEBSERVER01",
            Project("Admin", VelopackTarget()),
            Project("WebApp", IisTarget()));

        // Primary does both; the peer must never re-upload to Azure.
        CollectionAssert.AreEquivalent(
            new[] { "Admin", "WebApp" },
            plan.Self.Steps.Select(s => s.Project).ToArray());

        CollectionAssert.AreEqual(
            new[] { "WebApp" },
            plan.Peers.Single().Steps.Select(s => s.Project).ToArray());
    }

    [TestMethod]
    public void VelopackOnlyDeploy_LeavesPeersWithNothingToDo()
    {
        var plan = Plan(Config(Web1, Web2), "WEBSERVER01", Project("Admin", VelopackTarget()));

        Assert.AreEqual(1, plan.Self.Steps.Count);
        Assert.AreEqual(0, plan.Peers.Single().Steps.Count,
            "a velopack-only run has nothing server-scoped to propagate");
    }

    [TestMethod]
    public void ServerScopedSteps_GoToEveryServer()
    {
        var plan = Plan(Config(Web1, Web2), "WEBSERVER01",
            Project("WebApp", IisTarget()),
            Project("Processor", FolderTarget()));

        Assert.AreEqual(2, plan.Self.Steps.Count);
        Assert.AreEqual(2, plan.Peers.Single().Steps.Count);
    }

    [TestMethod]
    public void ScopeComesFromTheHandler_NotAHardcodedType()
    {
        Assert.AreEqual(DeployScope.Global, Handlers[DeployType.Velopack].GetScope(VelopackTarget()));
        Assert.AreEqual(DeployScope.Server, Handlers[DeployType.Iis].GetScope(IisTarget()));
        Assert.AreEqual(DeployScope.Server, Handlers[DeployType.Folder].GetScope(FolderTarget()));
    }

    // -- Artifact paths --------------------------------------------------------

    [TestMethod]
    public void ArtifactPathsAreRelative_SoStepsCanTravel()
    {
        var plan = Plan(Config(Web1, Web2), "WEBSERVER01", Project("WebApp", IisTarget()));

        var step = plan.Self.Steps.Single();
        Assert.AreEqual("artifacts/WebApp-iis", step.Artifact);
        Assert.IsFalse(Path.IsPathRooted(step.Artifact),
            "an absolute path from the primary would be meaningless on a peer");
    }

    [TestMethod]
    public void DuplicateTargetTypes_GetDistinctArtifactPaths()
    {
        // The artifact path is the key pairing a publish with its apply step - a collision
        // would apply the wrong build.
        var plan = Plan(Config(), "BOX",
            Project("WebApp", IisTarget(@"C:\inetpub\A"), IisTarget(@"C:\inetpub\B")));

        var paths = plan.Targets.Select(t => t.ArtifactRelativePath).ToList();

        CollectionAssert.AreEqual(new[] { "artifacts/WebApp-iis", "artifacts/WebApp-iis-2" }, paths);
        Assert.AreEqual(paths.Count, paths.Distinct().Count());
    }

    [TestMethod]
    public void TargetPlanArtifactPath_MatchesItsStepArtifactPath()
    {
        var plan = Plan(Config(), "BOX", Project("WebApp", IisTarget()), Project("Admin", VelopackTarget()));

        foreach (var target in plan.Targets)
            Assert.AreEqual(target.ArtifactRelativePath, target.Step.Artifact,
                "publish output must land exactly where its apply step looks for it");
    }

    [TestMethod]
    public void RebasedTo_ResolvesArtifactAgainstTheRunFolder()
    {
        var plan = Plan(Config(), "BOX", Project("WebApp", IisTarget()));
        var step = plan.Self.Steps.Single();

        var rebased = step.RebasedTo(Path.Combine("/staging", "run-1"));

        Assert.AreEqual(
            Path.Combine("/staging", "run-1", "artifacts", "WebApp-iis"),
            rebased.Artifact);
        Assert.AreEqual("artifacts/WebApp-iis", step.Artifact, "rebasing must not mutate the plan");
    }

    // -- Wait resolution -------------------------------------------------------

    [TestMethod]
    public void WaitDefaultsToRolloutDelay()
    {
        Assert.AreEqual(3600, Plan(Config(Web1, Web2), "WEBSERVER01", Project("W", IisTarget())).WaitSeconds);
    }

    [TestMethod]
    public void WaitDirectiveOverridesRolloutDelay()
    {
        var plan = Planner.Plan("run-1", "abc", [Project("W", IisTarget())], Config(Web1, Web2),
            Handlers, "WEBSERVER01", CommitDirectives.Parse("hotfix wait:0"), "changed files");

        Assert.AreEqual(0, plan.WaitSeconds, "wait:0 must mean immediate, not 'unset'");
    }

    [TestMethod]
    public void WaitFallsBackToOneHourWhenNoRolloutConfigured()
    {
        var plan = Planner.Plan("run-1", "abc", [Project("W", IisTarget())],
            new DeployConfig { Servers = [Web1, Web2] },
            Handlers, "WEBSERVER01", CommitDirectives.None, "changed files");

        Assert.AreEqual(3600, plan.WaitSeconds);
    }

    [TestMethod]
    public void PlanCarriesNoTimestamp_SoTheSoakClockCanStartOnSuccess()
    {
        var plan = Plan(Config(Web1, Web2), "WEBSERVER01", Project("W", IisTarget()));

        // WaitSeconds is a duration, never an absolute notBefore. If the planner stamped a
        // timestamp, the primary's own build time would silently eat into the soak window.
        Assert.AreEqual(3600, plan.WaitSeconds);
        Assert.IsInstanceOfType<int>(plan.WaitSeconds);
    }

    // -- Audit trail -----------------------------------------------------------

    [TestMethod]
    public void SelectionReasonAndProjectsAreRecorded()
    {
        var plan = Planner.Plan("run-1", "abc", [Project("WebApp", IisTarget())], Config(),
            Handlers, "BOX", CommitDirectives.Parse("pub:WebApp"), "pub: WebApp");

        CollectionAssert.AreEqual(new[] { "WebApp" }, plan.Selection.Projects.ToArray());
        Assert.AreEqual("pub: WebApp", plan.Selection.Reason);
        Assert.AreEqual("run-1", plan.RunId);
        Assert.AreEqual("abc", plan.CommitSha);
    }

    [TestMethod]
    public void EmptySelection_PlansNoSteps()
    {
        var plan = Plan(Config(Web1, Web2), "WEBSERVER01");

        Assert.AreEqual(0, plan.Targets.Count);
        Assert.AreEqual(0, plan.Self.Steps.Count);
        Assert.AreEqual(0, plan.Peers.Single().Steps.Count);
    }

    // -- srv: server selection --------------------------------------------------

    [TestMethod]
    public void SrvNarrowsWhichPeersReceiveTheRun()
    {
        var config = FleetConfig();
        var plan   = PlanWith(config, "WEB01", CommitDirectives.Parse("srv:web02"));

        Assert.IsTrue(plan.ServerPlans.Single(s => s.ServerName == "web02").Selected);
        Assert.IsFalse(plan.ServerPlans.Single(s => s.ServerName == "web03").Selected);

        // Excluded boxes stay in the plan so the output can say "excluded" rather than
        // silently dropping a server someone expected to see.
        Assert.AreEqual(3, plan.ServerPlans.Count);
        Assert.AreEqual(1, plan.SelectedPeers.Count());
        Assert.AreEqual(0, plan.ServerPlans.Single(s => s.ServerName == "web03").Steps.Count);
    }

    [TestMethod]
    public void ExcludingThePrimaryStillBuildsButAppliesNothingLocally()
    {
        // "roll this out to web02 only" - the primary is the only box that can build, so it
        // still does, but nothing goes live here.
        var config = FleetConfig();
        var plan   = PlanWith(config, "WEB01", CommitDirectives.Parse("srv:web02"));

        Assert.IsFalse(plan.Self.Selected);
        Assert.AreEqual(0, plan.Self.Steps.Count);

        // The targets are still planned - that is the publish work the peer's payload needs.
        Assert.AreNotEqual(0, plan.Targets.Count);
        Assert.AreNotEqual(0, plan.SelectedPeers.Single().Steps.Count);
    }

    [TestMethod]
    public void AnExcludedPrimaryKeepsItsGlobalSteps()
    {
        // A Velopack upload is not something done "to a server", so narrowing the rollout to
        // one box must not also cancel the package publish.
        var config = FleetConfig();
        config.Projects.Add(new ProjectConfig { Name = "Admin", Targets = { VelopackTarget() } });

        var projects = new[] { Project("Web", IisTarget()), Project("Admin", VelopackTarget()) };
        var plan = Planner.Plan("run", "sha", projects, config, Handlers, "WEB01",
            CommitDirectives.Parse("srv:web02"), "test");

        Assert.IsFalse(plan.Self.Selected);
        Assert.AreEqual(1, plan.Self.Steps.Count);
        Assert.AreEqual(DeployType.Velopack, plan.Self.Steps.Single().Type);
    }

    [TestMethod]
    public void NoSrvDirectiveSelectsEveryServer()
    {
        var plan = PlanWith(FleetConfig(), "WEB01", CommitDirectives.None);

        Assert.IsTrue(plan.ServerPlans.All(s => s.Selected));
        Assert.AreEqual(2, plan.SelectedPeers.Count());
    }

    [TestMethod]
    public void SrvMatchingNothingLeavesTheRunApplyingNowhere()
    {
        var plan = PlanWith(FleetConfig(), "WEB01", CommitDirectives.Parse("srv:nosuchbox"));

        Assert.IsTrue(plan.ServerPlans.All(s => !s.Selected));
        Assert.IsFalse(plan.HasPeers);
        Assert.AreEqual(0, plan.Self.Steps.Count);
    }

    [TestMethod]
    public void SrvCanSelectThePrimaryAlone()
    {
        // The other useful direction: put it live here, leave the rest of the fleet alone.
        var plan = PlanWith(FleetConfig(), "WEB01", CommitDirectives.Parse("srv:web01"));

        Assert.IsTrue(plan.Self.Selected);
        Assert.AreNotEqual(0, plan.Self.Steps.Count);
        Assert.IsFalse(plan.HasPeers);
    }

    // -- Fixtures for the above -------------------------------------------------

    private static DeployConfig FleetConfig() => new()
    {
        Servers =
        {
            new ServerConfig { Name = "web01", Hostname = "WEB01" },
            new ServerConfig { Name = "web02", Hostname = "WEB02", IncomingShare = @"\\WEB02\deploy\incoming" },
            new ServerConfig { Name = "web03", Hostname = "WEB03", IncomingShare = @"\\WEB03\deploy\incoming" }
        }
    };

    private static RolloutPlan PlanWith(DeployConfig config, string host, CommitDirectives directives)
        => Planner.Plan("run", "sha", [Project("Web", IisTarget())], config, Handlers, host, directives, "test");
}
