using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Manifest;
using Dytools.DeployTool.Services;

namespace Dytools.DeployTool.Resolvers;

/// <summary>
/// Turns a configured target into the self-describing ApplyStep the executor consumes.
///
/// Everything a handler needs is resolved here, at plan time - project metadata, icons,
/// labels - so that a step carries no reference back to DiscoveredProject or DeployConfig.
/// That is what allows the same step to be executed in memory on the primary or shipped
/// to a peer as JSON.
///
/// Deliberately does NOT expand %ENV% tokens in destination paths: those are expanded by
/// the handler on the box that owns the path, since values like %ProgramData% can differ
/// between the primary and a peer (and secrets should not be baked into a manifest).
/// </summary>
public static class ApplyStepBuilder
{
    public static ApplyStep Build(
        DiscoveredProject project,
        TargetConfig target,
        DeployConfig config,
        string artifactDir)
    {
        var build = target.Build ?? new BuildConfig();

        return target.Type switch
        {
            DeployType.Iis => new ApplyStep
            {
                Project  = project.Name,
                Type     = target.Type,
                Artifact = artifactDir,
                Rollback = target.Rollback,
                Label    = $"IIS / {FileHelper.ExpandEnvVars(target.Iis?.DeployPath)}",
                Iis      = target.Iis
            },

            DeployType.Folder => new ApplyStep
            {
                Project  = project.Name,
                Type     = target.Type,
                Artifact = artifactDir,
                Rollback = target.Rollback,
                Label    = $"Folder / {FileHelper.ExpandEnvVars(target.Folder?.DestinationPath)}",
                Folder   = target.Folder
            },

            DeployType.Velopack => new ApplyStep
            {
                Project  = project.Name,
                Type     = target.Type,
                Artifact = artifactDir,
                Rollback = target.Rollback,
                Label    = $"Velopack / {build.Runtime ?? "default-rid"} / {target.Velopack?.Delivery}",
                Velopack = target.Velopack is null ? null : new VelopackStep
                {
                    Config        = target.Velopack,
                    PackCache     = config.PackCache,
                    AssemblyName  = project.AssemblyName,
                    ProjectFolder = project.Folder,
                    BuildRuntime  = build.Runtime,
                    IconPath      = VelopackHandler.ResolveIconPath(target.Velopack, project, build.Runtime)
                }
            },

            _ => throw new InvalidOperationException(
                $"Project '{project.Name}': no step builder for deploy type '{target.Type}'.")
        };
    }
}
