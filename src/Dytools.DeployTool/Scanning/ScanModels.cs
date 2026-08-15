namespace Dytools.DeployTool.Scanning;

/// <summary>What a discovered project looks like, guessed from its .csproj.</summary>
public enum ProjectKind
{
    Web,          // Microsoft.NET.Sdk.Web / ASP.NET Core  -> IIS
    Console,      // OutputType Exe                          -> Folder
    Desktop,      // WinExe / WPF / WinForms                 -> Folder (Velopack is opt-in)
    ClassLibrary, // no OutputType                           -> dependency only, not deployed
    Test,         // has a test SDK / framework              -> not deployed
    Unknown,      // unreadable csproj
}

/// <summary>One project found by the scanner.</summary>
public sealed class ScannedProject
{
    public required string Name { get; init; }
    public required string CsprojPath { get; init; }     // absolute
    public required string Folder { get; init; }         // absolute
    public required string RelativeFolder { get; init; } // repo-root-relative, forward slashes
    public required ProjectKind Kind { get; init; }

    /// <summary>Absolute paths of the .csproj files this project directly references.</summary>
    public IReadOnlyList<string> ReferencePaths { get; init; } = [];

    /// <summary>References the Velopack package - a strong hint it ships as a Velopack app.</summary>
    public bool UsesVelopack { get; init; }

    public bool IsTest => Kind == ProjectKind.Test;

    /// <summary>Projects the scaffolder turns into deploy targets (libraries/tests are excluded).</summary>
    public bool IsDeployable => Kind is ProjectKind.Web or ProjectKind.Console or ProjectKind.Desktop;
}

/// <summary>The full result of scanning a solution (or a csproj glob).</summary>
public sealed class ScanResult
{
    public required string RepoRoot { get; init; }
    public required string? SolutionPath { get; init; } // null in glob mode
    public required IReadOnlyList<ScannedProject> Projects { get; init; }
}
