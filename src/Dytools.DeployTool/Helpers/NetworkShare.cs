using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Authenticates the primary to a peer's share for the length of one handoff.
///
/// Needed because a workgroup has no shared identity. A self-hosted Actions runner installs as
/// a service running as NETWORK SERVICE, which authenticates over the network as the machine
/// account (WEB01$) - a name the peer has never heard of and, with no domain controller, never
/// will. Every copy fails with access denied no matter what the share grants.
///
/// The alternative is mirrored local accounts (same username and password on both boxes, runner
/// service reconfigured to log on as it). This class exists so that is a choice rather than a
/// requirement: point the config at a credential and the tool establishes the session itself.
///
/// Scoped to a `using`. The connection is deviceless - no drive letter - so nothing is left
/// mapped, and concurrent runs on different peers do not fight over letters.
///
/// The password is never logged, never written to a report, and never reaches a manifest.
/// </summary>
public sealed class NetworkShare : IDisposable
{
    private const int ResourceTypeDisk = 0x00000001;

    private const int ErrorAccessDenied              = 5;
    private const int ErrorBadNetPath                = 53;
    private const int ErrorBadNetName                = 67;
    private const int ErrorInvalidPassword           = 86;
    private const int ErrorNoNetOrBadPath            = 1203;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorLogonFailure              = 1326;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string RemoteName;
        public string? Comment;
        public string? Provider;
    }

    // Both return a Win32 error code directly rather than through GetLastError.
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(
        ref NetResource netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    private NetworkShare(string root, string username)
    {
        Root     = root;
        Username = username;
    }

    /// <summary>The share the session was opened against, e.g. <c>\\WEB02\deploy</c>.</summary>
    public string Root { get; }

    /// <summary>Who we authenticated as. Safe to log - this is the username, never the password.</summary>
    public string Username { get; }

    /// <summary>
    /// Opens an authenticated session to the share containing <paramref name="uncPath"/>, or
    /// returns null when none is needed or possible - no username configured, a local path, or
    /// a non-Windows primary. A null return means "carry on with the identity you already have",
    /// which is the single-identity case the tool has always supported.
    ///
    /// Takes the raw config values, not expanded ones, so it can tell a %VAR% reference from a
    /// password someone pasted into a file they are about to commit.
    /// </summary>
    public static NetworkShare? Connect(string uncPath, string? rawUsername, string? rawPassword)
    {
        if (string.IsNullOrWhiteSpace(rawUsername)) return null;

        var root = ShareRootOf(uncPath);

        if (root is null)
        {
            Console.WriteLine(
                $"    ! '{uncPath}' is not a UNC share path, so the configured username is " +
                "ignored. Credentials only apply to \\\\server\\share paths.");
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            // The Win32 connection API has no counterpart here. A Linux primary reaches a peer
            // through a CIFS mount, and the mount already carries the credentials.
            Console.WriteLine(
                "    ! Share credentials are a Windows facility - ignored on this platform. " +
                "Mount the share with its credentials before running the deploy.");
            return null;
        }

        var username = Qualify(rawUsername.Trim(), root);
        var password = ResolvePassword(rawPassword);

        var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = root };
        var code     = WNetAddConnection2(ref resource, password, username, 0);

        // Windows permits one credential set per server per logon session. An existing session -
        // opened by the runner, by Explorer, or by an earlier peer in this same run - collides
        // with ours. Drop it and retry; we are the ones who need it now.
        if (code == ErrorSessionCredentialConflict)
        {
            WNetCancelConnection2(root, 0, true);
            code = WNetAddConnection2(ref resource, password, username, 0);
        }

        if (code != 0)
            throw new DeployException(Explain(code, root, username));

        return new NetworkShare(root, username);
    }

    public void Dispose()
    {
        // Best effort, and not forced: an open handle here means something is still reading the
        // share, and tearing that out would be worse than leaving a session behind for the
        // few seconds until the process exits.
        try { WNetCancelConnection2(Root, 0, false); } catch { /* nothing left to do about it */ }
    }

    // -- Helpers ---------------------------------------------------------------

    /// <summary>
    /// <c>\\WEB02\deploy\incoming</c> → <c>\\WEB02\deploy</c>. The connection API authenticates
    /// against a share, not a folder inside one; passing the deeper path fails.
    /// </summary>
    public static string? ShareRootOf(string uncPath)
    {
        var path = (uncPath ?? string.Empty).Replace('/', '\\').TrimEnd('\\');

        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return null;

        var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : $@"\\{parts[0]}\{parts[1]}";
    }

    /// <summary>
    /// Qualifies a bare username with the peer's name, so "deploysvc" reaches the peer's local
    /// account rather than being offered with the primary's own workgroup as its domain.
    ///
    /// Not done when the share is addressed by IP: "10.0.1.20\deploysvc" is not a domain Windows
    /// will accept, and a bare name is what works there. An already-qualified name
    /// (<c>PEER\user</c>, <c>user@domain</c>) is left exactly as written.
    /// </summary>
    public static string Qualify(string username, string shareRoot)
    {
        if (username.Contains('\\') || username.Contains('@')) return username;

        var host = shareRoot[2..].Split('\\')[0];
        return IPAddress.TryParse(host, out _) ? username : $@"{host}\{username}";
    }

    /// <summary>
    /// Expands the configured password. Public because it is the validation seam: the checks
    /// below are platform-independent, while <see cref="Connect"/> declines on a non-Windows
    /// primary before it ever gets here.
    ///
    /// Refuses the two ways this quietly goes wrong: a
    /// literal secret sitting in a file headed for git, and an environment reference whose
    /// variable was never set - which would otherwise be offered to the peer verbatim and come
    /// back as an unexplained logon failure.
    /// </summary>
    public static string ResolvePassword(string? rawPassword)
    {
        if (string.IsNullOrEmpty(rawPassword)) return string.Empty;

        var isReference = LooksLikeEnvReference(rawPassword);

        if (!isReference)
            Console.WriteLine(
                "    ! The peer password is written literally in deploy-config.json. Use " +
                "\"%DEPLOY_SHARE_PASSWORD%\" and set it from a secret instead - this file is " +
                "in your repository.");

        var expanded = FileHelper.ExpandEnvVars(rawPassword);

        if (isReference && LooksLikeEnvReference(expanded))
            throw new DeployException(
                $"The peer password references an environment variable that is not set ({rawPassword}). " +
                "Set it on the runner, or pass it through the workflow's env: block.");

        return expanded;
    }

    private static bool LooksLikeEnvReference(string value)
        => Regex.IsMatch(value, @"%[A-Z_][A-Z0-9_]*%|\$\{?[A-Z_][A-Z0-9_]*\}?",
               RegexOptions.IgnoreCase);

    /// <summary>
    /// Turns a Win32 code into the thing to go and change. These failures land in a CI log that
    /// nobody is watching live, so "error 1326" is worth spelling out once here.
    /// </summary>
    private static string Explain(int code, string root, string username) => code switch
    {
        ErrorLogonFailure or ErrorInvalidPassword =>
            $"Could not authenticate to {root} as '{username}': the username or password was " +
            "rejected. For a workgroup peer this must be a local account ON THAT BOX - write it " +
            "as PEERNAME\\user, or bare when the share is addressed by IP.",

        ErrorAccessDenied =>
            $"Authenticated to {root} as '{username}', but that account is not allowed to write " +
            "there. Grant it Change on the share and Modify on the folder - on the share root, " +
            "not just incoming: the handoff stages beside incoming and moves across.",

        ErrorBadNetPath or ErrorNoNetOrBadPath =>
            $"Cannot reach {root}. Check the peer is up, that TCP 445 is open to it (on EC2 that " +
            "is a security-group rule), and that the name resolves - a Windows computer name " +
            "will not resolve across VPC subnets, so use the private IP.",

        ErrorBadNetName =>
            $"{root} does not exist on that host. Create it with: " +
            @"net share deploy=C:\deploy /grant:<user>,CHANGE",

        ErrorSessionCredentialConflict =>
            $"Another session to that server is already open with different credentials, and " +
            $"dropping it did not help. Run `net use` on the primary to see it, `net use " +
            $"{root} /delete` to clear it.",

        _ => $"Could not connect to {root} as '{username}': {new Win32Exception(code).Message} " +
             $"(win32 {code})."
    };
}
