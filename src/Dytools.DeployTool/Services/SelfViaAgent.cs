using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Models.Plan;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Services;

/// <summary>
/// The primary handing steps to the agent on its own box. It is the peer handoff pointed at
/// ourselves - same run folder, manifest, shipped tool, through <see cref="Propagator.Ship"/>
/// - and, like a peer, it is fire and forget: the run folder lands, the agent applies it on
/// its next poll, and its result.json is the agent's record, not this run's. The only
/// difference from a peer is that the steps are due immediately.
///
/// Which steps arrive here is the caller's decision: everything server-scoped when
/// rollout.applyViaAgent is true, or just the targets that failed inline with access
/// denied when it is null.
/// </summary>
public static class SelfViaAgent
{
    public const string AppliedByLabel = "agent";

    /// <summary>
    /// Ships <paramref name="steps"/> to this box's agent and records the handoff in the
    /// report's propagation list. Returns true when the run folder landed.
    /// </summary>
    public static bool Ship(
        RolloutPlan plan,
        IReadOnlyList<ApplyStep> steps,
        string stagingRoot,
        int keepRuns,
        DeployReport report)
    {
        var self = plan.Self;

        var handoff = new ServerPlan
        {
            ServerName    = self.ServerName,
            IncomingShare = self.IncomingShare,
            ShareUsername = self.ShareUsername,
            SharePassword = self.SharePassword,
            Steps         = steps.ToList()
        };

        var result = new PropagationResult
        {
            ServerName    = self.ServerName,
            IncomingShare = self.IncomingShare ?? string.Empty,
            StepCount     = steps.Count,
            NotBeforeUtc  = DateTimeOffset.UtcNow
        };
        report.Propagation.Add(result);

        Console.WriteLine($"\n  → {self.ServerName} (this box, via agent): {steps.Count} step(s), applies on its next poll");

        try
        {
            Propagator.Ship(plan, handoff, stagingRoot, keepRuns, result.NotBeforeUtc, result);
            result.Success = true;
            Console.WriteLine($"    ✓ Handed off to {result.RunFolder}");
        }
        catch (Exception ex)
        {
            result.Success      = false;
            result.ErrorMessage = ex.Message;
            Console.WriteLine($"    ✗ {ex.Message}");
        }

        result.CompletedAt = DateTimeOffset.Now;
        return result.Success;
    }

    /// <summary>
    /// What a handed-off target looks like in this run's report: the handoff either landed
    /// or it did not. Whether the agent then succeeded is in the agent's own result.json.
    /// </summary>
    public static TargetDeployResult HandedOff(ApplyStep step, bool shipped, string? error) => new()
    {
        TargetLabel  = step.Label,
        Type         = step.Type,
        Success      = shipped,
        AppliedBy    = AppliedByLabel,
        ErrorMessage = shipped ? null : $"Not handed to the agent: {error}"
    };
}
