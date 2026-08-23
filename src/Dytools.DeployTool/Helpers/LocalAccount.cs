using System.Runtime.InteropServices;

namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Creates the local account a peer's share is granted to.
///
/// Through <c>NetUserAdd</c> rather than <c>net user &lt;name&gt; &lt;password&gt; /add</c>, and
/// that is the whole reason this file exists: a password on a command line is readable by any
/// user on the box for as long as the process lives (<c>Get-CimInstance Win32_Process</c>), and
/// it lands in command-line auditing and transcript logs if either is switched on. The API takes
/// it in a struct, so it never becomes an argument.
///
/// The account created is deliberately unprivileged - USER_PRIV_USER, no group membership beyond
/// the default Users. It exists to be granted a share and nothing else, and it never logs on
/// interactively.
/// </summary>
public static class LocalAccount
{
    private const uint Level1 = 1;

    private const uint UserPrivUser = 1;

    private const uint UfScript             = 0x0001;
    private const uint UfNormalAccount      = 0x0200;
    private const uint UfDontExpirePassword = 0x10000;

    private const uint NerrSuccess          = 0;
    private const uint ErrorAccessDenied    = 5;
    private const uint NerrUserExists       = 2224;
    private const uint NerrPasswordTooShort = 2245;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Password;
        public uint PasswordAge;
        public uint Priv;
        [MarshalAs(UnmanagedType.LPWStr)] public string? HomeDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ScriptPath;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint NetUserAdd(
        string? serverName, uint level, ref UserInfo1 buf, out uint parmError);

    /// <summary>
    /// Whether an account of this name already exists on this machine.
    ///
    /// Runs `net user` directly rather than through ProcessRunner: this is a silent probe whose
    /// negative answer is the normal case, and ProcessRunner would announce the command and dump
    /// PATH diagnostics on a miss - noise that reads like a fault.
    /// </summary>
    public static bool Exists(string name)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "net",
                ArgumentList           = { "user", name },
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            });

            if (probe is null) return false;

            probe.WaitForExit();
            return probe.ExitCode == 0;
        }
        catch
        {
            // No `net`, or it would not start. Treat as absent and let the create attempt be
            // the thing that reports a real problem.
            return false;
        }
    }

    /// <summary>
    /// Creates a local, unprivileged account whose password does not expire. Returns null on
    /// success, otherwise a message naming what to fix.
    /// </summary>
    public static string? Create(string name, string password, string comment)
    {
        if (!OperatingSystem.IsWindows())
            return "Local accounts can only be created on Windows.";

        var info = new UserInfo1
        {
            Name     = name,
            Password = password,
            Priv     = UserPrivUser,
            Comment  = comment,
            // UF_SCRIPT is required by the API whether or not a logon script exists. Password
            // expiry is off because nothing will be around to notice the account locking out
            // at 3am, six months from now, mid-rollout.
            Flags    = UfScript | UfNormalAccount | UfDontExpirePassword
        };

        var code = NetUserAdd(null, Level1, ref info, out _);

        return code switch
        {
            NerrSuccess          => null,
            NerrUserExists       => $"An account named '{name}' already exists on this machine.",
            NerrPasswordTooShort =>
                "The password does not meet this machine's policy (length, complexity, or " +
                "history). Check it with: net accounts",
            ErrorAccessDenied    =>
                "Access denied. Creating a local account needs an elevated prompt - " +
                "start PowerShell or cmd with 'Run as administrator'.",
            _                    => $"NetUserAdd failed with code {code}."
        };
    }

    /// <summary>
    /// Reads a password from the console without echoing it, twice, and only returns it when
    /// both entries match. Null means the operator gave up - not an error.
    ///
    /// Returns a plain string: the P/Invoke needs one, so a SecureString would have to be
    /// unprotected at the call site anyway - and SecureString is no longer recommended precisely
    /// because it cannot deliver on that promise. The value is used once and never stored.
    /// </summary>
    public static string? PromptForPassword(string label)
    {
        // Piped or redirected input cannot be read without echo, and a CI run has nobody to
        // type at it. Better to decline than to read a secret off a pipe.
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("    ! No interactive console - cannot prompt for a password.");
            return null;
        }

        while (true)
        {
            Console.Write($"    {label}: ");
            var first = ReadHidden();

            if (string.IsNullOrEmpty(first))
            {
                Console.WriteLine("    Cancelled.");
                return null;
            }

            Console.Write("    Confirm: ");
            var second = ReadHidden();

            if (string.Equals(first, second, StringComparison.Ordinal)) return first;

            Console.WriteLine("    Those do not match - try again, or press Enter to cancel.");
        }
    }

    private static string ReadHidden()
    {
        var buffer = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();

                case ConsoleKey.Backspace when buffer.Length > 0:
                    buffer.Length--;
                    continue;

                case ConsoleKey.Backspace:
                    continue;

                case ConsoleKey.Escape:
                    Console.WriteLine();
                    return string.Empty;
            }

            // Control characters would otherwise land in the password as unprintable junk the
            // operator cannot see and will never reproduce when typing it on the other box.
            if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
    }
}
