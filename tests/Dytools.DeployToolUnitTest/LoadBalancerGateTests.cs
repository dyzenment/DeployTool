using System.Net;
using Dytools.DeployTool.Models.Config;
using Dytools.DeployTool.Models.Reporting;
using Dytools.DeployTool.Services;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers the drain/restore gate that brackets an IIS deploy on a load-balanced box.
///
/// The restore decision gets the most attention here because it is the one that can strand an
/// instance out of rotation or - worse - put a half-written folder back into it. The HTTP parts
/// are exercised against a real loopback listener rather than mocked, since what is being
/// asserted is status-code handling and polling behaviour, which a mock would just restate.
/// </summary>
[TestClass]
public sealed class LoadBalancerGateTests
{
    // -- Restore policy --------------------------------------------------------

    [TestMethod]
    public void SuccessfulDeploy_Restores()
    {
        var result = new TargetDeployResult { Success = true };

        Assert.IsTrue(LoadBalancerGate.ShouldRestore(result, liveModified: true));
    }

    [TestMethod]
    public void FailedDeploy_ThatRolledBack_Restores()
    {
        var result = new TargetDeployResult { Success = false, RolledBack = true };

        Assert.IsTrue(LoadBalancerGate.ShouldRestore(result, liveModified: true),
            "a restored previous build is known-good, so the instance can serve again");
    }

    [TestMethod]
    public void FailedDeploy_ThatNeverTouchedLive_Restores()
    {
        var result = new TargetDeployResult { Success = false, RolledBack = false };

        Assert.IsTrue(LoadBalancerGate.ShouldRestore(result, liveModified: false),
            "blue-green that failed before the flip, or classic that died before the mirror, " +
            "left the previous build serving untouched");
    }

    [TestMethod]
    public void FailedDeploy_WithLiveModifiedAndNoRollback_StaysDrained()
    {
        var result = new TargetDeployResult { Success = false, RolledBack = false };

        Assert.IsFalse(LoadBalancerGate.ShouldRestore(result, liveModified: true),
            "an interrupted mirror leaves neither the old build nor the new one - that must " +
            "not go back into rotation");
    }

    // -- Phase composition -----------------------------------------------------

    [TestMethod]
    public async Task NullPhase_IsANoOpThatSucceeds()
    {
        var steps = new List<StepResult>();

        Assert.IsTrue(await LoadBalancerGate.RunPhaseAsync(null, "drain", Tokens, steps));
        Assert.AreEqual(0, steps.Count);
    }

    [TestMethod]
    public async Task PhaseWithNothingConfigured_DoesNothing()
    {
        var steps = new List<StepResult>();

        Assert.IsTrue(await LoadBalancerGate.RunPhaseAsync(new LoadBalancerPhase(), "drain", Tokens, steps));
        Assert.AreEqual(0, steps.Count, "no notify, no verify, no wait means no work");
    }

    [TestMethod]
    public async Task WaitOnlyPhase_JustWaits()
    {
        var steps = new List<StepResult>();

        Assert.IsTrue(await LoadBalancerGate.RunPhaseAsync(
            new LoadBalancerPhase { WaitSeconds = 1 }, "drain", Tokens, steps));

        Assert.AreEqual(1, steps.Count);
        Assert.AreEqual("drain wait", steps[0].StepName);
        Assert.IsTrue(steps[0].Success);
    }

    [TestMethod]
    public async Task VerifyIsSkipped_WhenNoExpectedStatusIsGiven()
    {
        // "if i dont provide a status we will assume all ok" - the URL alone must not start
        // a poll, or an unreachable box would block every deploy.
        var steps = new List<StepResult>();
        var phase = new LoadBalancerPhase { VerifyUrl = "http://127.0.0.1:1/never" };

        Assert.IsTrue(await LoadBalancerGate.RunPhaseAsync(phase, "drain", Tokens, steps));
        Assert.AreEqual(0, steps.Count);
    }

    // -- Notify ----------------------------------------------------------------

    [TestMethod]
    public async Task HttpNotify_SucceedsOnAnyTwoHundred_WhenNoStatusExpected()
    {
        using var server = new StubServer(HttpStatusCode.Accepted);
        var steps = new List<StepResult>();

        var phase = new LoadBalancerPhase { Notify = new NotifyHook { Url = server.Url } };

        Assert.IsTrue(await LoadBalancerGate.RunPhaseAsync(phase, "drain", Tokens, steps));
        Assert.IsTrue(steps.Single().Success);
    }

