using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dytools.DeployTool;

/// <summary>
/// Shared JSON serializer options for reading deploy-config.json.
/// </summary>
public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,   // tolerate camelCase in config, PascalCase in models
        ReadCommentHandling = JsonCommentHandling.Skip,  // allow // comments in config for documentation
        AllowTrailingCommas = true,            // allow trailing commas for easier editing
        Converters =
        {
            // Enum values are written as camelCase strings in JSON (e.g. "velopack", "iis")
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    /// <summary>
    /// For files this tool writes: manifest.json and result.json.
    ///
    /// The camelCase policy covers models without explicit attributes (DeployReport).
    /// Config types reused in a manifest carry [JsonPropertyName] already, and those win
    /// over the policy - so both spell the same names either way.
    ///
    /// Indented on purpose: these are read by humans debugging a rollout, and a manifest
    /// sitting on a share is often the only record of what a peer was told to do.
    /// </summary>
    public static readonly JsonSerializerOptions Write = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    /// <summary>Reads back what Write produced. Tolerant of casing so the two cannot drift.</summary>
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };
}