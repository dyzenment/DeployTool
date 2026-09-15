using System.Diagnostics;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Executes shell commands and captures stdout/stderr into a StepResult.
///
/// All spawned processes are set to BelowNormal priority immediately after
/// launch to protect production server throughput. Combined with the YAML-level
/// priority reduction on the runner shell, this keeps the full deploy pipeline
/// from competing with live application processes.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Executes a command, streams output to console in real time, and captures
    /// everything into a StepResult for reporting.
    /// </summary>
    /// <param name="stepName">Human-readable label used in reports and console output.</param>
    /// <param name="executable">Program to execute (name or full path).</param>
    /// <param name="arguments">Arguments string to pass to the executable.</param>
    /// <param name="workingDirectory">
    ///     Working directory for the process. Defaults to the current directory.
    /// </param>
    /// <param name="environmentVariables">
    ///     Additional environment variables merged with the current process environment.
    /// </param>
    /// <param name="successPredicate">
    ///     Custom function to determine success from the exit code.
    ///     Defaults to exitCode == 0. Use this for tools like robocopy that
    ///     return non-zero codes that are not errors (robocopy: 0–7 = success).
    /// </param>
    public static async Task<StepResult> RunAsync(
        string stepName,
        string executable,
        string arguments,
        string? workingDirectory = null,
        Dictionary<string, string>? environmentVariables = null,
        Func<int, bool>? successPredicate = null,
        string? displayArguments = null)
    {
        var result = new StepResult
        {
            StepName  = stepName,
            Command   = $"{executable} {arguments}".Trim(),
            StartedAt = DateTimeOffset.Now
        };

        // Use redacted version for console output if provided
        var displayCmd = displayArguments is not null
            ? $"{executable} {displayArguments}".Trim()
            : result.Command;

        Console.WriteLine();
        Console.WriteLine($"  ┌- {stepName}");
        Console.WriteLine($"  │  {displayCmd}");

        var psi = new ProcessStartInfo
        {
            FileName               = executable,
            Arguments              = arguments,
            WorkingDirectory       = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        // Ask children not to colour their output in the first place. Stripping on the way
        // through is what guarantees a clean log, but a tool that knows not to emit escapes also
        // stops redrawing progress bars and printing spinner frames, which strip alone would
        // leave as a column of near-identical lines. Only when we are writing to a file.
        if (LogConsole.IsActive)
        {
            psi.Environment["NO_COLOR"] = "1";
            psi.Environment["MSBUILDTERMINALLOGGER"] = "off";
        }

        // Caller-supplied values win: set after, so a step can override either of the above.
        if (environmentVariables is not null)
            foreach (var (key, value) in environmentVariables)
                psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };

        var stdoutBuilder = new System.Text.StringBuilder();
        var stderrBuilder = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdoutBuilder.AppendLine(e.Data);
            Console.WriteLine($"  │  {e.Data}");
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrBuilder.AppendLine(e.Data);
            Console.Error.WriteLine($"  │  ERR {e.Data}");
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            
            // If the executable wasn't found and has no extension, retry with .cmd
            // This handles tools like npm that are batch files (npm.cmd) on Windows
            if (!Path.HasExtension(executable) && OperatingSystem.IsWindows())
            {
                var cmdExecutable = ResolveFullPath(executable + ".cmd")
                                    ?? ResolveFullPath(executable + ".bat")
                                    ?? executable + ".cmd";
                
                psi.FileName = cmdExecutable;
                result = new StepResult
                {
                    StepName  = result.StepName,
                    Command   = $"{cmdExecutable} {arguments}".Trim(),
                    StartedAt = result.StartedAt
                };

                try
                {
                    process.Start();
                    goto processStarted;
                }
                catch { /* fall through to original error */ }
            }
            
            result.Stdout      = string.Empty;
            result.Stderr      = ex.Message;
            result.ExitCode    = -1;
            result.Success     = false;
            result.CompletedAt = DateTimeOffset.Now;
            Console.WriteLine($"  └- ✗ Executable not found: {executable}");
            var path = Environment.GetEnvironmentVariable("PATH") ?? "(not set)";
            Console.WriteLine($"  [DEBUG] PATH at time of failure:");
            foreach (var entry in path.Split(';'))
                Console.WriteLine($"  [DEBUG]   {entry}");
            return result;
        }
        
        processStarted:
        
        // Apply BelowNormal priority immediately after launch.
        // The try/catch handles the race where the process exits before we set priority.
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* process exited before priority could be set - that's fine */ }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        result.Stdout       = stdoutBuilder.ToString();
        result.Stderr       = stderrBuilder.ToString();
        result.ExitCode     = process.ExitCode;
        result.Success      = successPredicate?.Invoke(process.ExitCode) ?? process.ExitCode == 0;
        result.CompletedAt  = DateTimeOffset.Now;

        var statusIcon = result.Success ? "✓" : "✗";
        Console.WriteLine($"  └- {statusIcon} Exit {result.ExitCode}  ({result.Duration?.TotalSeconds:F1}s)");

        return result;
    }
    
    /// <summary>
    /// Searches the current process PATH for the full path to a file.
    /// Returns null if not found.
    /// </summary>
    private static string? ResolveFullPath(string fileName)
    {
        var searchPaths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in searchPaths)
        {
            var candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
    
    /// <summary>
    /// Convenience overload for dotnet CLI commands.
    /// </summary>
    public static Task<StepResult> RunDotnetAsync(
        string stepName,
        string dotnetArguments,
        string? workingDirectory = null,
        Dictionary<string, string>? environmentVariables = null)
        => RunAsync(stepName, "dotnet", dotnetArguments, workingDirectory, environmentVariables);

    /// <summary>
    /// Creates a synthetic StepResult for logical steps that don't invoke a process
    /// (e.g. directory snapshots, skip decisions) so they appear uniformly in reports.
    /// </summary>
    public static StepResult Synthetic(string stepName, string description, bool success = true)
        => new()
        {
            StepName   = stepName,
            Command    = description,
            ExitCode   = success ? 0 : 1,
            Success    = success,
            StartedAt  = DateTimeOffset.Now,
            CompletedAt = DateTimeOffset.Now
        };
}