using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;

namespace Dytools.DeployTool.Resolvers;

/// <summary>
/// Determines which projects need to be deployed based on the set of changed files.
/// Uses the pre-built ProjectRegistry - no additional file system scanning.
///
/// All paths are repo-root-relative with forward slashes, consistent with git diff output.
/// Matching is a simple path prefix check throughout.
/// </summary>
/// <summary>
/// The chosen projects and the rule that chose them. The reason travels into the plan's
/// audit trail so a rollout records not just what shipped, but why.
/// </summary>
public sealed record SelectionResult(List<DiscoveredProject> Projects, string Reason);

public static class DeployListResolver
{
    /// <summary>
    /// Selects the projects to deploy. Precedence, most specific intent first:
    ///
    ///   1. pub: directive  - an explicit statement in the commit message
    ///   2. --force-all     - an explicit manual action on the workflow
    ///   3. doNotPublishIfNoPubInCommitMessage - the safety opt-in, nothing to deploy
    ///   4. changed files   - the default inference
    ///
    /// A pub: directive replaces the SELECTION step only. Everything downstream -
    /// pre-build, unit tests, targets - still runs for each selected project.
    /// </summary>
    public static SelectionResult Resolve(
        ProjectRegistry registry,
        List<string> changedFiles,
        bool forceAll = false,
        CommitDirectives? directives = null,
        bool doNotPublishWithoutPub = false)
    {
        directives ??= CommitDirectives.None;

        var deployableProjects = registry.AllProjects
            .Where(p => p.IsDeployable)
            .ToList();

        // -- 1. pub: directive -------------------------------------------------
        if (directives.HasPub)
        {
            Console.WriteLine(
                $"[Resolver] pub: directive -- selecting by name: " +
                $"{string.Join(" | ", directives.PubPatterns!)}");

            var selected = new List<DiscoveredProject>();
            foreach (var project in deployableProjects)
            {
                if (directives.MatchesPub(project.Name))
                {
                    Console.WriteLine($"[Resolver] ✓ {project.Name,-30} matched pub: pattern");
                    selected.Add(project);
                }
                else
                {
                    Console.WriteLine($"[Resolver] ✗ {project.Name,-30} no pub: pattern matched");
                }
            }

            if (selected.Count == 0)
                Console.WriteLine("[Resolver] pub: matched no projects -- nothing to deploy.");

            return new SelectionResult(selected, $"pub: {string.Join(" | ", directives.PubPatterns!)}");
        }

        // -- 2. --force-all ----------------------------------------------------
        if (forceAll)
        {
            Console.WriteLine("[Resolver] Force-all flag set -- all projects selected.");
            return new SelectionResult(deployableProjects, "--force-all");
        }

        // -- 3. Safety opt-in --------------------------------------------------
        if (doNotPublishWithoutPub)
        {
            Console.WriteLine(
                "[Resolver] doNotPublishIfNoPubInCommitMessage is true and the commit carries " +
                "no pub: directive -- nothing to deploy. Use pub:* to deploy everything.");
            return new SelectionResult([], "no pub: directive (doNotPublishIfNoPubInCommitMessage)");
        }

        // -- 4. Changed files --------------------------------------------------
        if (changedFiles.Count == 0)
        {
            Console.WriteLine("[Resolver] No changed files -- nothing to deploy.");
            return new SelectionResult([], "no changed files");
        }

        // Normalize changed files to forward slashes
        var normalizedChanged = changedFiles
            .Select(f => f.Replace('\\', '/').TrimStart('/'))
            .ToList();

        // Solution-level changes: files not under any known project folder.
        // Uses AllProjects (including libraries) so e.g. src/Core/Foo.cs is
        // correctly identified as a project-level change, not solution-level.
        var allFolders = registry.AllProjects
            .Select(p => p.RelativeFolder)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var solutionChanges = normalizedChanged
            .Where(f => !allFolders.Any(pf => IsUnder(f, pf)))
            .ToList();

        if (solutionChanges.Count > 0)
        {
            Console.WriteLine($"[Resolver] {solutionChanges.Count} solution-level change(s):");
            foreach (var f in solutionChanges)
                Console.WriteLine($"  {f}");
        }

        var deployList = new List<DiscoveredProject>();

        foreach (var project in deployableProjects)
        {
            var watchFolders  = BuildWatchFolders(project, registry);
            var rawSubstrings = BuildRawSubstrings(project, registry);

            if (TryMatch(normalizedChanged, solutionChanges, watchFolders,
                    rawSubstrings, project.Config!.DependentProjects, out var reason))
            {
                Console.WriteLine($"[Resolver] ✓ {project.Name,-30} {reason}");
                deployList.Add(project);
            }
            else
            {
                Console.WriteLine($"[Resolver] ✗ {project.Name,-30} no relevant changes");
            }
        }

        return new SelectionResult(deployList, "changed files");
    }

