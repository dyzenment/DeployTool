using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dytools.DeployTool.Helpers;
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

    [TestMethod]
    public async Task Precheck_FailedHandshake_AssumesInRotation()
    {
        // The bug this guards: an https://localhost probe against a certificate issued for the
        // box's real hostname fails the handshake, which says nothing about rotation state. Read
        // as "offline" it skipped the drain and the app pool was bounced under live traffic.
        using var server = new TlsStubServer();

        var (inRotation, step) = await LoadBalancerGate.PrecheckAsync(
            new LoadBalancerPrecheck { Url = server.BrokenHandshakeUrl, TimeoutSeconds = 2, IntervalSeconds = 1 },
            Tokens);

        Assert.IsTrue(inRotation,
            "a probe that cannot complete a handshake is broken, not proof the box is out of rotation");
        Assert.IsTrue(step.Success);
        StringAssert.Contains(step.Stdout, "rotation state unknown");
    }

    [TestMethod]
    public async Task Precheck_LoopbackCertificateIsTrusted()
    {
        // The fix for the original symptom: a self-signed cert on localhost should be probed
        // successfully rather than rejected for not chaining to a trusted root.
        using var server = new TlsStubServer();

        var (inRotation, _) = await LoadBalancerGate.PrecheckAsync(
            new LoadBalancerPrecheck { Url = server.Url, LiveStatus = 200 }, Tokens);

        Assert.IsTrue(inRotation, "loopback certificate errors are ignored by design");
    }

    // -- Error reporting -------------------------------------------------------

    [TestMethod]
    public void Describe_UnwrapsInnerExceptions()
    {
        var ex = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException("The remote certificate is invalid."));

        var described = ProbeHttp.Describe(ex);

        StringAssert.Contains(described, "The remote certificate is invalid.",
            "logging only the outer message is what made this undiagnosable from the report");
    }

    [TestMethod]
    public void IsHandshakeFailure_DistinguishesTlsFromARefusedConnection()
    {
        Assert.IsTrue(ProbeHttp.IsHandshakeFailure(
            new HttpRequestException("ssl", new AuthenticationException("bad cert"))));

        Assert.IsFalse(ProbeHttp.IsHandshakeFailure(
            new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused))),
            "a refused connection is a real answer about the site, not a broken probe");
    }

    [TestMethod]
    public void ValidateCertificate_LoopbackIsAccepted_EvenWhenTheCertificateIsBad()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost/api/health");

        Assert.IsTrue(ProbeHttp.ValidateCertificate(
            request, null, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [TestMethod]
    public void ValidateCertificate_NonLoopbackRejectionNamesTheActualErrors()
    {
        // Returning false here would report only "rejected by the provided
        // RemoteCertificateValidationCallback", which is less than the runtime says without any
        // callback at all - a diagnosis worse than the one being fixed.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://web01.example.com/api/health");

        var thrown = Assert.ThrowsException<AuthenticationException>(() => ProbeHttp.ValidateCertificate(
            request, null, null,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));

        StringAssert.Contains(thrown.Message, "RemoteCertificateNameMismatch");
        StringAssert.Contains(thrown.Message, "RemoteCertificateChainErrors");
        StringAssert.Contains(thrown.Message, "web01.example.com");
        Assert.IsTrue(ProbeHttp.IsHandshakeFailure(thrown),
            "the thrown detail must still read as a handshake failure, or the precheck would " +
            "treat a bad certificate as an offline box again");
    }

    [TestMethod]
    public void ValidateCertificate_AValidCertificateIsAcceptedAnywhere()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://web01.example.com/api/health");

        Assert.IsTrue(ProbeHttp.ValidateCertificate(request, null, null, SslPolicyErrors.None));
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

    /// <summary>
    /// Loopback TLS fixture for the two certificate cases.
    ///
    /// <see cref="Url"/> serves HTTPS under a self-signed certificate that chains to nothing -
    /// the shape of a real IIS box seen from localhost, and what the loopback exemption exists
    /// for. <see cref="BrokenHandshakeUrl"/> accepts the connection and then answers with bytes
    /// that are not TLS, which fails the handshake the same way a rejected certificate does:
    /// something is listening, the client cannot talk to it, and no HTTP status is ever reached.
    ///
    /// Raw sockets rather than HttpListener because binding an HTTPS prefix needs netsh on
    /// Windows, and these tests should run anywhere.
    /// </summary>
    private sealed class TlsStubServer : IDisposable
    {
        private const string Response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";

        private readonly TcpListener _tls;
        private readonly TcpListener _garbage;
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cts = new();

        public string Url { get; }
        public string BrokenHandshakeUrl { get; }

        public TlsStubServer()
        {
            _certificate = SelfSigned();

            _tls = new TcpListener(IPAddress.Loopback, 0);
            _tls.Start();
            Url = $"https://127.0.0.1:{((IPEndPoint)_tls.LocalEndpoint).Port}/health";

            _garbage = new TcpListener(IPAddress.Loopback, 0);
            _garbage.Start();
            BrokenHandshakeUrl = $"https://127.0.0.1:{((IPEndPoint)_garbage.LocalEndpoint).Port}/health";

            _ = Task.Run(() => ServeAsync(_tls, tls: true));
            _ = Task.Run(() => ServeAsync(_garbage, tls: false));
        }

        private async Task ServeAsync(TcpListener listener, bool tls)
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await using var network = client.GetStream();

                            if (!tls)
                            {
                                // Not a TLS record, so the client's handshake fails rather than
                                // its certificate check - the distinction under test.
                                await network.WriteAsync("not tls at all\r\n\r\n"u8.ToArray(), _cts.Token);
                                return;
                            }

                            await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
                            await ssl.AuthenticateAsServerAsync(_certificate, false, checkCertificateRevocation: false);

                            // One read is enough: the point is to drain the request so the client
                            // is not writing into a full buffer, not to parse it.
#pragma warning disable CA2022
                            var buffer = new byte[4096];
                            await ssl.ReadAsync(buffer, _cts.Token);
#pragma warning restore CA2022
                            await ssl.WriteAsync(System.Text.Encoding.ASCII.GetBytes(Response), _cts.Token);
                            await ssl.FlushAsync(_cts.Token);
                        }
                        catch
                        {
                            // A client that hangs up mid-handshake is one of the cases being
                            // provoked here, not a fixture failure.
                        }
                    }
                });
            }
        }

        /// <summary>
        /// Exported and reloaded as a PFX because a certificate straight out of CreateSelfSigned
        /// carries an ephemeral key that SslStream cannot use for server authentication.
        /// </summary>
        private static X509Certificate2 SelfSigned()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _tls.Stop();
            _garbage.Stop();
            _certificate.Dispose();
            _cts.Dispose();
        }
    }
}
