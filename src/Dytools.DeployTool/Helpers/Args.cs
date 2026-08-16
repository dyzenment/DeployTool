namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Minimal --key value command-line argument parser.
/// Parses pairs of the form: --config deploy-config.json --changed "a|b|c"
/// </summary>
public sealed class Args
{
    private readonly Dictionary<string, string> _values;
    private readonly HashSet<string> _present;

    private Args(Dictionary<string, string> values, HashSet<string> present)
    {
        _values  = values;
        _present = present;
    }

    /// <summary>
    /// Parses the given argument array into named key/value pairs.
    /// Keys must be prefixed with "--". Each key must be followed by its value.
    ///
    /// Which keys appeared is tracked separately from their values, so a switch written bare
    /// (--skip-tests, with nothing after it) is still distinguishable from one never written
    /// at all. See <see cref="GetOptionalBool"/>.
    /// </summary>
    public static Args Parse(string[] args)
    {
        var dict    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
                present.Add(arg[2..]);
        }

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
                dict[args[i][2..]] = args[i + 1];
        }

        return new Args(dict, present);
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

    /// <summary>
    /// Returns a three-state flag: null when the argument was not given at all, otherwise the
    /// value it was given. All of these mean true:
    ///
    ///   --skip-tests                 (bare, nothing follows)
    ///   --skip-tests true
    ///   --skip-tests --pub Web       (bare, another switch follows)
    ///
    /// and only an explicit --skip-tests false means false.
    ///
    /// The null is the point. <see cref="GetFlag"/> cannot express "not specified", so using it
    /// for an override would turn every run without the flag into an explicit false and quietly
    /// countermand the commit message.
    /// </summary>
    public bool? GetOptionalBool(string key)
    {
        if (!_present.Contains(key)) return null;

        // Absent from _values means the switch was last on the line; a value that is itself a
        // switch means the next token belongs to someone else. Both are the bare form.
        if (!_values.TryGetValue(key, out var v) || v.StartsWith("--", StringComparison.Ordinal))
            return true;

        // An unparseable value reads as true rather than as absent: the user typed the switch,
        // and honouring that is friendlier than silently ignoring a typo'd value.
        return !bool.TryParse(v, out var parsed) || parsed;
    }
}