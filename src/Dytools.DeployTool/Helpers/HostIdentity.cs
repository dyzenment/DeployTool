using System.Net;
using Dytools.DeployTool.Models.Config;

namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Decides which entry in <c>servers[]</c> the running box is.
///
/// This is the one question the whole fleet model rests on: get it wrong and a box either
/// silently deploys to nobody, or - worse - believes it is a different server and applies
/// that server's plan. So the answer is resolved in exactly one place, is overridable
/// without editing config, and is printable on demand (see the <c>hostname</c> command).
///
/// Resolution order, first non-blank wins:
///
///   1. <c>--server &lt;name&gt;</c>          explicit, per-run. Wins over everything.
///   2. <c>DEPLOYTOOL_SERVER</c>             per-machine, set once. For boxes whose machine
///                                           name is not what the fleet calls them - renamed
///                                           hosts, containers, images cloned from a template.
///   3. <c>Environment.MachineName</c>       the default, and the zero-configuration case.
///
/// Deliberately NOT DNS. <see cref="Environment.MachineName"/> is a local fact that cannot
/// fail, cannot hang, and cannot change because a resolver upstream changed. DNS names are
/// offered as *suggestions* by <see cref="Describe"/>, never used for matching.
/// </summary>
public static class HostIdentity
{
    /// <summary>Per-machine override. Set once on a box whose machine name is not its fleet name.</summary>
    public const string EnvVariable = "DEPLOYTOOL_SERVER";

    /// <summary>
    /// The name this run answers to. Never empty: machine name is the floor.
    /// </summary>
    public static string Resolve(string? cliOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(cliOverride))
            return cliOverride.Trim();

        var fromEnv = Environment.GetEnvironmentVariable(EnvVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        return Environment.MachineName;
    }

    /// <summary>Where <see cref="Resolve"/> got its answer, for the run header and diagnostics.</summary>
    public static string ResolveSource(string? cliOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(cliOverride)) return "--server";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVariable))) return EnvVariable;
        return "machine name";
    }

    // -- Matching --------------------------------------------------------------

    /// <summary>
    /// Finds the servers[] entry for <paramref name="identity"/>, in three passes of
    /// decreasing strictness. Passes are ordered rather than merged so that an exact
    /// hostname always beats a lenient short-name match somewhere else in the list.
    ///
    ///   1. <c>hostname</c> matches exactly
    ///   2. <c>name</c> matches exactly     - lets --server take the label you read in logs
    ///   3. short names match               - "WEB01" answers for "web01.corp.local", either way round
    ///
    /// Returns null when nothing matches; the caller decides what an unlisted box means.
    /// Throws when a single pass matches more than one entry, because guessing between two
    /// servers is how a box ends up applying the other one's plan.
    /// </summary>
    public static HostMatch? Find(IReadOnlyList<ServerConfig> servers, string identity)
    {
        if (servers.Count == 0 || string.IsNullOrWhiteSpace(identity)) return null;

        return Pass(servers, "hostname",   s => Eq(s.Hostname, identity))
            ?? Pass(servers, "name",       s => Eq(s.Name, identity))
            ?? Pass(servers, "short name", s => Eq(Short(s.Hostname), Short(identity))
                                             && !string.IsNullOrWhiteSpace(s.Hostname));
    }

    private static HostMatch? Pass(
        IReadOnlyList<ServerConfig> servers, string reason, Func<ServerConfig, bool> predicate)
    {
        var hits = servers.Where(predicate).ToList();

        if (hits.Count == 0) return null;

        if (hits.Count > 1)
            throw new DeployException(
                $"This host matches {hits.Count} entries in servers[] by {reason} " +
                $"({string.Join(", ", hits.Select(h => $"'{h.Name}'"))}). " +
                "Give each server a distinct hostname, or pin this box with --server <name> " +
                $"or the {EnvVariable} environment variable.");

        return new HostMatch(hits[0], reason);
    }

    /// <summary>Everything before the first dot: "web01.corp.local" and "WEB01" are the same box.</summary>
    private static string Short(string? value)
        => (value ?? string.Empty).Split('.', 2)[0];

    private static bool Eq(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a)
        && string.Equals(a.Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    // -- Diagnostics -----------------------------------------------------------

    /// <summary>
    /// Every name this box is known by, labelled with where it came from. Feeds the
    /// <c>hostname</c> command, whose whole job is to end the guesswork about what to put
    /// in <c>servers[].hostname</c>.
    /// </summary>
    public static List<(string Source, string Value)> Describe()
    {
        var names = new List<(string, string)>
        {
            ("machine name", Environment.MachineName)
        };

        // Both of these can differ from the machine name and from each other: DNS host name
        // is what the resolver stack thinks, and the FQDN adds the domain suffix. Neither is
        // used for matching - they are here so you can see the alternatives before choosing.
        TryAdd(names, "dns host name", Dns.GetHostName);
        TryAdd(names, "fqdn",          () => Dns.GetHostEntry(Dns.GetHostName()).HostName);

        var env = Environment.GetEnvironmentVariable(EnvVariable);
        names.Add(($"{EnvVariable} env var", string.IsNullOrWhiteSpace(env) ? "(not set)" : env.Trim()));

        return names;
    }

    /// <summary>
    /// A name lookup can hang on a box with a sick resolver, and this is a diagnostic - it
    /// must never be the reason a command fails. A failed probe reports itself and moves on.
    /// </summary>
    private static void TryAdd(List<(string, string)> into, string label, Func<string> probe)
    {
        try
        {
            var value = probe();
            if (!string.IsNullOrWhiteSpace(value)) into.Add((label, value));
        }
        catch (Exception ex)
        {
            into.Add((label, $"(lookup failed: {ex.Message})"));
        }
    }
}

/// <summary>The servers[] entry this box is, and which matching pass found it.</summary>
public sealed record HostMatch(ServerConfig Server, string Reason);
