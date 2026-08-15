using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Services;

/// <summary>
/// Contract for all deployment type handlers.
///
/// A handler only knows how to take an already-published artifact directory and put it
/// live. It does not build, publish, or resolve projects - publish is a pipeline phase
/// owned by the orchestrator (identical for every type), and everything a handler needs
/// is pre-resolved onto the ApplyStep.
///
/// This is what lets the primary and a peer share one executor: the primary constructs
/// ApplyStep objects in memory, a peer deserializes them from manifest.json, and neither
/// path can diverge because there is only one implementation below this interface.
/// </summary>
public interface IDeployTypeHandler
{
    DeployType SupportedType { get; }

    /// <summary>
    /// Whether this target lands on a specific box (Server) or on shared infrastructure
    /// (Global). Drives which server plans the step is emitted into. May depend on the
    /// target's own config - e.g. a delivery mode that changes the destination.
    /// </summary>
    DeployScope GetScope(TargetConfig target);

    /// <summary>
    /// Puts a pre-published artifact directory live. The only role-agnostic operation
    /// in the tool: identical whether invoked by the primary inline or by a peer's agent.
    /// </summary>
    Task<TargetDeployResult> ApplyAsync(ApplyStep step);
}