    [TestMethod]
    public async Task HttpNotify_FailingStopsThePhase()
    {
        using var server = new StubServer(HttpStatusCode.InternalServerError);
        var steps = new List<StepResult>();

        var phase = new LoadBalancerPhase
        {
            Notify      = new NotifyHook { Url = server.Url },
            WaitSeconds = 30
        };

        Assert.IsFalse(await LoadBalancerGate.RunPhaseAsync(phase, "drain", Tokens, steps));
        Assert.AreEqual(1, steps.Count, "a failed drain must not go on to wait, let alone stop the site");
    }

    [TestMethod]
    public async Task HttpNotify_HonoursAnExplicitExpectedStatus()
    {
        using var server = new StubServer(HttpStatusCode.NoContent);
        var steps = new List<StepResult>();

        var phase = new LoadBalancerPhase
        {
            Notify = new NotifyHook { Url = server.Url, ExpectStatus = 204 }
        };

        Assert.IsTrue(await LoadBalancerGate.RunPhaseAsync(phase, "drain", Tokens, steps));
    }

    [TestMethod]
    public async Task HttpNotify_UnreachableHostFailsRatherThanThrows()
    {
        var steps = new List<StepResult>();
        var phase = new LoadBalancerPhase
        {
            Notify = new NotifyHook { Url = "http://127.0.0.1:1/drain", TimeoutSeconds = 2 }
        };

        Assert.IsFalse(await LoadBalancerGate.RunPhaseAsync(phase, "drain", Tokens, steps));
        Assert.IsFalse(steps.Single().Success);
    }

