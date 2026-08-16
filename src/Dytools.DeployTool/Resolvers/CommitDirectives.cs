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
///   skiptests       deploy without running the unit-test gate
///   skiptests:false run the gate even if something else would have skipped it
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

    /// <summary>
    /// Whether to bypass the unit-test gate. Null when nothing said either way, which is what
    /// lets a command-line --skip-tests false countermand a skiptests in the commit message:
    /// "unspecified" and "explicitly off" have to be distinguishable for the overlay to work.
    /// </summary>
    public bool? SkipTests { get; init; }

    /// <summary>True when the commit explicitly stated what to publish.</summary>
    public bool HasPub => PubPatterns is not null;

    public static readonly CommitDirectives None = new();

    // pub: runs to the next whitespace, so it works anywhere in a multi-line message.
    private static readonly Regex PubRegex =
        new(@"\bpub:(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WaitRegex =
        new(@"\bwait:(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Unlike pub:/wait: this one is meaningful bare, since "skip the tests" needs no argument.
    // The optional :true/:false exists so a commit can say "no, definitely run them".
    // skiptests, skip-tests and skip_tests are all accepted - the separator is not worth a
    // failed deploy, and a directive nobody can misspell is a directive people will use.
    private static readonly Regex SkipTestsRegex =
        new(@"\bskip[-_]?tests(?::(true|false))?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static CommitDirectives Parse(string? commitMessage)
    {
        if (string.IsNullOrWhiteSpace(commitMessage)) return None;

        var pubMatch  = PubRegex.Match(commitMessage);
        var waitMatch = WaitRegex.Match(commitMessage);
        var skipMatch = SkipTestsRegex.Match(commitMessage);

        var patterns = pubMatch.Success ? SplitPubPatterns(pubMatch.Groups[1].Value) : null;

        int? waitSeconds = null;
        if (waitMatch.Success && int.TryParse(waitMatch.Groups[1].Value, out var parsed))
            waitSeconds = parsed;

        bool? skipTests = null;
        if (skipMatch.Success)
        {
            // Bare "skiptests" means skip; "skiptests:false" is the explicit opt-out.
            var value = skipMatch.Groups[1].Value;
            skipTests = value.Length == 0 || bool.Parse(value);
        }

        return new CommitDirectives
        {
            PubPatterns = patterns,
            WaitSeconds = waitSeconds,
            SkipTests   = skipTests
        };
    }

    /// <summary>
    /// Builds directives from explicit values (command-line overrides). A null field means
    /// "not specified" - the same absence a commit message with no such directive produces,
    /// so it overlays cleanly. <paramref name="pubRaw"/> uses the same pipe-separated glob
    /// syntax as pub: ("Web|Proc*", "*", "none").
    /// </summary>
    public static CommitDirectives FromValues(string? pubRaw, int? waitSeconds, bool? skipTests = null)
        => new()
        {
            PubPatterns = pubRaw is null ? null : SplitPubPatterns(pubRaw),
            WaitSeconds = waitSeconds,
            SkipTests   = skipTests
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
            WaitSeconds = overrides.WaitSeconds ?? WaitSeconds,
            SkipTests   = overrides.SkipTests   ?? SkipTests
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
