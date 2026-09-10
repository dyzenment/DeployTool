using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;

namespace Dytools.DeployTool.Resolvers;

/// <summary>
/// What makes one publish different from another. Two targets whose build blocks resolve to
/// the same variant share one <c>dotnet publish</c> and one artifact folder; the planner uses
/// this to collapse "IIS + folder, same build" into a single build with two apply steps.
///
/// Every field of BuildConfig participates. It would be tempting to leave noWarn out - it
/// does not change the bits - but two targets that disagree on it are asking for two
/// different command lines, and silently running one of them for both is the kind of
/// surprise this tool exists to avoid. A null build block is the default build, so a target
/// that omits <c>build</c> shares with one that spells out the defaults.
/// </summary>
public static class BuildVariant
{
    /// <summary>
    /// Identity string. Equal keys mean "one publish serves both". Case-insensitive on the
    /// string fields, since RIDs, TFMs and configurations are compared that way by the SDK.
    /// </summary>
    public static string Key(BuildConfig? build)
    {
        build ??= new BuildConfig();

        // Sorted, deduplicated codes: "A,B" and "B, a" are the same suppression list.
        var noWarn = BuildHelper.MergeNoWarn(build.NoWarn, null)?
            .Split(',')
            .Select(c => c.ToLowerInvariant())
            .OrderBy(c => c, StringComparer.Ordinal);

        return string.Join("|",
            Norm(build.Configuration),
            Norm(build.Runtime),
            Norm(build.TargetFramework),
            build.SelfContained?.ToString() ?? string.Empty,
            build.SingleFile?.ToString() ?? string.Empty,
            noWarn is null ? string.Empty : string.Join(",", noWarn));
    }

    /// <summary>
    /// Folder-name fragment: "release-win-x64-net8.0-sc-single". Only the fields that
    /// describe the output take part, so the name says what is inside without becoming a
    /// hash. Not unique on its own - the planner disambiguates when two variants collide here
    /// but differ in Key.
    /// </summary>
    public static string Slug(BuildConfig? build)
    {
        build ??= new BuildConfig();

        var parts = new List<string> { Norm(build.Configuration) };

        if (!string.IsNullOrWhiteSpace(build.Runtime))         parts.Add(Norm(build.Runtime));
        if (!string.IsNullOrWhiteSpace(build.TargetFramework)) parts.Add(Norm(build.TargetFramework));
        if (build.SelfContained is { } sc)                      parts.Add(sc ? "sc" : "fdd");
        if (build.SingleFile == true)                           parts.Add("single");

        return string.Join("-", parts.Select(Safe));
    }

    /// <summary>Human-readable form for plan output and reports: "Release / win-x64 / net8.0".</summary>
    public static string Label(BuildConfig? build)
    {
        build ??= new BuildConfig();

        var parts = new List<string> { build.Configuration };

        if (!string.IsNullOrWhiteSpace(build.Runtime))         parts.Add(build.Runtime);
        if (!string.IsNullOrWhiteSpace(build.TargetFramework)) parts.Add(build.TargetFramework);
        if (build.SelfContained is { } sc)                      parts.Add(sc ? "self-contained" : "framework-dependent");
        if (build.SingleFile == true)                           parts.Add("single-file");

        return string.Join(" / ", parts);
    }

    private static string Norm(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>Anything a TFM or RID could contain that a folder name cannot.</summary>
    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
    }
}
