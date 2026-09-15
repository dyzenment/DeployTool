using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Dytools.DeployTool.Helpers;

/// <summary>
/// The HTTP client every health probe, warmup and notify hook goes through.
///
/// These requests are aimed at localhost by design - the question they ask is "is THIS box
/// serving", which only this box can answer. But a production IIS certificate is issued for the
/// site's real hostname, never for "localhost", so validating it against the loopback name fails
/// the handshake before a single HTTP byte moves. The endpoint is fine; the name on the cert
/// simply is not the name we dialled.
///
/// So certificate errors are ignored for loopback hosts and enforced everywhere else. Nothing is
/// given up by that: to make a loopback request at all we are already executing on the box, which
/// is strictly more access than any certificate was protecting. A non-loopback probe crosses a
/// network and is still validated normally.
/// </summary>
public static class ProbeHttp
{
    /// <summary>Client for a probe request, trusting loopback certificates only.</summary>
    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateCertificate
        };

        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    /// <summary>
    /// Accepts a certificate if it is valid or if the request is going to loopback.
    ///
    /// Rejection throws rather than returning false, which looks odd until you see what false
    /// produces: the runtime collapses every cause into "The remote certificate was rejected by
    /// the provided RemoteCertificateValidationCallback", discarding the SslPolicyErrors that
    /// say whether the name, the chain or the dates were the problem. Since the point of this
    /// class is to make certificate failures readable, throwing with the errors attached keeps
    /// the one detail worth having - and it lands in the exception chain, so Describe finds it
    /// and IsHandshakeFailure still recognises it.
    ///
    /// Public so the loopback rule can be tested without a non-loopback listener to hand.
    /// </summary>
    public static bool ValidateCertificate(
        HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None || request.RequestUri?.IsLoopback == true)
            return true;

        throw new AuthenticationException(
            $"The remote certificate for {request.RequestUri?.Host} is invalid: {errors}.");
    }

    /// <summary>
    /// Flattens an exception chain into one line.
    ///
    /// HttpRequestException's own message for a failed handshake is "The SSL connection could not
    /// be established, see inner exception." - which, logged alone, tells you only that something
    /// about TLS went wrong. The reason you actually need (NotTimeValid, RemoteCertificateNameMismatch,
    /// UntrustedRoot) is one or two levels down, so unwrap the whole chain rather than making
    /// whoever reads the report guess.
    /// </summary>
    public static string Describe(Exception exception)
    {
        var messages = new List<string>();

        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (messages.Count == 0 || !string.Equals(messages[^1], current.Message, StringComparison.Ordinal))
                messages.Add(current.Message);

        return string.Join(" -> ", messages);
    }

    /// <summary>
    /// True when the request died in the TLS handshake.
    ///
    /// Worth separating from every other failure because it says something different. A refused
    /// connection means nothing is listening - a real answer about the site's state. A handshake
    /// failure means something IS listening and the probe cannot talk to it, which says nothing
    /// about the site and everything about the probe. Callers that infer state from a failed
    /// request need to tell those two apart.
    /// </summary>
    public static bool IsHandshakeFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is AuthenticationException)
                return true;

        return false;
    }
}