    [TestMethod]
    public async Task FileNotify_CreatesAndDeletesTheMarker()
    {
        var path  = Path.Combine(Path.GetTempPath(), $"dytools-lb-{Guid.NewGuid():N}.txt");
        var steps = new List<StepResult>();

        var create = new LoadBalancerPhase
        {
            Notify = new NotifyHook { Type = NotifyHookType.File, Path = path, Action = FileHookAction.Create }
        };
        Assert.IsTrue(await LoadBalancerGate.RunPhaseAsync(create, "drain", Tokens, steps));
        Assert.IsTrue(File.Exists(path));

        var delete = new LoadBalancerPhase
        {
            Notify = new NotifyHook { Type = NotifyHookType.File, Path = path, Action = FileHookAction.Delete }
        };
        Assert.IsTrue(await LoadBalancerGate.RunPhaseAsync(delete, "restore", Tokens, steps));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task FileNotify_DeletingAnAbsentMarkerSucceeds()
    {
        var steps = new List<StepResult>();
        var phase = new LoadBalancerPhase
        {
            Notify = new NotifyHook
            {
                Type   = NotifyHookType.File,
                Path   = Path.Combine(Path.GetTempPath(), $"dytools-absent-{Guid.NewGuid():N}.txt"),
                Action = FileHookAction.Delete
            }
        };

        Assert.IsTrue(await LoadBalancerGate.RunPhaseAsync(phase, "drain", Tokens, steps),
            "the desired end state is 'no marker', which is already true");
    }

    // -- Verify ----------------------------------------------------------------

    [TestMethod]
    public async Task Verify_PassesWhenTheStatusMatches()
    {
        using var server = new StubServer(HttpStatusCode.InternalServerError);
        var steps = new List<StepResult>();

        var phase = new LoadBalancerPhase
        {
            VerifyUrl            = server.Url,
            ExpectStatus         = 500,
            VerifyTimeoutSeconds = 10
        };

        Assert.IsTrue(await LoadBalancerGate.RunPhaseAsync(phase, "drain", Tokens, steps));
        Assert.IsTrue(steps.Single().Success);
    }

    [TestMethod]
    public async Task Verify_TimesOutWhenTheStatusNeverArrives()
    {
        using var server = new StubServer(HttpStatusCode.OK);
        var steps = new List<StepResult>();

        var phase = new LoadBalancerPhase
        {
            VerifyUrl             = server.Url,
            ExpectStatus          = 500,
            VerifyTimeoutSeconds  = 3,
            VerifyIntervalSeconds = 1,
            WaitSeconds           = 30
        };

        Assert.IsFalse(await LoadBalancerGate.RunPhaseAsync(phase, "drain", Tokens, steps));
        Assert.AreEqual(1, steps.Count, "a failed verify must not fall through to the wait");
    }

    // -- Precheck --------------------------------------------------------------
    //
    // The precheck decides whether the drain/restore routine applies at all. Getting it wrong
    // in one direction wastes a drain on an already-drained box; wrong in the other direction
    // stops a live site without draining it, so "in rotation" has to be proven, not assumed.

    [TestMethod]
    public async Task Precheck_LiveStatus_MeansInRotation()
    {
        using var server = new StubServer(HttpStatusCode.OK);

        var (inRotation, step) = await LoadBalancerGate.PrecheckAsync(
            new LoadBalancerPrecheck { Url = server.Url, LiveStatus = 200 }, Tokens);

        Assert.IsTrue(inRotation);
        Assert.IsTrue(step.Success, "the precheck itself never fails - it only decides");
    }

    [TestMethod]
    public async Task Precheck_FiveHundred_SkipsTheLoadBalancerRoutine()
    {
        using var server = new StubServer(HttpStatusCode.InternalServerError);

        var (inRotation, step) = await LoadBalancerGate.PrecheckAsync(
            new LoadBalancerPrecheck { Url = server.Url, TimeoutSeconds = 2, IntervalSeconds = 1 }, Tokens);

        Assert.IsFalse(inRotation);
        Assert.IsTrue(step.Success, "already out of rotation is a routing decision, not a failed step");
        StringAssert.Contains(step.Stdout, "skipping drain/restore");
    }

    [TestMethod]
    public async Task Precheck_UnreachableBox_SkipsTheLoadBalancerRoutine()
    {
        // The case this exists for: the site is stopped, so nothing answers on localhost. The
        // drain notification could not be delivered either, and a failed drain aborts the
        // deploy - so without this an offline server could never be updated.
        var (inRotation, step) = await LoadBalancerGate.PrecheckAsync(
            new LoadBalancerPrecheck { Url = "http://127.0.0.1:1/health", TimeoutSeconds = 2, IntervalSeconds = 1 },
            Tokens);

        Assert.IsFalse(inRotation);
        Assert.IsTrue(step.Success);
    }

    [TestMethod]
    public async Task Precheck_NonDefaultLiveStatusIsHonoured()
    {
        using var server = new StubServer(HttpStatusCode.NoContent);

        var (inRotation, _) = await LoadBalancerGate.PrecheckAsync(
            new LoadBalancerPrecheck { Url = server.Url, LiveStatus = 204 }, Tokens);

        Assert.IsTrue(inRotation);
    }

    [TestMethod]
    public async Task Precheck_WithNoUrl_AssumesInRotation()
    {
        var (inRotation, step) = await LoadBalancerGate.PrecheckAsync(new LoadBalancerPrecheck(), Tokens);

        Assert.IsTrue(inRotation,
            "guessing 'offline' on a misconfigured precheck would stop a live site without draining it");
        Assert.IsTrue(step.Success);
    }

    [TestMethod]
    public async Task Precheck_ResolvesTokensInTheUrl()
    {
        using var server = new StubServer(HttpStatusCode.OK);

        var (inRotation, step) = await LoadBalancerGate.PrecheckAsync(
            new LoadBalancerPrecheck { Url = server.Url + "?node={hostname}" }, Tokens);

        Assert.IsTrue(inRotation);
        StringAssert.Contains(step.Command, Environment.MachineName);
    }

    // -- Tokens ----------------------------------------------------------------

    [TestMethod]
    public void Tokens_AreSubstituted()
    {
        var resolved = new TokenContext("MyWeb", "WebApp")
            .Resolve("http://localhost/admin?node={hostname}&site={site}&project={project}");

        StringAssert.Contains(resolved!, $"node={Environment.MachineName}");
        StringAssert.Contains(resolved!, "site=MyWeb");
        StringAssert.Contains(resolved!, "project=WebApp");
    }

    [TestMethod]
    public void Tokens_AreCaseInsensitive()
        => StringAssert.Contains(
            new TokenContext("MyWeb", "WebApp").Resolve("{HOSTNAME}")!, Environment.MachineName);

    [TestMethod]
    public void Tokens_MissingSiteBecomesEmptyRatherThanLiteral()
        => Assert.AreEqual("site=", new TokenContext(null, "WebApp").Resolve("site={site}"));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void Tokens_NullAndEmptyPassThrough(string? value)
        => Assert.AreEqual(value, new TokenContext("MyWeb", "WebApp").Resolve(value));

    // -- Fixtures --------------------------------------------------------------

    private static TokenContext Tokens => new("MyWeb", "WebApp");

    /// <summary>
    /// Minimal loopback HTTP listener returning one fixed status. Port 0 lets the OS pick a
    /// free port so parallel test runs cannot collide.
    /// </summary>
    private sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();

        public string Url { get; }

        public StubServer(HttpStatusCode status)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();

            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }

                    context.Response.StatusCode = (int)status;
                    context.Response.Close();
                }
            });
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            _cts.Dispose();
        }
    }
}
