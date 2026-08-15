using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Services;

public sealed class FolderHandler : IDeployTypeHandler
{
    public DeployType SupportedType => DeployType.Folder;

    /// <summary>A destination folder and service live on a specific box.</summary>
    public DeployScope GetScope(TargetConfig target) => DeployScope.Server;

    public async Task<TargetDeployResult> ApplyAsync(ApplyStep step)
    {
        var folder = step.Folder
            ?? throw new InvalidOperationException(
                $"Project '{step.Project}': type is 'folder' but the 'folder' block is missing.");

        // Expanded on the box that owns the path - see IisHandler.
        var destinationPath = FileHelper.ExpandEnvVars(folder.DestinationPath);
        var hasService      = !string.IsNullOrWhiteSpace(folder.ServiceName);
        var result          = new TargetDeployResult { TargetLabel = step.Label, Type = DeployType.Folder };

        string? backupDir = null;

        try
        {
            if (hasService)
            {
                var stopResult = await ProcessRunner.RunAsync(
                    $"Stop service '{folder.ServiceName}'", "sc",
                    $"stop \"{folder.ServiceName}\"",
                    successPredicate: code => code == 0 || code == 1062);
                result.DeploySteps.Add(stopResult);
                if (!stopResult.Success) throw new DeployException($"Failed to stop service '{folder.ServiceName}'.");
                await Task.Delay(TimeSpan.FromSeconds(3));
            }

            if (step.Rollback && Directory.Exists(destinationPath))
            {
                backupDir = destinationPath.TrimEnd('\\', '/') + "_rollback_" + DateTimeOffset.Now.ToUnixTimeSeconds();
                FileHelper.CopyDirectory(destinationPath, backupDir);
                result.DeploySteps.Add(ProcessRunner.Synthetic("Snapshot destination", $"Backed up to {backupDir}"));
            }

            Directory.CreateDirectory(destinationPath);
            StepResult copyResult;
            try
            {
                FileHelper.MirrorDirectory(step.Artifact, destinationPath);
                copyResult = ProcessRunner.Synthetic("Mirror files to destination", $"{step.Artifact} -> {destinationPath}");
            }
            catch (Exception ex)
            {
                copyResult = ProcessRunner.Synthetic("Mirror files to destination", ex.Message, success: false);
            }
            result.DeploySteps.Add(copyResult);
            if (!copyResult.Success) throw new DeployException("File copy to destination failed.");

            if (hasService)
            {
                var startResult = await ProcessRunner.RunAsync(
                    $"Start service '{folder.ServiceName}'", "sc", $"start \"{folder.ServiceName}\"");
                result.DeploySteps.Add(startResult);
                if (!startResult.Success) throw new DeployException($"Failed to start service '{folder.ServiceName}'.");
            }

            result.Success = true;
        }
        catch (DeployException ex)
        {
            result.ErrorMessage = ex.Message;
            Console.WriteLine($"  [Folder] ✗ {ex.Message}");

            if (step.Rollback && backupDir is not null && Directory.Exists(backupDir))
            {
                FileHelper.TryDeleteDirectory(destinationPath);
                Directory.Move(backupDir, destinationPath);
                result.RolledBack = true;
            }

            if (hasService)
            {
                var recovery = await ProcessRunner.RunAsync(
                    $"Start service '{folder.ServiceName}' (error recovery)", "sc",
                    $"start \"{folder.ServiceName}\"");
                result.DeploySteps.Add(recovery);
            }
        }
        finally
        {
            if (result.Success && backupDir is not null)
                FileHelper.TryDeleteDirectory(backupDir);
        }

        return result;
    }
}
