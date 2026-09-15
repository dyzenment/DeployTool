using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Reporting;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// The classifier decides which inline failures are handed to the agent when
/// applyViaAgent is null. A false positive hands a real failure to SYSTEM to fail again;
/// a false negative leaves a fixable one unfixed. Both directions are pinned here.
/// </summary>
[TestClass]
public sealed class AccessDeniedTests
{
    private static TargetDeployResult Failed(string? error = null, params StepResult[] steps)
    {
        var r = new TargetDeployResult { Success = false, ErrorMessage = error };
        r.DeploySteps.AddRange(steps);
        return r;
    }

    private static StepResult Step(int exit, string stderr = "", string stdout = "") => new()
    {
        StepName = "x", ExitCode = exit, Success = exit == 0, Stderr = stderr, Stdout = stdout
    };

    // -- Single step, as the IIS probe asks it ---------------------------------
    //
    // IisHandler runs one read-only `appcmd list apppool` before anything is stopped or drained,
    // and aborts the target on a positive denial so it goes straight to the agent. The rule has
    // to be the same one used afterwards: a probe that said "denied" where Looks(result) would
    // not would abort a target and then fail to hand it off, which is worse than not probing.

    [TestMethod]
    public void Probe_RedirectionConfigError_Blocks()
    {
        // The field failure, as the probe would see it: the read itself is what is refused, so
        // it shows up identically whether the command was `list` or `stop`.
        var probe = Step(1168, stdout: "ERROR ( message:Configuration error\n\nFilename: redirection.config\n" +
                                       "Description: Cannot read configuration file due to insufficient permissions\n. )");

        Assert.IsTrue(AccessDenied.Looks(probe));
    }

    [TestMethod]
    public void Probe_SucceedingIsNeverBlocked()
        => Assert.IsFalse(AccessDenied.Looks(Step(0, stdout: "Started")));

    [TestMethod]
    public void Probe_UnknownAppPool_DoesNotBlock()
    {
        // appcmd exits 1168 with nothing on stdout for a pool that is not there. Reading that as
        // denied would abort before the real step could report the actual problem.
        Assert.IsFalse(AccessDenied.Looks(Step(1168)),
            "an inconclusive probe must fall through to the real steps, not guess");
    }

    [TestMethod]
    public void Probe_MissingAppcmd_DoesNotBlock()
        => Assert.IsFalse(AccessDenied.Looks(Step(-1, stderr: "Executable not found: appcmd.exe")),
            "a broken probe is not evidence about permissions");

    [TestMethod]
    public void Probe_ExitFiveBlocks()
        => Assert.IsTrue(AccessDenied.Looks(Step(5, stderr: "Access is denied.")));

    [TestMethod]
    public void ProbeAbortMessage_IsClassifiedAsAccessDenied()
    {
        // What IisHandler returns when the probe blocks. It carries no failing appcmd step of its
        // own beyond the probe, so the ErrorMessage has to be enough on its own for Program.cs to
        // route it to the agent.
        var aborted = Failed(
            "Cannot control app pool 'ShipSystem' as NT AUTHORITY\\NETWORK SERVICE - access is denied. " +
            "Nothing was stopped, drained or copied.");

        Assert.IsTrue(AccessDenied.Looks(aborted),
            "an early abort must still be handed to the agent, or the probe would turn a " +
            "recoverable deploy into a hard failure");
    }

    [TestMethod]
    public void SuccessfulTarget_IsNeverAccessDenied()
    {
        var r = new TargetDeployResult { Success = true };
        r.DeploySteps.Add(Step(5, "Access is denied."));
        Assert.IsFalse(AccessDenied.Looks(r));
    }

    [TestMethod]
    public void AppcmdRedirectionConfig_IsAccessDenied()
    {
        // The exact shape seen in the field: exit 1168 with the config error on stdout.
        var r = Failed("Failed to stop app pool 'ShipSystem'.",
            Step(1168, stdout: "ERROR ( message:Configuration error\n\nFilename: redirection.config\n" +
                               "Description: Cannot read configuration file due to insufficient permissions\n. )"));
        Assert.IsTrue(AccessDenied.Looks(r));
    }

    [TestMethod]
    public void Exit1168Alone_IsNotAccessDenied()
    {
        // ERROR_NOT_FOUND - also what appcmd returns for a pool that does not exist.
        Assert.IsFalse(AccessDenied.Looks(Failed("Failed to stop app pool.",
            Step(1168, stdout: "ERROR ( message:Cannot find APPPOOL object with identifier \"Nope\". )"))));
    }

    [TestMethod]
    public void ScAccessDenied_IsAccessDenied()
    {
        Assert.IsTrue(AccessDenied.Looks(Failed("Failed to stop service.",
            Step(5, stdout: "[SC] OpenService FAILED 5:\n\nAccess is denied."))));
    }

    [TestMethod]
    public void Exit5Alone_IsAccessDenied() => Assert.IsTrue(AccessDenied.Looks(Failed(null, Step(5))));

    [TestMethod]
    public void ElevationRequired_IsAccessDenied() => Assert.IsTrue(AccessDenied.Looks(Failed(null, Step(740))));

    [TestMethod]
    public void FolderMirrorDenied_IsAccessDenied()
    {
        // A folder target with no service: the mirror throws UnauthorizedAccessException.
        Assert.IsTrue(AccessDenied.Looks(Failed(@"Access to the path 'C:\inetpub\Web\app.dll' is denied.")));
    }

    [TestMethod]
    public void UnixPermissionDenied_IsAccessDenied()
    {
        Assert.IsTrue(AccessDenied.Looks(Failed(null, Step(1, stderr: "Failed to stop myproc.service: Permission denied"))));
    }

    [TestMethod]
    public void WarmupFailure_IsNotAccessDenied()
    {
        Assert.IsFalse(AccessDenied.Looks(Failed("Warm-up returned 503 from http://localhost/health.", Step(1, stderr: "HTTP 503"))));
    }

    [TestMethod]
    public void OnlySuccessfulStepsMentioningDenied_IsNotAccessDenied()
    {
        var r = Failed("Warm-up timed out.", Step(0, stdout: "note: access is denied for anonymous users, as configured"));
        Assert.IsFalse(AccessDenied.Looks(r));
    }
}
