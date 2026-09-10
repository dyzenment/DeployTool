using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Plan;
using Dytools.DeployTool.Services;

namespace Dytools.DeployTool.Resolvers;

/// <summary>
/// Decides the entire rollout - every server, every step - in one shot from one set of
/// inputs.
///
/// Pure: no disk writes, no network, no IIS, no clock. That is what lets a whole fleet
/// rollout be asserted in a unit test with nothing installed, and it is why notBeforeUtc
/// is NOT computed here (see RolloutPlan.WaitSeconds).
///
/// Planning everything up front is deliberate: peer plans are never recomputed later or
/// derived on the peer itself, so the rollout is immutable and auditable the moment it exists.
/// </summary>
public static class Planner
{
    public static RolloutPlan Plan(
        string runId,
        string commitSha,
        IReadOnlyList<DiscoveredProject> selectedProjects,
        DeployConfig config,
        IReadOnlyDictionary<DeployType, IDeployTypeHandler> handlers,
        string selfHostname,
        CommitDirectives directives,
        string selectionReason)
    {
        var (publishes, targets) = BuildTargets(selectedProjects, config, handlers);
        var allSteps  = targets.Select(t => t.Step).ToList();
        var peerSteps = targets.Where(t => t.Scope == DeployScope.Server)
                               .Select(t => t.Step)
                               .ToList();

        // Global-scope work (a Velopack upload) is not server work, so an srv: directive that
        // excludes this box still leaves it holding these. Skipping them would turn "roll out
        // to WEB02 only" into "and also silently do not publish the package", which is not
        // what anyone means by it.
        var globalSteps = targets.Where(t => t.Scope == DeployScope.Global)
                                 .Select(t => t.Step)
                                 .ToList();

        return new RolloutPlan
        {
            RunId       = runId,
            CommitSha   = commitSha,
            WaitSeconds = directives.WaitSeconds ?? config.Rollout?.DelaySeconds ?? 3600,
            SkipTests   = directives.SkipTests ?? false,
            Selection   = new PlanSelection
            {
                Projects = selectedProjects.Select(p => p.Name).ToList(),
                Reason   = selectionReason
            },
            Publishes   = publishes,
            Targets     = targets,
            ServerPlans = BuildServerPlans(config, selfHostname, directives, allSteps, peerSteps, globalSteps)
        };
    }

    // -- Targets and publishes -------------------------------------------------

    /// <summary>
    /// Publishes are keyed by build variant, not by target. Every target in a project is
    /// folded onto the first publish whose <see cref="BuildVariant.Key"/> matches, so a
    /// project with an IIS target and a folder target on the same build compiles once and
    /// applies twice. Targets that differ in any build field get their own publish.
    /// </summary>
    private static (List<PublishPlan>, List<TargetPlan>) BuildTargets(
        IReadOnlyList<DiscoveredProject> selectedProjects,
        DeployConfig config,
        IReadOnlyDictionary<DeployType, IDeployTypeHandler> handlers)
    {
        var publishes = new List<PublishPlan>();
        var targets   = new List<TargetPlan>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in selectedProjects)
        {
            // Per project, not per plan: the same build of two different projects is two builds.
            var byVariant = new Dictionary<string, PublishPlan>(StringComparer.Ordinal);

            foreach (var target in project.Config!.Targets)
            {
                if (!handlers.TryGetValue(target.Type, out var handler))
                    throw new InvalidOperationException(
                        $"Project '{project.Name}': no handler registered for deploy type '{target.Type}'.");

                var key = BuildVariant.Key(target.Build);

                if (!byVariant.TryGetValue(key, out var publish))
                {
                    publish = new PublishPlan
                    {
                        ProjectName          = project.Name,
                        Build                = target.Build ?? new BuildConfig(),
                        Label                = BuildVariant.Label(target.Build),
                        ArtifactRelativePath = "artifacts/" + UniqueArtifactName(project, target.Build, usedNames)
                    };
                    byVariant[key] = publish;
                    publishes.Add(publish);
                }

                targets.Add(new TargetPlan
                {
                    ProjectName          = project.Name,
                    Target               = target,
                    ArtifactRelativePath = publish.ArtifactRelativePath,
                    Scope                = handler.GetScope(target),
                    Step                 = ApplyStepBuilder.Build(project, target, config, publish.ArtifactRelativePath)
                });
            }
        }

