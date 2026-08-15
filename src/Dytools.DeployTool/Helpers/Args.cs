namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Minimal --key value command-line argument parser.
/// Parses pairs of the form: --config deploy-config.json --changed "a|b|c"
/// </summary>
public sealed class Args
{
    private readonly Dictionary<string, string> _values;

    private Args(Dictionary<string, string> values) => _values = values;

    /// <summary>
    /// Parses the given argument array into named key/value pairs.
    /// Keys must be prefixed with "--". Each key must be followed by its value.
    /// </summary>
    public static Args Parse(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
                dict[args[i][2..]] = args[i + 1];
        }

        return new Args(dict);
    }

    /// <summary>Returns the value for a required argument. Throws if missing.</summary>
    public string GetRequired(string key)
        => _values.TryGetValue(key, out var v)
            ? v
            : throw new ArgumentException($"Required argument '--{key}' is missing.");

    /// <summary>Returns the value for an optional argument, or null if absent.</summary>
    public string? GetOptional(string key)
        => _values.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// Returns the value as an int for an optional numeric argument like --wait 60.
    /// Null when absent or non-numeric - a bad value reads as "not specified" rather than
    /// aborting, matching how the commit-message wait: directive tolerates junk.
    /// </summary>
    public int? GetOptionalInt(string key)
        => _values.TryGetValue(key, out var v) && int.TryParse(v, out var parsed)
            ? parsed
            : null;

    /// <summary>Returns the value as a bool for flag arguments like --force-all true.</summary>
    public bool GetFlag(string key, bool defaultValue = false)
    {
        if (!_values.TryGetValue(key, out var v)) return defaultValue;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}