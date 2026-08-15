using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Services;

public sealed class VelopackHandler : IDeployTypeHandler
{
    public DeployType SupportedType => DeployType.Velopack;

    /// <summary>
    /// Global for every delivery mode. downloadPackAndUpload targets an Azure/S3/GitHub
    /// container reachable from anywhere; packOnly and downloadAndPack produce nothing a
    /// server serves (packCache is a build-time delta cache, not a deploy destination).
    /// Scoped per-target so a future delivery mode that lands bits on a box can differ
    /// without the planner changing.
    /// </summary>
    public DeployScope GetScope(TargetConfig target) => DeployScope.Global;

    public async Task<TargetDeployResult> ApplyAsync(ApplyStep step)
    {
        var velo = step.Velopack
            ?? throw new InvalidOperationException(
                $"Project '{step.Project}': type is 'velopack' but the 'velopack' block is missing.");

        var vpk = velo.Config;

        if (vpk.Delivery != VelopackDelivery.PackOnly && vpk.Source is null)
            throw new InvalidOperationException(
                $"Project '{step.Project}': delivery is '{vpk.Delivery}' but no 'source' block is configured.");

        var result = new TargetDeployResult { TargetLabel = step.Label, Type = DeployType.Velopack };

        // The artifact dir is owned by the caller. Only the pack dir needs local scratch,
        // and only when no packCache is configured.
        var tempBase   = Path.Combine(Path.GetTempPath(), "DeployTool", "vpk", ShortId());
        var publishDir = step.Artifact;
        var packDir    = ResolvePackDir(velo.PackCache, vpk, tempBase);

        Directory.CreateDirectory(packDir);

        string? backupDir = null;

        try
        {
            // -- 1. Read version from published DLL ----------------------------
            var assemblyPath = FindPublishedDll(publishDir, velo.AssemblyName);
            var version      = ReadFileVersion(assemblyPath);
            var veloVersion  = ToVelopackVersion(version);
            Console.WriteLine($"  [Velopack] Version: {version} -> Velopack: {veloVersion}");

            // -- 2. vpk download -----------------------------------------------
            if (vpk.Delivery is VelopackDelivery.DownloadAndPack or VelopackDelivery.DownloadPackAndUpload)
            {
                var downloadResult = await RunVpkDownloadAsync(vpk, packDir, velo.ProjectFolder);
                result.DeploySteps.Add(downloadResult);
                if (!downloadResult.Success) throw new DeployException("vpk download failed.");
            }

            // -- 3. Snapshot for rollback --------------------------------------
            if (step.Rollback && Directory.GetFiles(packDir).Length > 0)
            {
                backupDir = packDir.TrimEnd('\\', '/') + "_rollback_" + DateTimeOffset.Now.ToUnixTimeSeconds();
                FileHelper.CopyDirectory(packDir, backupDir);
                result.DeploySteps.Add(ProcessRunner.Synthetic(
                    "Snapshot packDir", $"Backed up to {backupDir}"));
            }

            // -- 4. vpk pack (with icon retry logic) ---------------------------
            var mainExe    = ResolveMainExe(vpk, velo.AssemblyName, velo.BuildRuntime);
            var packResult = await TryVpkPackAsync(vpk, veloVersion, publishDir, packDir,
                                 mainExe, velo.IconPath, velo.ProjectFolder, result);
            if (!packResult.Success) throw new DeployException("vpk pack failed.");

            // -- 5. vpk upload -------------------------------------------------
            if (vpk.Delivery == VelopackDelivery.DownloadPackAndUpload)
            {
                var uploadResult = await RunVpkUploadAsync(vpk, packDir, velo.ProjectFolder);
                result.DeploySteps.Add(uploadResult);
                if (!uploadResult.Success) throw new DeployException("vpk upload failed.");
            }

            result.Success = true;
        }
        catch (DeployException ex)
        {
            result.ErrorMessage = ex.Message;
            Console.WriteLine($"  [Velopack] ✗ {ex.Message}");

            if (step.Rollback && backupDir is not null && Directory.Exists(backupDir))
            {
                Console.WriteLine("  [Velopack] Rolling back packDir...");
                FileHelper.TryDeleteDirectory(packDir);
                Directory.Move(backupDir, packDir);
                result.RolledBack = true;
                Console.WriteLine("  [Velopack] Rollback complete.");
            }
        }
        finally
        {
            FileHelper.TryDeleteDirectory(tempBase);
            if (result.Success && backupDir is not null)
                FileHelper.TryDeleteDirectory(backupDir);
            ApplyPackCacheRetention(velo.PackCache, vpk);
        }

        return result;
    }

    // -- Icon resolution -------------------------------------------------------

