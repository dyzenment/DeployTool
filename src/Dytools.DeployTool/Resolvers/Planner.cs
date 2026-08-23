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
        var targets   = BuildTargets(selectedProjects, config, handlers);
        var allSteps  = targets.Select(t => t.Step).ToList();
        var peerSteps = targets.Where(t => t.Scope == DeployScope.Server)
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
            Targets     = targets,
            ServerPlans = BuildServerPlans(config, selfHostname, allSteps, peerSteps)
        };
    }

    // -- Targets ---------------------------------------------------------------

    private static List<TargetPlan> BuildTargets(
        IReadOnlyList<DiscoveredProject> selectedProjects,
        DeployConfig config,
        IReadOnlyDictionary<DeployType, IDeployTypeHandler> handlers)
    {
        var targets   = new List<TargetPlan>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in selectedProjects)
        {
            foreach (var target in project.Config!.Targets)
            {
                if (!handlers.TryGetValue(target.Type, out var handler))
                    throw new InvalidOperationException(
                        $"Project '{project.Name}': no handler registered for deploy type '{target.Type}'.");

                var relativePath = "artifacts/" + UniqueArtifactName(project, target, usedNames);

                targets.Add(new TargetPlan
                {
                    ProjectName          = project.Name,
                    Target               = target,
                    ArtifactRelativePath = relativePath,
                    Scope                = handler.GetScope(target),
                    Step                 = ApplyStepBuilder.Build(project, target, config, relativePath)
                });
            }
        }

        return targets;
    }

    /// <summary>
    /// "WebApp-iis", disambiguated to "WebApp-iis-2" if a project declares more than one
    /// target of the same type. Uniqueness matters beyond tidiness: this path is the key
    /// that pairs a publish with its apply step, so a collision would apply the wrong build.
    /// </summary>
    private static string UniqueArtifactName(
        DiscoveredProject project, TargetConfig target, HashSet<string> used)
    {
        var baseName = $"{project.Name}-{target.Type.ToString().ToLowerInvariant()}";
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
        List<Models.Manifest.ApplyStep> allSteps,
        List<Models.Manifest.ApplyStep> peerSteps)
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

        return config.Servers.Select(server =>
        {
            var isSelf = ReferenceEquals(server, match.Server);
            return new ServerPlan
            {
                ServerName    = server.Name,
                IsSelf        = isSelf,
                IncomingShare = isSelf ? null : server.IncomingShare,
                ShareUsername = isSelf ? null : server.Username,
                SharePassword = isSelf ? null : server.Password,
                // Self runs everything. A peer gets Server-scoped steps only - Global steps
                // are emitted once, on the primary, and never travel.
                Steps         = isSelf ? allSteps : peerSteps
            };
        }).ToList();
    }

    private static ServerPlan SelfOnly(string selfHostname, List<Models.Manifest.ApplyStep> allSteps)
        => new() { ServerName = selfHostname, IsSelf = true, Steps = allSteps };
}
