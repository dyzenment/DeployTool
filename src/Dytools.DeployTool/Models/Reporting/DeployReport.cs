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

    public bool Success => Results.Count > 0 && Results.All(r => r.Success);

    public int TotalProjectsSelected => Results.Count;
    public int SucceededProjects => Results.Count(r => r.Success);
    public int SkippedProjects { get; set; }

    public string CommitSha { get; init; } = string.Empty;
    public List<string> ChangedFiles { get; init; } = [];
    public bool ForcedAll { get; init; }

    public List<StepResult> PrerequisiteResults { get; init; } = [];
    public List<ProjectDeployResult> Results { get; init; } = [];
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