        return (publishes, targets);
    }

    /// <summary>
    /// "WebApp-release-win-x64", disambiguated to "WebApp-release-win-x64-2" when two variants
    /// share a slug but not a key (a noWarn-only difference, say). Uniqueness matters beyond
    /// tidiness: this path is the key that pairs a publish with its apply steps, so a
    /// collision would apply the wrong build.
    /// </summary>
    private static string UniqueArtifactName(
        DiscoveredProject project, BuildConfig? build, HashSet<string> used)
    {
        var baseName = $"{project.Name}-{BuildVariant.Slug(build)}";
        var name     = baseName;
        var counter  = 2;

        while (!used.Add(name))
            name = $"{baseName}-{counter++}";

        return name;
    }

    // -- Server plans ----------------------------------------------------------

    private static List<ServerPlan> BuildServerPlans(
        DeployConfig config,
        string selfHostname,
        CommitDirectives directives,
        List<Models.Manifest.ApplyStep> allSteps,
        List<Models.Manifest.ApplyStep> peerSteps,
        List<Models.Manifest.ApplyStep> globalSteps)
    {
        // No fleet configured: single-box, exactly as the tool has always behaved.
        if (config.Servers.Count == 0)
            return [SelfOnly(selfHostname, allSteps)];

        var match = HostIdentity.Find(config.Servers, selfHostname);

        // Host is not in servers[]. Deploy locally but propagate to nobody: an unlisted box
        // has no mandate to drive the fleet, and silently rolling out from an unknown
        // machine is the worse failure.
        if (match is null)
        {
            Console.WriteLine(
                $"[Planner] This host ('{selfHostname}') is not listed in servers[] -- " +
                "deploying locally only, no propagation. " +
                "Run 'dytools-deploy hostname --config <path>' to see what it would need to match.");
            return [SelfOnly(selfHostname, allSteps)];
        }

        if (match.Reason != "hostname")
            Console.WriteLine(
                $"[Planner] This host ('{selfHostname}') matched servers[] entry " +
                $"'{match.Server.Name}' by {match.Reason}.");

        var plans = config.Servers.Select(server =>
        {
            var isSelf   = ReferenceEquals(server, match.Server);
            var selected = directives.MatchesSrv(server.Name, server.Hostname);

            return new ServerPlan
            {
                ServerName    = server.Name,
                IsSelf        = isSelf,
                Selected      = selected,
                IncomingShare = isSelf ? null : server.IncomingShare,
                ShareUsername = isSelf ? null : server.Username,
                SharePassword = isSelf ? null : server.Password,

                // Self runs everything. A peer gets Server-scoped steps only - Global steps are
                // emitted once, on the primary, and never travel. An excluded box gets nothing,
                // except that an excluded PRIMARY keeps its Global steps: it still has to build
                // (nobody else can) and a package upload is not something it does "to a server".
                Steps = (isSelf, selected) switch
                {
                    (true,  true)  => allSteps,
                    (true,  false) => globalSteps,
                    (false, true)  => peerSteps,
                    (false, false) => []
                }
            };
        }).ToList();

        if (directives.HasSrv)
        {
            var excluded = plans.Where(p => !p.Selected).Select(p => p.ServerName).ToList();

            Console.WriteLine(excluded.Count == 0
                ? $"[Planner] srv: {string.Join(" | ", directives.SrvPatterns!)} -- every server matched."
                : $"[Planner] srv: {string.Join(" | ", directives.SrvPatterns!)} -- excluding " +
                  string.Join(", ", excluded));

            if (plans.All(p => !p.Selected))
                Console.WriteLine(
                    "[Planner] Warning: srv: matched no servers, so this run applies nowhere. " +
                    "Projects will still be built.");
        }

        return plans;
    }

    private static ServerPlan SelfOnly(string selfHostname, List<Models.Manifest.ApplyStep> allSteps)
        => new() { ServerName = selfHostname, IsSelf = true, Steps = allSteps };
}