    /// <summary>
    /// Resolves the icon to pass to vpk pack in priority order:
    ///   1. Explicit icon in velopack config (repo-root, project-relative, or absolute)
    ///   2. ApplicationIcon resolved from .csproj at startup (stored in DiscoveredProject)
    ///   3. Null - TryVpkPackAsync handles the retry/fallback
    /// Tries the path as-is, then with the platform-appropriate extension swapped.
    ///
    /// Called at plan time so the resolved path lands on the ApplyStep - the executor
    /// never touches DiscoveredProject.
    /// </summary>
    public static string? ResolveIconPath(
        VelopackConfig vpk, DiscoveredProject project, string? runtime)
    {
        var expectedExt = GetIconExtension(runtime);

        string? TryWithExt(string absPath)
        {
            if (File.Exists(absPath)) return absPath;
            var swapped = Path.ChangeExtension(absPath, expectedExt);
            return File.Exists(swapped) ? swapped : null;
        }

        // 1. Explicit config icon
        if (!string.IsNullOrWhiteSpace(vpk.Icon))
        {
            var expanded = FileHelper.ExpandEnvVars(vpk.Icon);
            if (!Path.IsPathRooted(expanded))
            {
                // Try relative to repo root (parent of project folder's parent)
                // and relative to project folder
                var fromProject = Path.GetFullPath(Path.Combine(project.Folder, expanded));
                var result      = TryWithExt(fromProject);
                if (result is not null) return result;
            }
            else
            {
                var result = TryWithExt(expanded);
                if (result is not null) return result;
            }

            Console.WriteLine($"  [Velopack] Warning: icon '{vpk.Icon}' not found.");
            return null;
        }

        // 2. ApplicationIcon from .csproj (pre-resolved at startup)
        if (project.CsprojIconPath is not null)
        {
            var result = TryWithExt(project.CsprojIconPath);
            if (result is not null)
            {
                Console.WriteLine($"  [Velopack] Using icon from .csproj: {result}");
                return result;
            }
        }

        return null;
    }

    private static string GetIconExtension(string? runtime)
    {
        if (runtime is null) return ".ico";
        if (runtime.StartsWith("osx", StringComparison.OrdinalIgnoreCase))   return ".icns";
        if (runtime.StartsWith("linux", StringComparison.OrdinalIgnoreCase)) return ".png";
        return ".ico";
    }

    // -- vpk pack with icon retry ----------------------------------------------

    private static async Task<StepResult> TryVpkPackAsync(
        VelopackConfig vpk, string version, string publishDir, string packDir,
        string mainExe, string? resolvedIcon, string workingDir, TargetDeployResult result)
    {
        var args       = BuildVpkPackArgs(vpk, version, publishDir, packDir, mainExe, resolvedIcon);
        var packResult = await ProcessRunner.RunAsync("vpk pack", "vpk", args, workingDir);
        result.DeploySteps.Add(packResult);
        if (packResult.Success) return packResult;

        var isIconError = (packResult.Stdout + packResult.Stderr)
            .Contains("--icon", StringComparison.OrdinalIgnoreCase);

        if (!isIconError || resolvedIcon is null)
            throw new DeployException("vpk pack failed.");

        var icnsPath = Path.ChangeExtension(resolvedIcon, ".icns");
        if (File.Exists(icnsPath))
        {
            Console.WriteLine($"  [Velopack] Icon rejected -- retrying with .icns: {icnsPath}");
            args       = BuildVpkPackArgs(vpk, version, publishDir, packDir, mainExe, icnsPath);
            packResult = await ProcessRunner.RunAsync("vpk pack (icns retry)", "vpk", args, workingDir);
            result.DeploySteps.Add(packResult);
            if (packResult.Success) return packResult;

            isIconError = (packResult.Stdout + packResult.Stderr)
                .Contains("--icon", StringComparison.OrdinalIgnoreCase);
            if (!isIconError) throw new DeployException("vpk pack failed.");
        }
        else
        {
            Console.WriteLine($"  [Velopack] Icon rejected and no .icns at {icnsPath} -- retrying without icon.");
        }

        Console.WriteLine("  [Velopack] Retrying vpk pack without icon.");
        args       = BuildVpkPackArgs(vpk, version, publishDir, packDir, mainExe, null);
        packResult = await ProcessRunner.RunAsync("vpk pack (no icon)", "vpk", args, workingDir);
        result.DeploySteps.Add(packResult);
        if (!packResult.Success) throw new DeployException("vpk pack failed.");
        return packResult;
    }

    // -- vpk download / upload -------------------------------------------------

    private static Task<StepResult> RunVpkDownloadAsync(
        VelopackConfig vpk, string packDir, string workingDir)
    {
        var typeStr = vpk.Source!.Type.ToString().ToLowerInvariant();
        var args    = BuildVpkSourceArgs("download", typeStr, vpk, packDir, redact: false);
        var display = BuildVpkSourceArgs("download", typeStr, vpk, packDir, redact: true);
        return ProcessRunner.RunAsync($"vpk download {typeStr}", "vpk", args, workingDir,
            displayArguments: display);
    }

