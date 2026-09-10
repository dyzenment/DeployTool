using System.Text.Json;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Writes a run's DeployReport to result.json.
///
/// One schema for both roles: a full primary run and a peer's apply run produce the same
/// shape - a peer's simply has no pre-build, test or publish entries.
///
/// On a peer the file is also the idempotency marker. The agent polls every minute,
/// forever, and "manifest present + no result.json" is what makes a run pending; writing
/// this file is what stops a finished run being applied again on the next tick.
/// </summary>
public static class ReportWriter
{
    public const string FileName = "result.json";

    /// <summary>
    /// Per-stream cap. A dotnet publish log runs to megabytes; uncapped, build noise would
    /// dominate the report - and on a peer it would sit on a file share.
    /// </summary>
    private const int MaxStreamChars = 4000;

    public static void Write(DeployReport report, string directory)
    {
        // Truncated in place: the console already streamed the full output, and the report
        // is serialized once at the end of the run, so nothing downstream reads these again.
        TruncateStreams(report);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions.Write));
        Console.WriteLine($"  Report: {path}");
    }

    public static DeployReport? TryRead(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DeployReport>(File.ReadAllText(path), JsonOptions.Read)
                : null;
        }
        catch { return null; }
    }

    private static void TruncateStreams(DeployReport report)
    {
        foreach (var step in AllSteps(report))
        {
            step.Stdout = Truncate(step.Stdout);
            step.Stderr = Truncate(step.Stderr);
        }
    }

    private static IEnumerable<StepResult> AllSteps(DeployReport report)
    {
        foreach (var step in report.PrerequisiteResults)
            yield return step;

        foreach (var project in report.Results)
        {
            foreach (var step in project.PreBuildResults) yield return step;
            foreach (var step in project.TestResults)     yield return step;
            foreach (var publish in project.PublishResults) yield return publish.Result;

            foreach (var target in project.TargetResults)
                foreach (var step in target.DeploySteps) yield return step;
        }
    }

    private static string Truncate(string value)
        => string.IsNullOrEmpty(value) || value.Length <= MaxStreamChars
            ? value
            : value[..MaxStreamChars] + $"\n... [truncated {value.Length - MaxStreamChars} chars]";
}
