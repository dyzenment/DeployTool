using Dytools.DeployTool;
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

    private static TargetConfig IisTarget(string path = @"C:\inetpub\Web", BuildConfig? build = null) => new()
    {
        Type  = DeployType.Iis,
        Build = build,
        Iis   = new IisConfig { SiteName = "MyWeb", DeployPath = path, AppPool = "MyWeb" }
    };

    private static TargetConfig FolderTarget(BuildConfig? build = null) => new()
    {
        Type   = DeployType.Folder,
        Build  = build,
        Folder = new FolderConfig { DestinationPath = @"C:\Apps\Proc", ServiceName = "MyProc" }
    };

    private static BuildConfig Build(string runtime) => new() { Runtime = runtime };

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
    public void PeerCarriesItsIncomingShare_AndSelfOnlyWhenItMayHandOff()
    {
        // Default (applyViaAgent null): self keeps its share so an access-denied target has
        // somewhere to go. With false it never hands off, so it has no use for one.
        var byDefault = Plan(Config(Web1, Web2), "WEBSERVER01", Project("WebApp", IisTarget()));
        Assert.AreEqual(Web1.IncomingShare, byDefault.Self.IncomingShare);
        Assert.AreEqual(@"\\WEBSERVER02\deploy\incoming", byDefault.Peers.Single().IncomingShare);

        var inlineOnly = Plan(ViaAgentConfig(false, Web1, Web2), "WEBSERVER01", Project("WebApp", IisTarget()));
        Assert.IsNull(inlineOnly.Self.IncomingShare, "applyViaAgent false: the primary applies inline from staging only");
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
        Assert.AreEqual("artifacts/WebApp-release", step.Artifact);
        Assert.IsFalse(Path.IsPathRooted(step.Artifact),
            "an absolute path from the primary would be meaningless on a peer");
    }

    [TestMethod]
    public void EveryTarget_ConsumesExactlyOnePlannedPublish()
    {
        var plan = Plan(Config(), "BOX",
            Project("WebApp", IisTarget(), FolderTarget()),
            Project("Admin", VelopackTarget()));

        var publishPaths = plan.Publishes.Select(p => p.ArtifactRelativePath).ToList();
        Assert.AreEqual(publishPaths.Count, publishPaths.Distinct().Count(),
            "artifact paths are the publish/apply pairing key - they must be unique");

        foreach (var target in plan.Targets)
            Assert.AreEqual(1, plan.Publishes.Count(p => p.ArtifactRelativePath == target.ArtifactRelativePath),
                $"{target.Step.Label} must map to one and only one publish");
    }

    // -- Publish dedupe --------------------------------------------------------

    [TestMethod]
    public void SameBuild_DifferentTypes_ShareOnePublish()
    {
        // The motivating case: an IIS target and a folder target with identical build blocks
        // used to compile the project twice into two folders.
        var plan = Plan(Config(), "BOX",
            Project("WebApp", IisTarget(build: Build("win-x64")), FolderTarget(build: Build("win-x64"))));

        Assert.AreEqual(1, plan.Publishes.Count);
        Assert.AreEqual(2, plan.Targets.Count);

        var artifact = plan.Publishes.Single().ArtifactRelativePath;
        Assert.AreEqual("artifacts/WebApp-release-win-x64", artifact);
        Assert.IsTrue(plan.Targets.All(t => t.ArtifactRelativePath == artifact));
        Assert.IsTrue(plan.Self.Steps.All(s => s.Artifact == artifact),
            "both apply steps must read the one shared artifact folder");
    }

    [TestMethod]
    public void DuplicateTargetTypes_SameBuild_ShareOnePublish()
    {
        var plan = Plan(Config(), "BOX",
            Project("WebApp", IisTarget(@"C:\inetpub\A"), IisTarget(@"C:\inetpub\B")));

        Assert.AreEqual(1, plan.Publishes.Count);
        Assert.AreEqual(2, plan.Self.Steps.Count, "one build, two sites");
    }

    [TestMethod]
    public void OmittedBuild_EqualsDefaultBuild()
    {
        // A target with no build block means "the default build", which is the same thing
        // as spelling the defaults out - so the two must share.
        var plan = Plan(Config(), "BOX",
            Project("WebApp", IisTarget(build: null), FolderTarget(build: new BuildConfig())));

        Assert.AreEqual(1, plan.Publishes.Count);
        Assert.AreEqual("Release", plan.Publishes.Single().Label);
    }

    [TestMethod]
    public void ExplicitFalseAndEmpty_EqualOmitted()
    {
        // The SDK treats an absent selfContained/singleFile as false and an empty noWarn as
        // none, so a config that spells those out must share with one that leaves them off.
        var plan = Plan(Config(), "BOX",
            Project("Web",
                FolderTarget(build: new BuildConfig { Configuration = "Release", Runtime = "win-x64" }),
                IisTarget(build: new BuildConfig
                {
                    Configuration = "Release", Runtime = "win-x64",
                    SelfContained = false, SingleFile = false, NoWarn = ""
                })));

        Assert.AreEqual(1, plan.Publishes.Count);
        Assert.AreEqual("artifacts/Web-release-win-x64", plan.Publishes.Single().ArtifactRelativePath);
        Assert.AreEqual("Release / win-x64", plan.Publishes.Single().Label);
    }

    [TestMethod]
    public void DifferentRuntime_GetsSeparatePublishes()
    {
        var plan = Plan(Config(), "BOX",
            Project("WebApp", IisTarget(build: Build("win-x64")), FolderTarget(build: Build("linux-x64"))));

        CollectionAssert.AreEqual(
            new[] { "artifacts/WebApp-release-win-x64", "artifacts/WebApp-release-linux-x64" },
            plan.Publishes.Select(p => p.ArtifactRelativePath).ToList());
    }

    [TestMethod]
    public void EveryBuildField_ParticipatesInIdentity()
    {
        // Each row differs from the baseline in exactly one field and must not share with it.
        var baseline = Build("win-x64");
        var variants = new[]
        {
            new BuildConfig { Runtime = "win-x64", Configuration = "Debug" },
            new BuildConfig { Runtime = "win-x64", TargetFramework = "net8.0" },
            new BuildConfig { Runtime = "win-x64", SelfContained = true },
            new BuildConfig { Runtime = "win-x64", SingleFile = true },
            new BuildConfig { Runtime = "win-x64", NoWarn = "CS8600" }
        };

        foreach (var variant in variants)
        {
            var plan = Plan(Config(), "BOX",
                Project("WebApp", IisTarget(build: baseline), FolderTarget(build: variant)));

            Assert.AreEqual(2, plan.Publishes.Count,
                $"a build differing only in {Describe(variant)} must get its own publish");
        }
    }

    [TestMethod]
    public void SameSlugDifferentKey_StillGetsDistinctPaths()
    {
        // noWarn is part of the identity but not the folder name, so this pair collides on
        // the slug and must be disambiguated - a shared folder would apply the wrong build.
        var plan = Plan(Config(), "BOX",
            Project("WebApp",
                IisTarget(build: Build("win-x64")),
                FolderTarget(build: new BuildConfig { Runtime = "win-x64", NoWarn = "CS8600" })));

        CollectionAssert.AreEqual(
            new[] { "artifacts/WebApp-release-win-x64", "artifacts/WebApp-release-win-x64-2" },
            plan.Publishes.Select(p => p.ArtifactRelativePath).ToList());
    }

    [TestMethod]
    public void CaseAndOrder_DoNotSplitAPublish()
    {
        var plan = Plan(Config(), "BOX",
            Project("WebApp",
                IisTarget(build: new BuildConfig { Runtime = "win-x64", Configuration = "Release", NoWarn = "CS8600,CS8601" }),
                FolderTarget(build: new BuildConfig { Runtime = "WIN-X64", Configuration = "release", NoWarn = "cs8601, CS8600" })));

        Assert.AreEqual(1, plan.Publishes.Count);
    }

    [TestMethod]
    public void SameBuild_DifferentProjects_NeverShare()
    {
        var plan = Plan(Config(), "BOX",
            Project("WebApp", IisTarget(build: Build("win-x64"))),
            Project("Processor", FolderTarget(build: Build("win-x64"))));

        Assert.AreEqual(2, plan.Publishes.Count);
    }

    [TestMethod]
    public void SharedPublish_TravelsToPeersOnce()
    {
        // Two peer steps on one artifact must still name the one folder - propagation
        // copies distinct artifacts, so this is what keeps the payload from doubling.
        var plan = Plan(Config(Web1, Web2), "WEBSERVER01",
            Project("WebApp", IisTarget(), FolderTarget()));

        var peer = plan.Peers.Single();
        Assert.AreEqual(2, peer.Steps.Count);
        Assert.AreEqual(1, peer.Steps.Select(s => s.Artifact).Distinct().Count());
    }

    // -- applyViaAgent ---------------------------------------------------------

    private static DeployConfig ViaAgentConfig(bool? mode, params ServerConfig[] servers)
    {
        var c = Config(servers);
        c.Rollout!.ApplyViaAgent = mode;
        return c;
    }

    [TestMethod]
    public void ApplyViaAgent_True_SelfKeepsItsShare_AndHandsOffServerScopedStepsOnly()
    {
        var plan = Plan(ViaAgentConfig(true, Web1, Web2), "WEBSERVER01",
            Project("WebApp", IisTarget()),
            Project("Admin", VelopackTarget()));

        Assert.AreEqual(true, plan.Self.ApplyViaAgent);
        Assert.AreEqual(Web1.IncomingShare, plan.Self.IncomingShare, "self is reached like a peer");
        Assert.IsTrue(plan.SelfCanHandOff);
        Assert.AreEqual(2, plan.Self.Steps.Count, "self's plan is still the whole plan");

        var handed = plan.SelfAgentSteps;
        Assert.AreEqual(1, handed.Count);
        Assert.AreEqual("WebApp", handed[0].Project, "the Velopack step stays inline - it needs no rights here");
    }

    [TestMethod]
    public void ApplyViaAgent_Null_KeepsShareForASecondAttempt_ButPlansNothingUpFront()
    {
        var plan = Plan(ViaAgentConfig(null, Web1, Web2), "WEBSERVER01", Project("WebApp", IisTarget()));

        Assert.IsNull(plan.Self.ApplyViaAgent);
        Assert.AreEqual(Web1.IncomingShare, plan.Self.IncomingShare);
        Assert.IsTrue(plan.SelfCanHandOff);
        Assert.AreEqual(0, plan.SelfAgentSteps.Count, "with null the decision is per target, after the inline attempt");
    }

    [TestMethod]
    public void ApplyViaAgent_Null_WithoutAShare_CannotHandOff_AndIsNotAnError()
    {
        var bare = new ServerConfig { Name = "web1", Hostname = "WEBSERVER01" };
        var plan = Plan(ViaAgentConfig(null, bare, Web2), "WEBSERVER01", Project("WebApp", IisTarget()));

        Assert.IsFalse(plan.SelfCanHandOff);
    }

    [TestMethod]
    public void ApplyViaAgent_False_LeavesSelfWithoutAShare()
    {
        var plan = Plan(ViaAgentConfig(false, Web1, Web2), "WEBSERVER01", Project("WebApp", IisTarget()));

        Assert.AreEqual(false, plan.Self.ApplyViaAgent);
        Assert.IsNull(plan.Self.IncomingShare);
        Assert.IsFalse(plan.SelfCanHandOff);
        Assert.AreEqual(0, plan.SelfAgentSteps.Count);
    }

    [TestMethod]
    public void ApplyViaAgent_PeersAreUnaffected()
    {
        var plan = Plan(ViaAgentConfig(true, Web1, Web2), "WEBSERVER01", Project("WebApp", IisTarget()));

        var peer = plan.Peers.Single();
        Assert.IsNull(peer.ApplyViaAgent);
        Assert.AreEqual(Web2.IncomingShare, peer.IncomingShare);
        Assert.AreEqual(1, peer.Steps.Count);
    }

    [TestMethod]
    public void ApplyViaAgent_True_SelfExcludedBySrv_HandsOffNothing()
    {
        var directives = CommitDirectives.Parse("srv:web2");
        var plan = Planner.Plan("run-1", "abc", [Project("WebApp", IisTarget())], ViaAgentConfig(true, Web1, Web2),
                                Handlers, "WEBSERVER01", directives, "test");

        Assert.AreEqual(0, plan.SelfAgentSteps.Count, "an excluded self has nothing server-scoped to hand off");
    }

    [TestMethod]
    public void ApplyViaAgent_True_WithoutServers_IsAConfigError()
    {
        var c = new DeployConfig { Rollout = new RolloutConfig { ApplyViaAgent = true } };
        var ex = Assert.ThrowsException<DeployException>(() => Plan(c, "ANYBOX", Project("WebApp", IisTarget())));
        StringAssert.Contains(ex.Message, "servers[] is empty");
    }

    [TestMethod]
    public void ApplyViaAgent_True_UnlistedHost_IsAConfigError()
    {
        var ex = Assert.ThrowsException<DeployException>(
            () => Plan(ViaAgentConfig(true, Web1, Web2), "STRANGER", Project("WebApp", IisTarget())));
        StringAssert.Contains(ex.Message, "matches nothing in servers[]");
    }

    [TestMethod]
    public void ApplyViaAgent_True_SelfWithoutIncomingShare_IsAConfigError()
    {
        var bare = new ServerConfig { Name = "web1", Hostname = "WEBSERVER01" };
        var ex = Assert.ThrowsException<DeployException>(
            () => Plan(ViaAgentConfig(true, bare, Web2), "WEBSERVER01", Project("WebApp", IisTarget())));
        StringAssert.Contains(ex.Message, "has no incomingShare");
    }

    [TestMethod]
    public void ApplyViaAgent_Null_SingleBox_IsNotAnError()
    {
        var c = new DeployConfig { Rollout = new RolloutConfig { ApplyViaAgent = null } };
        var plan = Plan(c, "ANYBOX", Project("WebApp", IisTarget()));

        Assert.IsFalse(plan.SelfCanHandOff);
        Assert.AreEqual(1, plan.Self.Steps.Count);
    }

    private static string Describe(BuildConfig b) =>
        $"configuration={b.Configuration} tfm={b.TargetFramework} sc={b.SelfContained} single={b.SingleFile} noWarn={b.NoWarn}";

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
            Path.Combine("/staging", "run-1", "artifacts", "WebApp-release"),
            rebased.Artifact);
        Assert.AreEqual("artifacts/WebApp-release", step.Artifact, "rebasing must not mutate the plan");
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
