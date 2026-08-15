using System.Text.Json.Serialization;
using Dytools.DeployTool.Models.Config;

namespace Dytools.DeployTool.Models.Manifest;

/// <summary>
/// One unit of deploy work: take a pre-published artifact directory and put it live.
///
/// This is the single contract the apply executor consumes - whether the steps were
/// constructed in memory on the primary or deserialized from a peer's manifest.json.
/// Nothing below this type knows which server role it is running as. Role differences
/// are expressed by which steps get emitted, never by branching inside the executor.
/// </summary>
public sealed class ApplyStep
{
    [JsonPropertyName("project")]
    public string Project { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public DeployType Type { get; init; }

    /// <summary>
    /// Directory holding the published output for this step.
    /// Absolute in memory; relative to the run folder when serialized into a manifest.
    /// Apply mode rebases it to absolute before executing.
    /// </summary>
    [JsonPropertyName("artifact")]
    public string Artifact { get; init; } = string.Empty;

    /// <summary>Human-readable description, resolved at plan time. Surfaces in reports and logs.</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>Whether to snapshot before applying and restore on failure.</summary>
    [JsonPropertyName("rollback")]
    public bool Rollback { get; init; }

    [JsonPropertyName("iis")]
    public IisConfig? Iis { get; init; }

    [JsonPropertyName("folder")]
    public FolderConfig? Folder { get; init; }

    /// <summary>
    /// Velopack is Global scope: it is only ever emitted into the primary's own plan and is
    /// never written to a peer manifest. That is what lets it carry local build metadata
    /// (assembly name, project folder, icon) which server-scoped steps cannot rely on.
    /// </summary>
    [JsonPropertyName("velopack")]
    public VelopackStep? Velopack { get; init; }

    /// <summary>
    /// Copy with Artifact resolved against the run folder that holds it.
    ///
    /// Both roles call this and it means the same thing in each: the primary rebases against
    /// its staging folder, a peer against its incoming run folder. Keeping the stored path
    /// relative is what lets one manifest be produced on one box and executed on another.
    /// </summary>
    public ApplyStep RebasedTo(string runFolder) => new()
    {
        Project  = Project,
        Type     = Type,
        Artifact = Path.Combine(runFolder, Artifact.Replace('/', Path.DirectorySeparatorChar)),
        Label    = Label,
        Rollback = Rollback,
        Iis      = Iis,
        Folder   = Folder,
        Velopack = Velopack
    };
}

/// <summary>
/// Velopack apply payload. Bundles the target's velopack config with the project metadata
/// resolved at plan time, so the executor needs no DiscoveredProject or DeployConfig.
/// </summary>
public sealed class VelopackStep
{
    [JsonPropertyName("config")]
    public VelopackConfig Config { get; init; } = new();

    [JsonPropertyName("packCache")]
    public PackCacheConfig? PackCache { get; init; }

    /// <summary>AssemblyName from the .csproj - used to locate the published DLL for versioning.</summary>
    [JsonPropertyName("assemblyName")]
    public string AssemblyName { get; init; } = string.Empty;

    /// <summary>Working directory for vpk invocations.</summary>
    [JsonPropertyName("projectFolder")]
    public string ProjectFolder { get; init; } = string.Empty;

    /// <summary>Icon resolved at plan time: velopack.icon → csproj ApplicationIcon → null.</summary>
    [JsonPropertyName("iconPath")]
    public string? IconPath { get; init; }

    /// <summary>Build RID - drives icon extension choice and the mainExe suffix.</summary>
    [JsonPropertyName("buildRuntime")]
    public string? BuildRuntime { get; init; }
}
