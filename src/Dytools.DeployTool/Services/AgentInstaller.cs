using System.Text;
using Dytools.DeployTool.Helpers;

namespace Dytools.DeployTool.Services;

/// <summary>
/// Stands a peer up so it can receive rollouts: creates the fixed folder layout, writes the
/// poll script, and registers the recurring task that runs it.
///
/// Run once per box, then add the box to <c>servers[]</c>. Nothing else about a peer is
/// configured - which is the design goal: the tool that applies a run arrives *with* the run,
/// so there is no installed version to keep in step with the primary, and no config file on
/// the peer to drift from the one in the repo.
///
/// Idempotent by construction. Folders are created only if missing, the poll script is
/// rewritten from the same template, and the task is registered with /F so a second run
/// upgrades the box rather than failing on it.
/// </summary>
public static class AgentInstaller
{
    public const string DefaultTaskName = "DeployAgent";
    public const int    DefaultIntervalMinutes = 1;

    /// <summary>Rolls the log at this size so an agent left running for years cannot fill a disk.</summary>
    private const long LogRollBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Accounts the task can run as without a password. Anything else has to be registered by
    /// hand: a deploy tool must never be in the business of collecting credentials, and there
    /// is no way to pass one to schtasks that does not put it on a command line.
    /// </summary>
    private static readonly Dictionary<string, (string Sid, string Label)> PasswordlessAccounts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["SYSTEM"]          = ("S-1-5-18", @"NT AUTHORITY\SYSTEM"),
            ["LOCALSERVICE"]    = ("S-1-5-19", @"NT AUTHORITY\LOCAL SERVICE"),
            ["NETWORKSERVICE"]  = ("S-1-5-20", @"NT AUTHORITY\NETWORK SERVICE")
        };

    /// <summary>The default agent root. The only fixed path in the system - see docs/ARCHITECTURE.md §8.</summary>
    public static string DefaultRoot =>
        OperatingSystem.IsWindows() ? @"C:\deploy" : "/var/lib/deploytool";

    // -- Install ---------------------------------------------------------------

    public static async Task<int> InstallAsync(AgentOptions options)
    {
        var layout = new AgentLayout(options.Root);

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  DeployTool  agent install");
        Console.WriteLine($"  Host:  {HostIdentity.Resolve()}");
        Console.WriteLine($"  Root:  {layout.Root}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        // -- Folders -----------------------------------------------------------

        Console.WriteLine("\n-- Folders -----------------------------------------------------");
        foreach (var dir in layout.All)
        {
            var existed = Directory.Exists(dir);
            Directory.CreateDirectory(dir);
            Console.WriteLine($"  {(existed ? "=" : "+")} {dir}");
        }

        // -- Poll script -------------------------------------------------------

        Console.WriteLine("\n-- Poll script -------------------------------------------------");
        var scriptPath = layout.PollScript;
        await File.WriteAllTextAsync(scriptPath, BuildPollScript(layout));

        if (!OperatingSystem.IsWindows())
            SetExecutable(scriptPath);

        Console.WriteLine($"  Wrote {scriptPath}");
        Console.WriteLine("  (deliberately dumb: it finds the newest binary that arrived and hands it the");
        Console.WriteLine("   whole incoming folder. The tool decides what is due, so this file never changes.)");

        // -- Schedule ----------------------------------------------------------

        Console.WriteLine("\n-- Schedule ----------------------------------------------------");

        var scheduled = OperatingSystem.IsWindows()
            ? await RegisterWindowsTaskAsync(layout, options)
            : PrintUnixSchedule(layout, options);

        PrintNextSteps(layout, scheduled, options);
        return scheduled ? 0 : 1;
    }

    // -- Uninstall -------------------------------------------------------------

    public static async Task<int> UninstallAsync(AgentOptions options)
    {
        var layout = new AgentLayout(options.Root);

        Console.WriteLine($"\n  Removing the agent schedule on {HostIdentity.Resolve()}.");

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine($"  Remove the crontab line that runs {layout.PollScript} by hand.");
            return 0;
        }

        var result = await ProcessRunner.RunAsync(
            "schtasks /Delete", "schtasks", $"/Delete /TN \"{options.TaskName}\" /F");

        if (!result.Success)
        {
            Console.WriteLine($"  ✗ Could not delete task '{options.TaskName}'. {ElevationHint()}");
            return 1;
        }

        Console.WriteLine($"  ✓ Task '{options.TaskName}' deleted.");

        // Folders are left on purpose: every run folder under incoming holds the result.json
        // that says what this box did and when. That history outlives the schedule.
        Console.WriteLine(
            $"  {layout.Root} was left in place - it holds this box's deploy history. " +
            "Delete it by hand if you want it gone.");

        return 0;
    }

    // -- Windows Task Scheduler ------------------------------------------------

    /// <summary>
    /// Registers via /XML rather than the /SC MINUTE switches, because two settings that the
    /// switch form cannot express are load-bearing:
    ///
    ///   * MultipleInstancesPolicy=IgnoreNew - a deploy takes longer than the poll interval,
    ///     so without it the scheduler would start a second apply on top of a running one.
    ///     This is what removes the need for a lock file.
    ///   * ExecutionTimeLimit=PT0S (unlimited) - the switch form inherits a 72-hour default,
    ///     and a task killed mid-mirror leaves neither the old build nor the new one.
    /// </summary>
    private static async Task<bool> RegisterWindowsTaskAsync(AgentLayout layout, AgentOptions options)
    {
        if (!PasswordlessAccounts.TryGetValue(options.RunAsUser.Replace(" ", string.Empty), out var account))
        {
            Console.WriteLine($"  ! '{options.RunAsUser}' needs a password, which this tool will not handle.");
            Console.WriteLine("    Register it yourself (you will be prompted for the password):");
            Console.WriteLine();
            Console.WriteLine($"      schtasks /Create /TN \"{options.TaskName}\" /TR \"{layout.PollScript}\" " +
                $"/SC MINUTE /MO {options.IntervalMinutes} /RU \"{options.RunAsUser}\" /RP * /RL HIGHEST /F");
            Console.WriteLine();
            Console.WriteLine("    Then set Multiple instances -> Ignore new instance in Task Scheduler,");
            Console.WriteLine("    or the scheduler will start a second apply on top of a running one.");
            return false;
        }

        var xmlPath = Path.Combine(Path.GetTempPath(), $"deployagent-{Guid.NewGuid():N}.xml");

        try
        {
            // UTF-16 with a BOM: schtasks rejects a task definition in any other encoding with
            // a message that blames the XML's contents rather than its bytes.
            await File.WriteAllTextAsync(
                xmlPath, BuildTaskXml(layout, options, account.Sid), Encoding.Unicode);

            var result = await ProcessRunner.RunAsync(
                "schtasks /Create", "schtasks",
                $"/Create /TN \"{options.TaskName}\" /XML \"{xmlPath}\" /F");

            if (!result.Success)
            {
                Console.WriteLine($"  ✗ Could not register task '{options.TaskName}'. {ElevationHint()}");
                return false;
            }

            Console.WriteLine($"  ✓ Task '{options.TaskName}' registered.");
            Console.WriteLine($"      runs      {layout.PollScript}");
            Console.WriteLine($"      every     {options.IntervalMinutes} minute(s), indefinitely");
            Console.WriteLine($"      as        {account.Label} (highest privileges)");
            Console.WriteLine("      overlap   ignored - a slow deploy is never doubled up");
            Console.WriteLine($"      log       {layout.LogFile}");
            return true;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* temp file; nothing depends on it */ }
        }
    }

    private static string BuildTaskXml(AgentLayout layout, AgentOptions options, string sid) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>DeployTool peer agent. Applies deploy runs dropped into {layout.Incoming} once their soak window has passed.</Description>
            <URI>\{options.TaskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <TimeTrigger>
              <StartBoundary>2000-01-01T00:00:00</StartBoundary>
              <Enabled>true</Enabled>
              <Repetition>
                <Interval>PT{options.IntervalMinutes}M</Interval>
                <StopAtDurationEnd>false</StopAtDurationEnd>
              </Repetition>
            </TimeTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{sid}</UserId>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>false</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{layout.PollScript}</Command>
            </Exec>
          </Actions>
        </Task>
        """;

    private static string ElevationHint() =>
        "Registering a task that runs as a service account needs an elevated shell - " +
        "start PowerShell or cmd with 'Run as administrator' and try again. " +
        "The schtasks output above says exactly what it objected to.";

    // -- Unix ------------------------------------------------------------------

    /// <summary>
    /// Prints the schedule instead of installing it. Editing a crontab or dropping a systemd
    /// unit is a change to how the box boots, and doing that behind someone's back on a
    /// server is worse than asking them to paste one line.
    /// </summary>
    private static bool PrintUnixSchedule(AgentLayout layout, AgentOptions options)
    {
        Console.WriteLine("  Not installed - add the schedule yourself, one line:");
        Console.WriteLine();
        Console.WriteLine($"      sudo crontab -e");
        Console.WriteLine($"      */{options.IntervalMinutes} * * * * {layout.PollScript}");
        Console.WriteLine();
        Console.WriteLine("  The script takes its own lock, so overlapping runs are already handled.");

        // False, so the closing summary does not claim this box is ready. It is not: nothing
        // will run the poll script until that line exists.
        return false;
    }

    private static void SetExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead  | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ! Could not mark the script executable: {ex.Message}");
            Console.WriteLine($"    Run: chmod +x \"{path}\"");
        }
    }

    // -- Poll script -----------------------------------------------------------

    public static string BuildPollScript(AgentLayout layout) =>
        OperatingSystem.IsWindows() ? BuildPollCmd(layout) : BuildPollSh(layout);

    private static string BuildPollCmd(AgentLayout layout) =>
        $"""
        @echo off
        REM ---------------------------------------------------------------------------
        REM DeployTool peer agent. Written once by `dytools-deploy install-agent`.
        REM
        REM This file is frozen on purpose. It does not parse manifests and does not
        REM check soak windows - JSON in batch is misery, and that logic belongs
        REM somewhere it can be unit tested. All it does is launch the tool that arrived
        REM with the newest run and hand it the whole incoming folder; the tool works out
        REM which runs are due, applies them, and writes its own completion markers.
        REM
        REM Because the tool travels with the run, the peer has no installed version to
        REM keep in step with the primary.
        REM ---------------------------------------------------------------------------
        setlocal
        set "INCOMING={layout.Incoming}"
        set "LOG={layout.LogFile}"

        REM Keep one generation of log. The task runs every minute, forever.
        REM Guarded on existence: %%~zs is empty for a missing file, and `if  GTR n` is a
        REM batch syntax error - which this would otherwise hit on the very first run.
        if exist "%LOG%" for %%s in ("%LOG%") do if %%~zs GTR {LogRollBytes} move /y "%LOG%" "%LOG%.1" >nul 2>&1

        REM /o-d = newest first. The freshest tool processes every pending run, so a new
        REM manifest is never applied by an older binary. apply.cmd travels with the run and
        REM knows how that particular build starts - which is why this file never has to.
        for /f "delims=" %%d in ('dir /b /ad /o-d "%INCOMING%" 2^>nul') do (
          if exist "%INCOMING%\%%d\{Propagator.ToolFolderName}\{Propagator.LauncherCmd}" (
            echo. >> "%LOG%"
            echo ==== %DATE% %TIME% ==== >> "%LOG%"
            call "%INCOMING%\%%d\{Propagator.ToolFolderName}\{Propagator.LauncherCmd}" --apply "%INCOMING%" >> "%LOG%" 2>&1
            exit /b
          )
        )

        """;

    private static string BuildPollSh(AgentLayout layout) =>
        $"""
        #!/bin/sh
        # ---------------------------------------------------------------------------
        # DeployTool peer agent. Written once by `dytools-deploy install-agent`.
        #
        # Frozen on purpose: it finds the newest binary that arrived with a run and
        # hands it the whole incoming folder. The tool decides which runs are due.
        # ---------------------------------------------------------------------------
        set -eu

        INCOMING="{layout.Incoming}"
        LOG="{layout.LogFile}"
        LOCK="{layout.AgentDir}/poll.lock"

        # A deploy outlasts the poll interval, so a second run must not start on top of
        # one already going. mkdir is the portable atomic test-and-set.
        if ! mkdir "$LOCK" 2>/dev/null; then
          exit 0
        fi
        trap 'rmdir "$LOCK" 2>/dev/null || true' EXIT

        if [ -f "$LOG" ] && [ "$(wc -c < "$LOG")" -gt {LogRollBytes} ]; then
          mv -f "$LOG" "$LOG.1"
        fi

        [ -d "$INCOMING" ] || exit 0

        # Newest first: the freshest tool processes every pending run. apply.sh travels with
        # the run and knows how that build starts, so this file never has to.
        for dir in $(ls -1t "$INCOMING" 2>/dev/null); do
          tool="$INCOMING/$dir/{Propagator.ToolFolderName}/{Propagator.LauncherSh}"
          if [ -f "$tool" ]; then
            (
              echo
              echo "==== $(date) ===="
              # Invoked through sh rather than executed: a run folder written from a
              # Windows primary onto an SMB share carries no mode bits.
              sh "$tool" --apply "$INCOMING"
            ) >> "$LOG" 2>&1
            exit 0
          fi
        done

        """;

    // -- Closing summary -------------------------------------------------------

    /// <summary>
    /// Ends on everything the operator has to do next, with this box's real names already
    /// substituted in.
    ///
    /// Long on purpose. Standing a peer up is two-thirds a Windows permissions job, the
    /// permissions half happens in a different place from the config half, and the failure when
    /// it is wrong is a flat "access denied" hours later in a CI log nobody is watching. Saying
    /// it here, at the one moment someone is definitely looking at this box, is worth the lines.
    /// </summary>
    private static void PrintNextSteps(AgentLayout layout, bool scheduled, AgentOptions options)
    {
        var host      = HostIdentity.Resolve();
        // Empty only for a volume root ("C:\"), which nobody should be sharing wholesale -
        // fall back to a sane name rather than emitting `net share =C:\`.
        var shareName = Path.GetFileName(layout.Root) is { Length: > 0 } n ? n : "deploy";
        var unc       = OperatingSystem.IsWindows()
            ? $@"\\{host}\{shareName}\{AgentLayout.IncomingFolderName}"
            : layout.Incoming;

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine(scheduled
            ? "  ✓ This box will now poll for rollouts every minute."
            : "  ! Folders and poll script are ready - but nothing is scheduled to run them yet (see above).");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        if (!OperatingSystem.IsWindows())
        {
            PrintServersEntry(host, unc, options.AccountName, provisioned: false);
            Console.WriteLine($"\n  Watch it work:  {layout.LogFile}\n");
            return;
        }

        // Identity first. Granting comes second because you cannot grant to an account that
        // does not exist yet - net share fails with "no mapping between account names and
        // security IDs", which reads like a broken command rather than a missing step.
        PrintIdentityStep(host, unc, layout);

        // Offer to just do it. Everything in steps 1 and 2 is mechanical and happens on this
        // box; only the primary-side choice below genuinely needs a human.
        var provisioned = TryProvision(layout, shareName, options);

        if (!provisioned) PrintShareStep(layout, shareName);

        PrintServersEntry(host, unc, options.AccountName, provisioned);
        PrintChecklist(host, unc, layout, options.AccountName);
    }

    // -- Doing it, rather than printing it -------------------------------------

    /// <summary>
    /// Offers to create the account and grant it the share, and does it if asked. Returns
    /// whether the box is now actually set up, so the caller knows whether to print the manual
    /// commands instead.
    ///
    /// Interactive by design and never assumed: this creates a security principal and opens a
    /// share, on a production server, and doing that because someone ran an installer is not a
    /// reasonable default. Declining prints the commands and changes nothing.
    /// </summary>
    private static bool TryProvision(AgentLayout layout, string shareName, AgentOptions options)
    {
        if (!OperatingSystem.IsWindows()) return false;

        if (options.NoPrompt || Console.IsInputRedirected)
        {
            // Unattended: a CI run, or an operator who asked to be left alone. Say why the
            // offer is missing rather than silently skipping it.
            Console.WriteLine("\n  (running unattended - printing the commands instead)");
            return false;
        }

        Console.WriteLine();
        Console.Write($"  Create '{options.AccountName}' here and grant it the share now? [y/N] ");

        var answer = Console.ReadLine()?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)) return false;

        Console.WriteLine();

        if (!EnsureAccount(options.AccountName)) return false;
        if (!ShareFolder(layout, shareName, options.AccountName)) return false;

        Console.WriteLine();
        Console.WriteLine($"  ✓ This box is ready. {options.AccountName} can write to \\\\{HostIdentity.Resolve()}\\{shareName}.");
        return true;
    }

    /// <summary>
    /// Creates the account, or confirms an existing one is usable. An existing account is left
    /// exactly as it is - silently resetting the password of an account something else may be
    /// using is not a repair, it is an outage.
    /// </summary>
    private static bool EnsureAccount(string name)
    {
        if (LocalAccount.Exists(name))
        {
            Console.WriteLine($"  = Account '{name}' already exists - leaving its password alone.");
            Console.WriteLine("    (if you do not know it, reset it yourself: net user " +
                $"{name} * )");
            return true;
        }

        var password = LocalAccount.PromptForPassword($"Password for {name}");
        if (password is null) return false;

        var error = LocalAccount.Create(
            name, password, "DeployTool - receives rollouts from the primary. Share access only.");

        if (error is not null)
        {
            Console.WriteLine($"  ✗ {error}");
            return false;
        }

        Console.WriteLine($"  + Created local account '{name}' (unprivileged, password does not expire).");
        Console.WriteLine("    Keep that password - you will need it for whichever option you pick below.");
        return true;
    }

    /// <summary>
    /// Shares the agent root and grants the account on both gates. No secret is involved here,
    /// so these are ordinary process invocations.
    /// </summary>
    private static bool ShareFolder(AgentLayout layout, string shareName, string account)
    {
        var share = ProcessRunner.RunAsync(
            $"net share {shareName}", "net",
            $"share {shareName}=\"{layout.Root}\" /grant:\"{account}\",CHANGE").GetAwaiter().GetResult();

        // 2118 is NERR_DuplicateShare. Re-running the installer on a configured box is a normal
        // thing to do, and an existing share is a success, not a collision.
        var alreadyShared = !share.Success
            && (share.Stdout + share.Stderr).Contains("2118", StringComparison.Ordinal);

        if (!share.Success && !alreadyShared)
        {
            Console.WriteLine($"  ✗ Could not share {layout.Root}. {ElevationHint()}");
            return false;
        }

        Console.WriteLine(alreadyShared
            ? $"  = Share '{shareName}' already exists."
            : $"  + Shared {layout.Root} as '{shareName}', granted {account} Change.");

        var acl = ProcessRunner.RunAsync(
            "icacls grant", "icacls",
            $"\"{layout.Root}\" /grant \"{account}:(OI)(CI)M\"").GetAwaiter().GetResult();

        if (!acl.Success)
        {
            // The share permission alone is not enough - NTFS is the second, stricter gate, and
            // a half-granted folder fails later in a way that looks like the tool's fault.
            Console.WriteLine($"  ✗ Shared, but could not grant NTFS rights on {layout.Root}. {ElevationHint()}");
            return false;
        }

        Console.WriteLine($"  + Granted {account} Modify on {layout.Root} (and everything under it).");
        return true;
    }

    // -- Step 2: the share -----------------------------------------------------

    private static void PrintShareStep(AgentLayout layout, string shareName)
    {
        // icacls is quoted because these get pasted into PowerShell as often as cmd, and
        // PowerShell reads a bare (OI)(CI) as a subexpression to evaluate. Quoted works in both.
        Console.WriteLine($"""

          ── 2. Share this folder and grant that account ───────────────

          Here, elevated, once the account from step 1 exists:

              net share {shareName}={layout.Root} /grant:deploysvc,CHANGE
              icacls {layout.Root} /grant "deploysvc:(OI)(CI)M"

          Both are needed - the share permission and the NTFS permission are separate
          gates, and the stricter of the two wins. The quotes on icacls matter in
          PowerShell; without them it tries to evaluate (OI) as a command.

          Grant on {layout.Root}, NOT on {layout.Incoming}. The primary copies into
          {AgentLayout.StagingFolderName}\<runId> and then moves it across into
          {AgentLayout.IncomingFolderName}\<runId> - that move is what makes the handoff
          atomic - and it prunes old runs afterwards. It needs write and delete on both
          folders, which Modify on the root covers.
        """);
    }

    // -- Step 1: identity ------------------------------------------------------

    /// <summary>
    /// The part that actually bites. A self-hosted Actions runner installs as a service running
    /// as NETWORK SERVICE, which authenticates over the network as the machine account - a name
    /// a workgroup peer cannot resolve, so every copy fails no matter what step 1 granted.
    /// </summary>
    private static void PrintIdentityStep(string host, string unc, AgentLayout layout)
    {
        // On a workgroup box USERDOMAIN is the computer name. A heuristic, so it is phrased as
        // one - but it decides which of the two options is even relevant.
        var looksDomainJoined = !string.Equals(
            Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        Console.WriteLine("""

          ── 2. Give the primary an identity this box will accept ──────
        """);

        if (looksDomainJoined)
        {
            Console.WriteLine($"""

              This box appears to be domain-joined ({Environment.UserDomainName}), so this is
              easy: grant the primary's runner account in step 1 and you are done. If the runner
              runs as NETWORK SERVICE, its network identity is the machine account - grant
              DOMAIN\PRIMARYNAME$ rather than a user.

              If that is wrong and you are actually in a workgroup, use one of the options below.
              """);
        }

        Console.WriteLine($$"""

          A self-hosted Actions runner installs as a service running as NETWORK SERVICE.
          Over the network that authenticates as the MACHINE account - PRIMARY$ - which a
          workgroup peer has no way to resolve. Granting it in step 1 will not help; you
          need one of these.

          Option A - dedicated account here, credentials in deploy-config.json
                     (changes nothing about the runner - start here)

            On this box only, elevated. Read-Host keeps the password out of your
            shell history:

                New-LocalUser -Name deploysvc -PasswordNeverExpires \
                  -Password (Read-Host -AsSecureString "Password for deploysvc")

            It needs nothing beyond the share - no interactive logon, so not an
            administrator. Then add to its servers[] entry:

                "username": "{{host}}\\deploysvc",
                "password": "%DEPLOY_SHARE_PASSWORD%"

            Set DEPLOY_SHARE_PASSWORD from a CI secret - the tool refuses to run if the
            variable is missing, and warns every time if you paste the password literally.
            The account needs no rights beyond the share; it never logs on interactively.

          Option B - mirrored local account
                     (nothing in the config, but the runner must be reconfigured)

            Create the SAME username with the SAME password on BOTH boxes:

                New-LocalUser -Name deploysvc -PasswordNeverExpires \
                  -Password (Read-Host -AsSecureString "Password for deploysvc")

            Then on the primary, point the runner service at it:
            Services -> actions.runner.* -> Log On -> .\deploysvc -> restart.

            Workgroup pass-through does the rest: the peer validates the incoming
            credentials against its own SAM and lets it in. Step 2 grants it the share.
            Keeping the two passwords in step forever is the cost.
        """);
    }

    // -- Step 3: config --------------------------------------------------------

    private static void PrintServersEntry(string host, string unc, string account, bool provisioned)
    {
        // The share path is written with the machine name, which is right on a flat LAN and
        // wrong the moment the two boxes are in different VPC subnets - see the checklist.
        Console.WriteLine($$"""

          ── 3. Add it to servers[] in your deploy-config.json ─────────

              {
                "name": "{{host.ToLowerInvariant()}}",
                "hostname": "{{host}}",
                "incomingShare": "{{unc.Replace("\\", "\\\\")}}"
              }

          hostname is how this box recognises itself, and stays the computer name.
          incomingShare only has to be reachable FROM the primary.
        """);

        if (provisioned)
            Console.WriteLine($$"""
              For Option A, add the credential to that entry too:

                  "username": "{{host}}\{{account}}",
                  "password": "%DEPLOY_SHARE_PASSWORD%"

              and set DEPLOY_SHARE_PASSWORD from a CI secret in the workflow's env: block.
            """);
    }

    // -- Closing checklist -----------------------------------------------------

    private static void PrintChecklist(string host, string unc, AgentLayout layout, string account)
    {
        Console.WriteLine($"""

          ── Before the first rollout ──────────────────────────────────

            • TCP 445 must be open from the primary to this box. On EC2 or Azure that is a
              security-group / NSG rule, not just the Windows firewall.

            • '{host}' may not resolve from the primary. A Windows computer name only
              resolves by broadcast within one subnet, so across VPC subnets you want the
              private IP in incomingShare instead:  \\10.0.0.0\{Path.GetFileName(layout.Root)}\{AgentLayout.IncomingFolderName}

            • Verify AS THE RUNNER'S ACCOUNT, not your own login - that is the mistake that
              makes this look fixed when it is not:

                  psexec -u .\{account} -p "<password>" cmd /c "dir {unc}"

            • Then run 'dytools-deploy hostname --config deploy-config.json' on each box and
              confirm each one matches its own entry.

          Watch it work:  {layout.LogFile}

        """);
    }

}

/// <summary>Where everything an agent owns lives, derived from one root.</summary>
public sealed class AgentLayout
{
    public const string AgentFolderName    = "agent";
    public const string StagingFolderName  = "staging";
    public const string IncomingFolderName = "incoming";

    public AgentLayout(string root)
    {
        Root      = Path.TrimEndingDirectorySeparator(Path.GetFullPath(FileHelper.ExpandEnvVars(root)));
        AgentDir  = Path.Combine(Root, AgentFolderName);
        Staging   = Path.Combine(Root, StagingFolderName);
        Incoming  = Path.Combine(Root, IncomingFolderName);
        LogFile   = Path.Combine(AgentDir, "agent.log");
        PollScript = Path.Combine(AgentDir, OperatingSystem.IsWindows() ? "poll.cmd" : "poll.sh");
    }

    public string Root { get; }
    public string AgentDir { get; }

    /// <summary>Where the primary copies into. A sibling of Incoming so the handoff move is a rename.</summary>
    public string Staging { get; }

    /// <summary>The only path the poll script knows. Everything the agent acts on arrives here.</summary>
    public string Incoming { get; }

    public string LogFile { get; }
    public string PollScript { get; }

    public IEnumerable<string> All => [Root, AgentDir, Staging, Incoming];
}

/// <summary>Install-time switches. Every one has a working default.</summary>
public sealed class AgentOptions
{
    public string Root { get; init; } = AgentInstaller.DefaultRoot;
    public string TaskName { get; init; } = AgentInstaller.DefaultTaskName;
    public int IntervalMinutes { get; init; } = AgentInstaller.DefaultIntervalMinutes;

    /// <summary>
    /// SYSTEM by default because applying a run means controlling IIS and writing under
    /// Program Files - and because it needs no password.
    /// </summary>
    public string RunAsUser { get; init; } = "SYSTEM";

    /// <summary>
    /// Local account the primary will connect to this box's share as. Created on request during
    /// install; see AgentInstaller.TryProvisionAsync.
    /// </summary>
    public string AccountName { get; init; } = "deploysvc";

    /// <summary>
    /// Skips the offer to create and grant the account, and prints the commands instead.
    /// Set by --no-prompt, and implied whenever stdin is not a console.
    /// </summary>
    public bool NoPrompt { get; init; }
}
