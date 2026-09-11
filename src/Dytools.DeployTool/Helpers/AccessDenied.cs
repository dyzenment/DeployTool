using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployTool.Helpers;

/// <summary>
/// Recognises a target that failed because the account applying it lacked rights, as
/// opposed to failing because the deploy itself was wrong. Only the former is handed to
/// the agent (rollout.applyViaAgent = null); the latter would fail again as SYSTEM.
///
/// Conservative on purpose. Exit code 5 (ERROR_ACCESS_DENIED) and 740 (elevation required)
/// are unambiguous. appcmd's 1168 is not - it is ERROR_NOT_FOUND, which appcmd also returns
/// for a pool that does not exist - so that one needs the message to agree.
/// </summary>
public static class AccessDenied
{
    private static readonly string[] Phrases =
    [
        "access is denied",
        "access to the path",          // UnauthorizedAccessException from File/Directory APIs
        "insufficient permissions",     // appcmd reading redirection.config as a non-admin
        "unauthorizedaccess",
        "requires elevation",
        "permission denied",            // sc / systemctl on a Unix box
        "operation is not permitted"
    ];

    public static bool Looks(TargetDeployResult result)
    {
        if (result.Success) return false;
        if (Matches(result.ErrorMessage)) return true;

        return result.DeploySteps.Any(step =>
            !step.Success &&
            (step.ExitCode is 5 or 740 || Matches(step.Stderr) || Matches(step.Stdout)));
    }

    public static bool Matches(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var phrase in Phrases)
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>"CORP\\svc-runner" - who the failing attempt ran as, for the message.</summary>
    public static string CurrentIdentity()
    {
        var domain = Environment.UserDomainName;
        var user   = Environment.UserName;
        return string.IsNullOrEmpty(domain) || domain.Equals(user, StringComparison.OrdinalIgnoreCase)
            ? user
            : $"{domain}\\{user}";
    }
}