    private static Task<StepResult> RunVpkUploadAsync(
        VelopackConfig vpk, string packDir, string workingDir)
    {
        var typeStr = vpk.Source!.Type.ToString().ToLowerInvariant();
        var args    = BuildVpkSourceArgs("upload", typeStr, vpk, packDir, redact: false);
        var display = BuildVpkSourceArgs("upload", typeStr, vpk, packDir, redact: true);
        return ProcessRunner.RunAsync($"vpk upload {typeStr}", "vpk", args, workingDir,
            displayArguments: display);
    }

    // -- Argument builders -----------------------------------------------------

    private static string BuildVpkPackArgs(
        VelopackConfig vpk, string version, string publishDir, string packDir,
        string mainExe, string? resolvedIcon)
    {
        var directive = GetCrossCompileDirective(vpk.Runtime);

        var args = new List<string>();
        if (!string.IsNullOrEmpty(directive)) args.Add(directive);
        args.AddRange([
            "pack",
            $"--packId \"{vpk.PackId}\"",
            $"--packVersion \"{version}\"",
            $"--packDir \"{publishDir}\"",
            $"--outputDir \"{packDir}\"",
            $"--mainExe \"{mainExe}\""
        ]);

        if (!string.IsNullOrWhiteSpace(vpk.PackTitle)) args.Add($"--packTitle \"{vpk.PackTitle}\"");
        if (!string.IsNullOrWhiteSpace(resolvedIcon))  args.Add($"--icon \"{resolvedIcon}\"");
        if (!string.IsNullOrWhiteSpace(vpk.Channel))   args.Add($"--channel \"{vpk.Channel}\"");
        if (!string.IsNullOrWhiteSpace(vpk.Runtime))   args.Add($"--runtime \"{vpk.Runtime}\"");
        if (!string.IsNullOrWhiteSpace(vpk.Framework)) args.Add($"--framework \"{vpk.Framework}\"");

        return string.Join(" ", args);
    }

    private static string BuildVpkSourceArgs(
        string command, string typeStr, VelopackConfig vpk, string packDir, bool redact)
    {
        var source = vpk.Source!;
        string Credential(string? raw) => redact ? "*private*" : FileHelper.ExpandEnvVars(raw);

        string? container = source.Container;
        string? prefix    = source.Prefix;

        if (source.Type == VelopackSourceType.Az &&
            !string.IsNullOrWhiteSpace(container) &&
            string.IsNullOrWhiteSpace(prefix) &&
            container.Contains('/'))
        {
            var slash = container.IndexOf('/');
            prefix    = container[(slash + 1)..];
            container = container[..slash];
            if (!redact)
                Console.WriteLine($"  [Velopack] Normalized az container: '{source.Container}' -> container='{container}' prefix='{prefix}'");
        }

        var args = new List<string> { command, typeStr, $"--outputDir \"{packDir}\"" };

        if (!string.IsNullOrWhiteSpace(vpk.Channel))
            args.Add($"--channel \"{vpk.Channel}\"");

        switch (source.Type)
        {
            case VelopackSourceType.Az:
                if (!string.IsNullOrWhiteSpace(source.Account))  args.Add($"--account \"{Credential(source.Account)}\"");
                if (!string.IsNullOrWhiteSpace(source.Key))      args.Add($"--key \"{Credential(source.Key)}\"");
                if (!string.IsNullOrWhiteSpace(source.Sas))      args.Add($"--sas \"{Credential(source.Sas)}\"");
                if (!string.IsNullOrWhiteSpace(container))       args.Add($"--container \"{container}\"");
                if (!string.IsNullOrWhiteSpace(prefix))          args.Add($"--prefix \"{prefix}\"");
                if (!string.IsNullOrWhiteSpace(source.Endpoint)) args.Add($"--endpoint \"{source.Endpoint}\"");
                break;
            case VelopackSourceType.S3:
                if (!string.IsNullOrWhiteSpace(source.KeyId))    args.Add($"--keyId \"{Credential(source.KeyId)}\"");
                if (!string.IsNullOrWhiteSpace(source.Secret))   args.Add($"--secret \"{Credential(source.Secret)}\"");
                if (!string.IsNullOrWhiteSpace(source.Region))   args.Add($"--region \"{source.Region}\"");
                if (!string.IsNullOrWhiteSpace(source.Bucket))   args.Add($"--bucket \"{source.Bucket}\"");
                if (!string.IsNullOrWhiteSpace(source.Endpoint)) args.Add($"--endpoint \"{source.Endpoint}\"");
                if (!string.IsNullOrWhiteSpace(source.Prefix))   args.Add($"--prefix \"{source.Prefix}\"");
                break;
            case VelopackSourceType.GitHub:
            case VelopackSourceType.Gitea:
                if (!string.IsNullOrWhiteSpace(source.RepoUrl)) args.Add($"--repoUrl \"{source.RepoUrl}\"");
                if (!string.IsNullOrWhiteSpace(source.Token))   args.Add($"--token \"{Credential(source.Token)}\"");
                break;
            case VelopackSourceType.Local:
                if (!string.IsNullOrWhiteSpace(source.Path)) args.Add($"--path \"{FileHelper.ExpandEnvVars(source.Path)}\"");
                break;
            case VelopackSourceType.Http:
                if (!string.IsNullOrWhiteSpace(source.Url)) args.Add($"--url \"{source.Url}\"");
                break;
        }

        if (command == "upload" && source.KeepMaxReleases.HasValue)
            args.Add($"--keepMaxReleases {source.KeepMaxReleases.Value}");

        return string.Join(" ", args);
    }

