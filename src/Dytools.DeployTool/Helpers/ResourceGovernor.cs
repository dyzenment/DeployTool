using System.Diagnostics;
using System.Runtime.InteropServices;
using Dytools.DeployTool.Models.Config;

namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Keeps the deploy pipeline out of the way of the applications already running on the box.
///
/// Both levers here are applied once to this process rather than per child, because both
/// propagate on their own:
///
///   * Priority. A child created by CreateProcess inherits the parent's priority class on
///     Windows, and a nice value survives fork/exec on Unix. Lowering this process therefore
///     also covers MSBuild worker nodes, VBCSCompiler, testhost and robocopy - including the
///     grandchildren this tool never holds a handle to. Lowering each child after Start()
///     cannot reach those: there is a window where the child runs at Normal, and whatever it
///     forks inside that window keeps Normal for its whole life.
///
///   * Environment. ProcessStartInfo.Environment is seeded from this process, so the build
///     server opt-outs below reach every descendant without touching a single call site.
///
/// Windows background mode is the exception - it applies only to the calling process, so it
/// governs the file copying this tool does itself (see FileHelper) rather than the build.
/// </summary>
public static class ResourceGovernor
{
    /// <summary>
    /// Lowers CPU, I/O and memory priority together, unlike a priority class which on Windows
    /// covers CPU alone. Windows only honours this for the calling process; passing another
    /// process handle fails, which is why there is no per-child equivalent.
    /// </summary>
    private const uint ProcessModeBackgroundBegin = 0x0010_0000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr handle, uint priorityClass);

    /// <summary>
    /// Settings in force for this run. Defaults apply until <see cref="Apply"/> is called with
    /// the loaded config, so the window before the config is parsed is still governed.
    /// </summary>
    public static ResourceConfig Settings { get; private set; } = new();

    /// <summary>
    /// MSBuild switches that stop a build spawning long-lived server processes and stop it
    /// fanning out across every core. Shared by build, publish and test so the three cannot
    /// drift apart.
    ///
    /// These matter more than priority does. MSBuild node reuse and the Roslyn compiler server
    /// are daemons that outlive the build that started them: a build which attaches to an
    /// existing node inherits that node's priority, not this process's, so no amount of
    /// priority lowering here would reach it.
    /// </summary>
    public static string MsBuildSwitches
    {
        get
        {
            var parts = new List<string>();

            if (Settings.DisableBuildServers)
            {
                parts.Add("-p:UseSharedCompilation=false");
                parts.Add("-nodeReuse:false");
            }

            if (Settings.MaxCpuCount > 0)
                parts.Add($"-maxcpucount:{Settings.MaxCpuCount}");

            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Lowers this process before anything else runs, using the built-in default rather than
    /// config - the config file has not been read yet at that point. Called first thing in
    /// Main so that the fewest possible child processes are created at Normal priority.
    /// </summary>
    public static void ApplyDefaultPriority() => ApplyPriority(new ResourceConfig().Priority);

    /// <summary>
    /// Applies the resource policy from the loaded config. Safe to call after
    /// <see cref="ApplyDefaultPriority"/>; it simply re-sets what that already set.
    /// </summary>
    public static void Apply(ResourceConfig? config)
    {
        Settings = config ?? new ResourceConfig();

        if (Settings.DisableBuildServers)
        {
            // The command-line switches above cover the calls this tool makes directly. These
            // cover everything those calls go on to spawn - and DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER
            // covers the MSBuild Server, which is a separate mechanism that -nodeReuse:false
            // does not disable.
            SetIfUnset("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "1");
            SetIfUnset("MSBUILDDISABLENODEREUSE", "1");
            SetIfUnset("UseSharedCompilation", "false");
        }

        // Server GC allocates a heap plus a dedicated thread per core. On a production box
        // those are cycles the live applications need more than a deploy does.
        if (Settings.WorkstationGc)
            SetIfUnset("DOTNET_gcServer", "0");

        ApplyPriority(Settings.Priority);

        // Must follow ApplyPriority: entering background mode drops the priority class to
        // Idle, so setting a class afterwards would undo it.
        if (Settings.BackgroundIo)
            ApplyBackgroundIo();
    }

    /// <summary>One-line summary for the run header, so a report says what policy was in force.</summary>
    public static string Describe()
    {
        var priority = Settings.Priority switch
        {
            ProcessPriority.BelowNormal => "below-normal",
            ProcessPriority.Idle        => "idle",
            _                           => "normal"
        };
        var cpu    = Settings.MaxCpuCount > 0 ? $"{Settings.MaxCpuCount} build core(s)" : "all build cores";
        var server = Settings.DisableBuildServers ? ", no build servers" : ", build servers allowed";
        var io     = Settings.BackgroundIo ? ", background i/o" : string.Empty;
        return $"{priority} priority, {cpu}{server}{io}";
    }

    private static void ApplyPriority(ProcessPriority priority)
    {
        var target = priority switch
        {
            ProcessPriority.Idle        => ProcessPriorityClass.Idle,
            ProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
            _                           => ProcessPriorityClass.Normal
        };

        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = target;
        }
        catch (Exception ex)
        {
            // Containers and hardened hosts can refuse setpriority/SetPriorityClass. A deploy
            // that runs at normal priority is badly behaved, not broken, so never fail here.
            Console.WriteLine($"  [Resources] Could not set priority to {target}: {ex.Message}");
        }
    }

    private static void ApplyBackgroundIo()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Linux has ioprio_set and macOS has no equivalent at all; neither is reachable
            // through BCL surface, so this stays a Windows-only knob rather than a silent no-op.
            Console.WriteLine("  [Resources] backgroundIo is a Windows facility - ignored on this platform.");
            return;
        }

        using var self = Process.GetCurrentProcess();
        if (!SetPriorityClass(self.Handle, ProcessModeBackgroundBegin))
            Console.WriteLine($"  [Resources] Could not enter background mode (win32 error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>
    /// Sets an inherited variable only when the caller has not already chosen a value, so a
    /// workflow or an operator can override any of this from the environment without having
    /// to edit deploy-config.json.
    /// </summary>
    private static void SetIfUnset(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }
}
