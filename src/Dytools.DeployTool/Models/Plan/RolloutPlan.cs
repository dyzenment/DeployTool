using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Manifest;

namespace Dytools.DeployTool.Models.Plan;

/// <summary>
/// The complete decision for one deploy run, across every server, produced in one shot from
/// one set of inputs. Nothing downstream re-decides anything: the primary executes its own
/// ServerPlan and ships the others verbatim.
///
/// Deliberately carries no clock. notBeforeUtc is stamped later, at manifest-write time,
/// because the soak window must start when the primary is proven good - not when the plan
/// was made. See WaitSeconds.
/// </summary>
public sealed class RolloutPlan
{
    public string RunId { get; init; } = string.Empty;
    public string CommitSha { get; init; } = string.Empty;

    /// <summary>
    /// Soak seconds between primary success and peers applying. Resolved from the wait:
    /// directive, else rollout.delaySeconds. Held as a duration, not a timestamp, so the
    /// planner stays clock-free and testable.
    /// </summary>
    public int WaitSeconds { get; init; }

    /// <summary>
    /// True when the unit-test gate is bypassed for this run, from a skiptests directive or
    /// --skip-tests. Decided here with everything else rather than re-read at test time, so the
    /// plan remains the single record of what this run chose to do.
    /// </summary>
    public bool SkipTests { get; init; }

    public PlanSelection Selection { get; init; } = new();

    /// <summary>
    /// The primary's ordered build+apply work. Each entry pairs the publish input
    /// (TargetConfig, which carries the build config) with the apply input (ApplyStep).
    /// ServerPlans are derived from this list - it is the single source.
    /// </summary>
    public List<TargetPlan> Targets { get; init; } = [];

    /// <summary>One plan per server, including this one.</summary>
    public List<ServerPlan> ServerPlans { get; init; } = [];

    public ServerPlan Self => ServerPlans.First(s => s.IsSelf);
    public IEnumerable<ServerPlan> Peers => ServerPlans.Where(s => !s.IsSelf);
    public bool HasPeers => ServerPlans.Any(s => !s.IsSelf);
}

/// <summary>What was chosen to deploy, and why. Recorded for the audit trail.</summary>
public sealed class PlanSelection
{
    public List<string> Projects { get; init; } = [];

    /// <summary>e.g. "pub: directive", "--force-all", "changed files".</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// One target's work on the primary: publish it, then apply it.
/// Pairs the two so they stay adjacent during execution.
/// </summary>
public sealed class TargetPlan
{
    public string ProjectName { get; init; } = string.Empty;

    /// <summary>Publish input. Primary-only - a peer never builds, so this never travels.</summary>
    public TargetConfig Target { get; init; } = new();

    /// <summary>
    /// Where this target's output lands, relative to the run folder - e.g.
    /// "artifacts/WebApp-iis". Unique within a plan, so it doubles as the key that pairs a
    /// publish with its apply step after a manifest round-trip.
    /// </summary>
    public string ArtifactRelativePath { get; init; } = string.Empty;

    /// <summary>Decides whether this step reaches peers at all.</summary>
    public DeployScope Scope { get; init; }

    /// <summary>Apply input. The same object instance appears in the relevant ServerPlans.</summary>
    public ApplyStep Step { get; init; } = new();
}

/// <summary>What one server will do.</summary>
public sealed class ServerPlan
{
    public string ServerName { get; init; } = string.Empty;

    /// <summary>True for the box running this deploy - it applies inline from staging.</summary>
    public bool IsSelf { get; init; }

    /// <summary>Where to drop this peer's run folder. Null for self.</summary>
    public string? IncomingShare { get; init; }

    /// <summary>
    /// Self receives every step. A peer receives only Server-scoped steps - Global steps
    /// (velopack uploads to Azure) are emitted once, here, and never travel.
    /// </summary>
    public List<ApplyStep> Steps { get; init; } = [];
}
