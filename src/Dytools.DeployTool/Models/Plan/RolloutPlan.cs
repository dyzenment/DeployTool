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
    /// The primary's publish work: one entry per distinct build variant per project, never
    /// one per target. Two targets whose build blocks agree point at the same entry and the
    /// same artifact folder, which is what stops "IIS + folder, same build" compiling twice.
    /// </summary>
    public List<PublishPlan> Publishes { get; init; } = [];

    /// <summary>
    /// The primary's ordered apply work. Each entry pairs the target with the ApplyStep it
    /// produces and names the publish it consumes. ServerPlans are derived from this list -
    /// it is the single source.
    /// </summary>
    public List<TargetPlan> Targets { get; init; } = [];

    /// <summary>One plan per server, including this one.</summary>
    public List<ServerPlan> ServerPlans { get; init; } = [];

    public ServerPlan Self => ServerPlans.First(s => s.IsSelf);

    /// <summary>Every other configured server, whether or not this run touches it.</summary>
    public IEnumerable<ServerPlan> Peers => ServerPlans.Where(s => !s.IsSelf);

    /// <summary>
    /// The server-scoped steps this box hands to its own agent up front - every one of them
    /// when applyViaAgent is true, none otherwise. (With null the decision is made per target
    /// after the inline attempt, so it is not knowable here.)
    /// </summary>
    public List<ApplyStep> SelfAgentSteps => Self.ApplyViaAgent == true
        ? Targets.Where(t => t.Scope == DeployScope.Server && Self.Steps.Contains(t.Step))
                 .Select(t => t.Step).ToList()
        : [];

    /// <summary>Whether this box can hand anything to its own agent at all.</summary>
    public bool SelfCanHandOff => Self.ApplyViaAgent != false && !string.IsNullOrWhiteSpace(Self.IncomingShare);

    /// <summary>
    /// The peers this run actually ships to - Peers minus anything an srv: directive excluded.
    /// This is what propagation and the precheck work from; Peers is only for display, so an
    /// excluded box still appears in the plan rather than silently vanishing.
    /// </summary>
    public IEnumerable<ServerPlan> SelectedPeers => Peers.Where(s => s.Selected);

    public bool HasPeers => SelectedPeers.Any();
}

/// <summary>What was chosen to deploy, and why. Recorded for the audit trail.</summary>
public sealed class PlanSelection
{
    public List<string> Projects { get; init; } = [];

    /// <summary>e.g. "pub: directive", "--force-all", "changed files".</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// One build on the primary. Primary-only: a peer never builds, so this never travels -
/// only the artifact folder it fills does.
/// </summary>
public sealed class PublishPlan
{
    public string ProjectName { get; init; } = string.Empty;

    /// <summary>The build block this publish runs with. Never null; a missing block is the default build.</summary>
    public BuildConfig Build { get; init; } = new();

    /// <summary>"Release / win-x64 / net8.0" - for plan output and the report.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Where the output lands, relative to the run folder - e.g. "artifacts/WebApp-release-win-x64".
    /// Unique within a plan, so it is the key that pairs this publish with every apply step
    /// that consumes it, on the primary and after a manifest round-trip.
    /// </summary>
    public string ArtifactRelativePath { get; init; } = string.Empty;
}

/// <summary>
/// One target's apply work on the primary, and which publish it waits on.
/// </summary>
public sealed class TargetPlan
{
    public string ProjectName { get; init; } = string.Empty;

    /// <summary>The configured target. Primary-only; the resolved Step is what travels.</summary>
    public TargetConfig Target { get; init; } = new();

    /// <summary>
    /// The publish this target applies from - always equal to Step.Artifact, and always the
    /// ArtifactRelativePath of exactly one PublishPlan in the same RolloutPlan.
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

    /// <summary>
    /// Whether this run touches this server at all. False only when an srv: directive excluded
    /// it. Kept alongside the (then empty) step list so the plan can say "excluded" rather than
    /// "nothing to do", which are different things and fail differently.
    /// </summary>
    public bool Selected { get; init; } = true;

    /// <summary>
    /// rollout.applyViaAgent, carried on the self plan only: true = every server-scoped step
    /// goes to this box's agent, false = none do, null = only those that fail inline with
    /// access denied. Always null on a peer.
    /// </summary>
    public bool? ApplyViaAgent { get; init; }

    /// <summary>
    /// Where to drop this server's run folder. Null for self unless it may hand steps to its
    /// own agent (ApplyViaAgent is not false) and its servers[] entry has one.
    /// </summary>
    public string? IncomingShare { get; init; }

    /// <summary>
    /// Credentials for reaching <see cref="IncomingShare"/>, when the primary's own identity is
    /// not enough. Null for self (unless ApplyViaAgent), and for any peer on a shared identity.
    ///
    /// A ServerPlan never leaves the primary's memory - only ApplyStep travels, inside a
    /// manifest that sits on a file share. Keep it that way: nothing here may be serialized.
    /// </summary>
    public string? ShareUsername { get; init; }

    /// <inheritdoc cref="ShareUsername"/>
    public string? SharePassword { get; init; }

    /// <summary>
    /// Self receives every step. A peer receives only Server-scoped steps - Global steps
    /// (velopack uploads to Azure) are emitted once, here, and never travel.
    /// </summary>
    public List<ApplyStep> Steps { get; init; } = [];
}
