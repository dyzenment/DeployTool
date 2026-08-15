using System.Text.RegularExpressions;

namespace Dytools.DeployTool.Resolvers;

/// <summary>
/// Directives that steer the deploy. Two sources feed the same shape:
///
///   • the HEAD commit message  (<see cref="Parse"/>) - the normal CI path
///   • command-line overrides   (<see cref="FromValues"/>) - for hand/test runs
///
///   pub:Web|Proc*   publish exactly these projects (pipe-separated name globs)
///   pub:*           publish everything
///   pub:none        publish nothing - no project is named "none", so it simply matches
///                   nothing. Deliberately not a special case.
///   wait:0          override the rollout soak delay, in seconds
///   wait:60         wait 60 seconds before peers receive the build
///
/// Pure and side-effect free: the git call happens in the caller so this stays trivially
/// testable. CLI overrides are layered over the commit directives with <see cref="OverlaidWith"/>.
/// </summary>
public sealed class CommitDirectives
{
    /// <summary>Name globs from a pub: directive. Null when the commit carries no pub:.</summary>
    public IReadOnlyList<string>? PubPatterns { get; init; }

    /// <summary>Seconds from a wait: directive. Null when the commit carries no wait:.</summary>
    public int? WaitSeconds { get; init; }

    /// <summary>True when the commit explicitly stated what to publish.</summary>
    public bool HasPub => PubPatterns is not null;

    public static readonly CommitDirectives None = new();

    // pub: runs to the next whitespace, so it works anywhere in a multi-line message.
    private static readonly Regex PubRegex =
        new(@"\bpub:(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WaitRegex =
        new(@"\bwait:(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static CommitDirectives Parse(string? commitMessage)
    {
        if (string.IsNullOrWhiteSpace(commitMessage)) return None;

        var pubMatch  = PubRegex.Match(commitMessage);
        var waitMatch = WaitRegex.Match(commitMessage);

        var patterns = pubMatch.Success ? SplitPubPatterns(pubMatch.Groups[1].Value) : null;

        int? waitSeconds = null;
        if (waitMatch.Success && int.TryParse(waitMatch.Groups[1].Value, out var parsed))
            waitSeconds = parsed;

        return new CommitDirectives { PubPatterns = patterns, WaitSeconds = waitSeconds };
    }

    /// <summary>
    /// Builds directives from explicit values (command-line overrides). A null field means
    /// "not specified" - the same absence a commit message with no such directive produces,
    /// so it overlays cleanly. <paramref name="pubRaw"/> uses the same pipe-separated glob
    /// syntax as pub: ("Web|Proc*", "*", "none").
    /// </summary>
    public static CommitDirectives FromValues(string? pubRaw, int? waitSeconds)
        => new()
        {
            PubPatterns = pubRaw is null ? null : SplitPubPatterns(pubRaw),
            WaitSeconds = waitSeconds
        };

    /// <summary>
    /// Returns a new set of directives with this instance as the base and
    /// <paramref name="overrides"/> layered on top, field by field: any field the override
    /// specifies (non-null) wins; unspecified fields fall through to the base. Used to let
    /// command-line flags override commit-message directives on a hand/test run.
    /// </summary>
    public CommitDirectives OverlaidWith(CommitDirectives overrides)
        => new()
        {
            PubPatterns = overrides.PubPatterns ?? PubPatterns,
            WaitSeconds = overrides.WaitSeconds ?? WaitSeconds
        };

    private static List<string> SplitPubPatterns(string raw)
        => raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .ToList();

    /// <summary>
    /// True if a project name matches any of the pub: globs. Case-insensitive;
    /// '*' matches any run of characters.
    /// </summary>
    public bool MatchesPub(string projectName)
        => PubPatterns is not null && PubPatterns.Any(p => GlobMatches(projectName, p));

    /// <summary>Translates a shell-style glob into an anchored regex match.</summary>
    private static bool GlobMatches(string name, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }
}