    // -- Watch folder construction ---------------------------------------------

    private static HashSet<string> BuildWatchFolders(
        DiscoveredProject project, ProjectRegistry registry)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { project.RelativeFolder };

        // Transitive .csproj dependencies (already resolved in registry)
        foreach (var dep in project.TransitiveDependencies)
            folders.Add(dep.RelativeFolder);

        // Config-declared dependentProjects that resolve to known projects
        foreach (var dep in project.Config?.DependentProjects ?? [])
        {
            if (dep == "*") continue;
            if (registry.ByName.TryGetValue(dep, out var depProject))
                folders.Add(depProject.RelativeFolder);
        }

        return folders;
    }

    private static List<string> BuildRawSubstrings(
        DiscoveredProject project, ProjectRegistry registry)
    {
        var raw = new List<string>();
        foreach (var dep in project.Config?.DependentProjects ?? [])
        {
            if (dep == "*") continue;
            if (!registry.ByName.ContainsKey(dep))
                raw.Add(dep); // unresolvable - keep as substring fallback
        }
        return raw;
    }

    // -- Matching --------------------------------------------------------------

    private static bool TryMatch(
        List<string> changed,
        List<string> solutionChanges,
        HashSet<string> watchFolders,
        List<string> rawSubstrings,
        List<string> dependentProjects,
        out string reason)
    {
        foreach (var file in changed)
            foreach (var folder in watchFolders)
                if (IsUnder(file, folder))
                {
                    reason = $"change under {folder}";
                    return true;
                }

        foreach (var sub in rawSubstrings)
            if (changed.Any(f => f.Contains(sub, StringComparison.OrdinalIgnoreCase)))
            {
                reason = $"dependentProject '{sub}' matched a changed path";
                return true;
            }

        if (dependentProjects.Contains("*") && solutionChanges.Count > 0)
        {
            reason = "solution-level change (* catch-all)";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    // -- Debug -----------------------------------------------------------------

#if DEBUG
    public static void PrintGraph(ProjectRegistry registry)
    {
        Console.WriteLine();
        Console.WriteLine("-- Dependency Graph ---------------------------------------------");

        foreach (var project in registry.AllProjects.Where(p => p.IsDeployable))
        {
            Console.WriteLine($"  ┌ {project.Name}");
            Console.WriteLine($"  │  own      {project.RelativeFolder}");
            Console.WriteLine($"  │  csproj   {project.CsprojPath}");
            Console.WriteLine($"  │  assembly {project.AssemblyName}");

            if (project.CsprojIconPath is not null)
                Console.WriteLine($"  │  icon     {project.CsprojIconPath}");

            if (project.UnitTestCsprojPath is not null)
                Console.WriteLine($"  │  tests    {project.UnitTestCsprojPath}");

            if (project.TransitiveDependencies.Count > 0)
            {
                var deps = project.TransitiveDependencies
                    .OrderBy(d => d.RelativeFolder, StringComparer.OrdinalIgnoreCase);
                Console.WriteLine($"  │  deps     {string.Join($"\n  │           ", deps.Select(d => d.RelativeFolder))}");
            }
            else
            {
                Console.WriteLine("  │  deps     (none)");
            }

            if (project.Config?.DependentProjects.Count > 0)
            {
                var annotations = project.Config.DependentProjects.Select(dep =>
                {
                    if (dep == "*") return "*  -> solution catch-all";
                    if (registry.ByName.TryGetValue(dep, out var dp))
                        return $"{dep}  -> {dp.RelativeFolder}";
                    return $"{dep}  -> (unresolved -- substring fallback)";
                });
                Console.WriteLine($"  │  conf     {string.Join($"\n  │           ", annotations)}");
            }

            Console.WriteLine("  └");
        }

        Console.WriteLine();
        Console.WriteLine("  All known project folders:");
        foreach (var p in registry.AllProjects.OrderBy(p => p.RelativeFolder))
        {
            var tag = p.IsDeployable ? "" : "  [library]";
            Console.WriteLine($"    {p.RelativeFolder}{tag}");
        }

        Console.WriteLine("----------------------------------------------------------------");
        Console.WriteLine();
    }
#endif

    private static bool IsUnder(string filePath, string folderPath)
    {
        var prefix = folderPath.TrimEnd('/') + "/";
        return filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}