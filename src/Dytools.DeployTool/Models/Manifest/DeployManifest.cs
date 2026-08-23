using System.Text.Json.Serialization;

namespace Dytools.DeployTool.Models.Manifest;

/// <summary>
/// What one peer is told to do, and the earliest moment it may do it.
///
/// This is the whole instruction set: deploy-config.json never travels. A peer's work is
/// auditable by reading one file next to the artifacts it applies, and each peer's manifest
/// can differ without a shared config having to describe every box.
///
/// Every Artifact path in Steps stays relative to the run folder holding this file, which
/// is exactly what lets a manifest written on one machine execute on another.
/// </summary>
public sealed class DeployManifest
{
    public const string FileName = "manifest.json";

    [JsonPropertyName("runId")]
    public string RunId { get; init; } = string.Empty;

    [JsonPropertyName("commitSha")]
    public string CommitSha { get; init; } = string.Empty;

    /// <summary>The box that produced this manifest - the primary for this run.</summary>
    [JsonPropertyName("primary")]
    public string Primary { get; init; } = string.Empty;

    /// <summary>The peer this manifest was written for, by its servers[] name.</summary>
    [JsonPropertyName("server")]
    public string Server { get; init; } = string.Empty;

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>
    /// Earliest time the peer may apply: primary success + this run's soak seconds. Stamped
    /// here rather than at plan time because the window has to start when the primary is
    /// proven good, not when the plan was made - see RolloutPlan.WaitSeconds.
    /// </summary>
    [JsonPropertyName("notBeforeUtc")]
    public DateTimeOffset NotBeforeUtc { get; init; }

    [JsonPropertyName("steps")]
    public List<ApplyStep> Steps { get; init; } = [];
}
