using Dytools.DeployTool.Models.Config;

namespace Dytools.DeployTool.Models.Reporting;

public sealed class DeployReport
{
    /// <summary>Ties this report to the run folder and manifest it came from.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Which box produced this report - the whole point of the file on a peer.</summary>
    public string ServerName { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : null;

    /// <summary>
    /// Propagation counts: a build that went live here but never reached a peer is a failed
    /// run, not a successful one. The list is empty on a single-box run, so All() holds.
    /// </summary>
    public bool Success => Results.Count > 0
                        && Results.All(r => r.Success)
                        && Propagation.All(p => p.Success);

    public int TotalProjectsSelected => Results.Count;
    public int SucceededProjects => Results.Count(r => r.Success);
    public int SkippedProjects { get; set; }

    public string CommitSha { get; init; } = string.Empty;
    public List<string> ChangedFiles { get; init; } = [];
    public bool ForcedAll { get; init; }

    public List<StepResult> PrerequisiteResults { get; init; } = [];
    public List<ProjectDeployResult> Results { get; init; } = [];

    /// <summary>One entry per peer this run shipped to. Empty on a single-box run.</summary>
    public List<PropagationResult> Propagation { get; init; } = [];
}

/// <summary>
/// The outcome of handing one peer its run folder. Recorded on the primary, because the
/// primary is the only box that knows whether the handoff happened at all - the peer's own
/// result.json only ever covers what it did after receiving one.
/// </summary>
public sealed class PropagationResult
{
    /// <summary>The peer's servers[] name.</summary>
    public string ServerName { get; init; } = string.Empty;

    /// <summary>Configured incomingShare, as written (before %ENV% expansion).</summary>
    public string IncomingShare { get; init; } = string.Empty;

    /// <summary>Final resting path of the run folder on the peer, once the move succeeded.</summary>
    public string? RunFolder { get; set; }

    /// <summary>How many apply steps this peer was given. Global-scope steps never travel.</summary>
    public int StepCount { get; init; }

    /// <summary>
    /// Whether this run's own binary was shipped alongside the manifest. False means the peer
    /// has the payload but nothing to execute it with - see Propagator.TryShipTool.
    /// </summary>
    public bool ToolShipped { get; set; }

    /// <summary>Earliest time the peer may apply, as stamped into its manifest.</summary>
    public DateTimeOffset NotBeforeUtc { get; init; }

    /// <summary>
    /// Account the share was opened as, or null when the primary connected as itself. Recorded
    /// because "which identity wrote this" is the first question asked when a peer's folder has
    /// the wrong permissions on it. The username only - never the password.
    /// </summary>
    public string? ConnectedAs { get; set; }

    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : null;
}

public sealed class ProjectDeployResult
{
    public string ProjectName { get; init; } = string.Empty;

    public bool Success => !TestsFailed &&
                           TargetResults.Count > 0 &&
                           TargetResults.All(t => t.Success);

    /// <summary>True if tests ran and failed, causing deploy to abort.</summary>
    public bool TestsFailed { get; set; }

    public List<StepResult> PreBuildResults { get; init; } = [];

    /// <summary>
    /// Results from unit test runs for this project and its transitive dependencies.
    /// Includes skipped entries (already passed earlier in the run).
    /// </summary>
    public List<StepResult> TestResults { get; init; } = [];

    public List<TargetDeployResult> TargetResults { get; init; } = [];
}

public sealed class TargetDeployResult
{
    /// <summary>
    /// Starts as the plan-time label from ApplyStep. Handlers may refine it once runtime
    /// facts are known - e.g. blue-green resolves "IIS / C:\inetpub\Web_A" into
    /// "IIS / MyWeb → Web_B" so the report names the slot that actually went live.
    /// </summary>
    public string TargetLabel { get; set; } = string.Empty;
    public DeployType Type { get; init; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public bool RolledBack { get; set; }
    public StepResult? PublishResult { get; set; }
    public List<StepResult> DeploySteps { get; init; } = [];
}

public sealed class StepResult
{
    public string StepName { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public int ExitCode { get; set; }
    public bool Success { get; set; }
    public string Stdout { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public TimeSpan? Duration => CompletedAt.HasValue ? CompletedAt.Value - StartedAt : null;
}