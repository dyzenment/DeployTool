namespace Dytools.DeployTool.Helpers;

/// <summary>
/// File system utilities shared across deploy type handlers.
/// </summary>
public static class FileHelper
{
    /// <summary>
    /// Recursively copies all files and subdirectories from source to dest.
    /// Creates dest if it does not exist.
    /// </summary>
    public static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Mirrors source into dest: copies new/changed files, deletes anything in dest
    /// that no longer exists in source. Equivalent to robocopy /MIR but cross-platform.
    /// Creates dest if it does not exist.
    /// </summary>
    public static void MirrorDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);

        // Copy new / overwrite changed files
        foreach (var srcFile in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, srcFile);
            var destFile = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(srcFile, destFile, overwrite: true);
        }

        // Delete files in dest that are no longer in source
        foreach (var destFile in Directory.GetFiles(dest, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dest, destFile);
            if (!File.Exists(Path.Combine(source, relative)))
                File.Delete(destFile);
        }

        // Delete directories in dest that are no longer in source
        foreach (var destDir in Directory.GetDirectories(dest, "*", SearchOption.AllDirectories)
                                         .OrderByDescending(d => d.Length)) // deepest first
        {
            var relative = Path.GetRelativePath(dest, destDir);
            if (!Directory.Exists(Path.Combine(source, relative)))
                Directory.Delete(destDir, recursive: false);
        }
    }

    /// <summary>
    /// Deletes a directory and all its contents. Silently swallows exceptions
    /// so cleanup failures do not mask the original deployment result.
    /// </summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup. Log if needed but do not propagate.
        }
    }

    /// <summary>
    /// Expands shell path shorthands and environment variable references in a string.
    /// Supports:
    ///   ~/path    → /Users/you/path  (home directory prefix)
    ///   %VAR%     → Windows-style env var
    ///   $VAR      → Unix-style env var
    ///   ${VAR}    → Unix-style env var with braces
    /// Use this for any path that may come from deploy-config.json user input.
    /// </summary>
    public static string ExpandEnvVars(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        // ~/path or ~\ expansion (must happen before env var expansion)
        if (value.StartsWith("~/", StringComparison.Ordinal) ||
            value.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            value = home + value[1..];
        }

        // %VAR% style (Windows native)
        var expanded = Environment.ExpandEnvironmentVariables(value);

        // $VAR or ${VAR} style
        expanded = System.Text.RegularExpressions.Regex.Replace(
            expanded,
            @"\$\{?([A-Z_][A-Z0-9_]*)\}?",
            m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return expanded;
    }
}