    // -- Cross-compile directive -----------------------------------------------

    private static string GetCrossCompileDirective(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime)) return string.Empty;

        var targetOs  = GetTargetOs(runtime);
        var currentOs = OperatingSystem.IsWindows() ? "win"
                      : OperatingSystem.IsMacOS()   ? "osx"
                      : "linux";

        if (targetOs == currentOs) return string.Empty;

        if (targetOs == "osx" && !OperatingSystem.IsMacOS())
            Console.WriteLine("  [Velopack] Warning: macOS packages can only be built on macOS.");

        Console.WriteLine($"  [Velopack] Cross-compiling: current={currentOs}, target={targetOs} -> [{targetOs}]");
        return $"[{targetOs}]";
    }

    private static string GetTargetOs(string runtime)
    {
        if (runtime.StartsWith("win",   StringComparison.OrdinalIgnoreCase)) return "win";
        if (runtime.StartsWith("osx",   StringComparison.OrdinalIgnoreCase)) return "osx";
        if (runtime.StartsWith("linux", StringComparison.OrdinalIgnoreCase)) return "linux";
        return "win";
    }

    // -- Version ---------------------------------------------------------------

    private static Version ReadFileVersion(string assemblyPath)
    {
        var fileVersion = System.Diagnostics.FileVersionInfo
            .GetVersionInfo(assemblyPath).FileVersion;

        if (!string.IsNullOrWhiteSpace(fileVersion) && Version.TryParse(fileVersion, out var parsed))
            return parsed;

        throw new InvalidOperationException(
            $"Could not read a valid FileVersion from '{assemblyPath}'. " +
            $"Raw value: '{fileVersion}'.");
    }

    private static string ToVelopackVersion(Version v)
        => $"{(v.Major * 100) + v.Minor}.{v.Build}.{v.Revision}";

    // -- Assembly / exe --------------------------------------------------------

    private static string FindPublishedDll(string publishDir, string assemblyName)
    {
        var dllPath = Path.Combine(publishDir, assemblyName + ".dll");
        if (File.Exists(dllPath)) return dllPath;
        throw new FileNotFoundException(
            $"Published DLL not found in '{publishDir}' for AssemblyName '{assemblyName}'.");
    }

    private static string ResolveMainExe(VelopackConfig vpk, string assemblyName, string? runtime)
        => !string.IsNullOrWhiteSpace(vpk.MainExe) ? vpk.MainExe
            : assemblyName + (runtime?.StartsWith("win", StringComparison.OrdinalIgnoreCase) == true
                ? ".exe" : string.Empty);

    // -- Pack cache ------------------------------------------------------------

    private static string ResolvePackDir(PackCacheConfig? packCache, VelopackConfig vpk, string tempBase)
    {
        if (packCache is null) return Path.Combine(tempBase, "pack");
        var expanded = ExpandPackCachePath(packCache.Path, vpk.PackId);
        Directory.CreateDirectory(expanded);
        return expanded;
    }

    private static void ApplyPackCacheRetention(PackCacheConfig? packCache, VelopackConfig vpk)
    {
        if (packCache?.KeepReleases is null) return;
        var dir = ExpandPackCachePath(packCache.Path, vpk.PackId);
        if (!Directory.Exists(dir)) return;

        var maxFiles = packCache.KeepReleases.Value * 2;
        foreach (var file in new DirectoryInfo(dir)
            .GetFiles("*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(maxFiles))
        {
            try { file.Delete(); } catch { }
        }
    }

    private static string ExpandPackCachePath(string path, string packId)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return FileHelper.ExpandEnvVars(
            path.Replace("{CommonAppData}", appData).Replace("{packId}", packId));
    }

    private static string ShortId() => Guid.NewGuid().ToString("N")[..8];
}
