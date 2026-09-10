using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dytools.DeployTool.Helpers;

namespace Dytools.DeployTool.Services;

/// <summary>
/// Proves every peer is reachable and writable before the build starts.
///
/// Propagation is the last thing a run does, so without this a fleet whose share is
/// unreachable - an unresolvable name, a closed security group, a password that never made it
/// out of CI - burns the entire build and test cycle before saying so. The facts it checks are
/// all knowable in about a second, and none of them depend on anything the build produces.
///
/// Four checks, in the order the failures actually happen, because each one makes the next
/// meaningless: resolve the host, reach tcp/445, authenticate, write. Reporting only the last
/// failure is what produced "the share does not exist" for a share that existed and a host that
/// did not resolve.
/// </summary>
public static class PeerPrecheck
{
    /// <summary>Long enough for a working VPC hop, short enough that a blocked port is not a wait.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    private const int SmbPort = 445;

    /// <summary>
    /// Checks each peer and prints what it found. Returns true when every peer is ready.
    /// Never throws: a precheck that fails is a result, not an exception.
    /// </summary>
    public static async Task<bool> RunAsync(IReadOnlyList<PeerTarget> peers)
    {
        if (peers.Count == 0) return true;

        var allOk = true;

        foreach (var peer in peers)
        {
            Console.WriteLine($"\n  → {peer.ServerName}  {peer.IncomingShare ?? "(no incomingShare)"}");
            allOk &= await CheckAsync(peer);
        }

        return allOk;
    }

    private static async Task<bool> CheckAsync(PeerTarget peer)
    {
        if (string.IsNullOrWhiteSpace(peer.IncomingShare))
            return Fail("no incomingShare configured",
                "Set it to the UNC path of that box's incoming folder. `install-agent` on the " +
                "peer prints the entry to paste.");

        var incoming = FileHelper.ExpandEnvVars(peer.IncomingShare).TrimEnd('\\', '/');

        // A local path is a legitimate test setup and has no host, port or credential to check.
        if (!incoming.StartsWith(@"\\", StringComparison.Ordinal) &&
            !incoming.StartsWith("//", StringComparison.Ordinal))
            return CheckPaths(incoming);

        var host = NetworkShare.ShareRootOf(incoming)?[2..].Split('\\')[0];

        if (host is null)
            return Fail($"'{incoming}' names a server but no share",
                @"Point it at a subfolder of a share (\\PEER\deploy\incoming), not the server root.");

        return await ResolveAsync(host)
            && await ReachableAsync(host)
            && Authenticate(incoming, peer)
            && CheckPaths(incoming);
    }

    // -- 1. Name resolution ----------------------------------------------------

    private static async Task<bool> ResolveAsync(string host)
    {
        if (IPAddress.TryParse(host, out _))
        {
            Ok($"{host} is an address - no name resolution needed");
            return true;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host).WaitAsync(ConnectTimeout);

            if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);

            Ok($"{host} resolves to {string.Join(", ", addresses.Select(a => a.ToString()))}");
            return true;
        }
        catch (Exception)
        {
            // The single most common cause on a cloud fleet, and the one whose SMB error code
            // (67) reads as a missing share instead.
            return Fail($"'{host}' does not resolve from this box",
                "A Windows computer name resolves by NetBIOS broadcast, and a VPC carries no " +
                "broadcast traffic - so an instance name will not resolve between instances. " +
                "Put that peer's PRIVATE IP in incomingShare instead (hostname stays the " +
                "computer name; the two are independent).");
        }
    }

    // -- 2. Port ---------------------------------------------------------------

    private static async Task<bool> ReachableAsync(string host)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, SmbPort).WaitAsync(ConnectTimeout);

            Ok($"tcp/{SmbPort} open ({stopwatch.ElapsedMilliseconds} ms)");
            return true;
        }
        catch (Exception)
        {
            return Fail($"cannot reach {host} on tcp/{SmbPort}",
                "On EC2 or Azure this is a security-group / NSG rule, not the Windows firewall - " +
                $"the peer's group needs inbound tcp/{SmbPort} from this box. Check it directly " +
                $"with:  Test-NetConnection {host} -Port {SmbPort}");
        }
    }

    // -- 3. Credentials --------------------------------------------------------

    private static bool Authenticate(string incoming, PeerTarget peer)
    {
        try
        {
            using var share = NetworkShare.Connect(incoming, peer.Username, peer.Password);

            Ok(share is null
                ? "connecting as this process's own identity (no credentials configured)"
                : $"authenticated to {share.Root} as {share.Username}");

            // Deliberately inside the using: the paths below are only reachable while the
            // session is open, which is exactly the condition propagation will run under.
            return true;
        }
        catch (Exception ex)
        {
            return Fail("could not authenticate", ex.Message);
        }
    }

    // -- 4. Paths and write access ---------------------------------------------

    /// <summary>
    /// The check that actually matters. Everything above can pass while the account still has
    /// no write access, which is the difference between a share permission and an NTFS one.
    /// </summary>
    private static bool CheckPaths(string incoming)
    {
        var deployRoot = Path.GetDirectoryName(incoming);
        var staging    = deployRoot is null ? null : Path.Combine(deployRoot, AgentLayout.StagingFolderName);

        if (!Directory.Exists(incoming))
            return Fail($"{incoming} does not exist",
                "Run `dytools-deploy install-agent` on that box - it creates the layout, the " +
                "poll script and the scheduled task.");

        if (staging is null || !Directory.Exists(staging))
            return Fail($"{staging ?? "(staging)"} does not exist",
                "Propagation stages beside incoming and moves across, which is what makes the " +
                "handoff atomic. `install-agent` creates both.");

        // A probe directory rather than a file: propagation creates and moves directories, and
        // on some filers those are governed differently.
        var probe = Path.Combine(staging, $".precheck-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(probe);
            Directory.Delete(probe);
        }
        catch (Exception ex)
        {
            return Fail($"cannot write to {staging}",
                "Both gates have to allow it - the SMB share permission AND the NTFS permission, " +
                $"granted on the share root rather than just incoming. ({ex.Message})");
        }

        Ok("incoming and staging exist and are writable");
        return true;
    }

    // -- Output ----------------------------------------------------------------

    private static void Ok(string message) => Console.WriteLine($"      ✓ {message}");

    private static bool Fail(string what, string fix)
    {
        Console.WriteLine($"      ✗ {what}");

        foreach (var line in Wrap(fix, 72))
            Console.WriteLine($"        {line}");

        return false;
    }

    /// <summary>
    /// Wraps remediation text so a long explanation stays readable in a CI log, which does no
    /// wrapping of its own.
    /// </summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) yield return line.ToString();
    }
}

/// <summary>
/// One peer to check. Deliberately not a ServerPlan: the same check runs from a deploy (which
/// has a plan) and from `test-peers` (which has only config, and no reason to build one).
/// </summary>
public sealed record PeerTarget(
    string ServerName,
    string? IncomingShare,
    string? Username,
    string? Password);
