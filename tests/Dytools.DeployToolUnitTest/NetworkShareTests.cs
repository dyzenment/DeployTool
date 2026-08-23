using Dytools.DeployTool;
using Dytools.DeployTool.Helpers;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers how a configured credential is turned into a connection attempt.
///
/// The connection itself is a Win32 call against a real SMB server, so it is not asserted here.
/// What is asserted is everything decided before that call - which share to authenticate
/// against, what form the username takes, and the two ways a password goes wrong quietly. Those
/// are the parts that fail as a bare "logon failure" hours later in a CI log.
/// </summary>
[TestClass]
public sealed class NetworkShareTests
{
    // -- Share root ------------------------------------------------------------

    [TestMethod]
    public void ConnectsToTheShare_NotTheFolderInsideIt()
    {
        // The Win32 connection API authenticates against a share; handing it a deeper path
        // fails outright.
        Assert.AreEqual(@"\\WEB02\deploy", NetworkShare.ShareRootOf(@"\\WEB02\deploy\incoming"));
        Assert.AreEqual(@"\\WEB02\deploy", NetworkShare.ShareRootOf(@"\\WEB02\deploy"));
        Assert.AreEqual(@"\\WEB02\deploy", NetworkShare.ShareRootOf(@"\\WEB02\deploy\incoming\"));
        Assert.AreEqual(@"\\10.0.1.20\deploy", NetworkShare.ShareRootOf(@"\\10.0.1.20\deploy\incoming"));
    }

    [TestMethod]
    public void ForwardSlashesInAUncPathStillResolve()
        => Assert.AreEqual(@"\\WEB02\deploy", NetworkShare.ShareRootOf("//WEB02/deploy/incoming"));

    [TestMethod]
    public void NonUncPathsHaveNoShareToAuthenticateAgainst()
    {
        Assert.IsNull(NetworkShare.ShareRootOf(@"C:\deploy\incoming"));
        Assert.IsNull(NetworkShare.ShareRootOf("/var/lib/deploytool/incoming"));

        // A server with no share named is not addressable either.
        Assert.IsNull(NetworkShare.ShareRootOf(@"\\WEB02"));
        Assert.IsNull(NetworkShare.ShareRootOf(@"\\WEB02\"));
    }

    // -- Username form ---------------------------------------------------------

    [TestMethod]
    public void BareUsernameIsQualifiedWithThePeerName()
    {
        // Unqualified, Windows offers the credential with the *primary's* workgroup as its
        // domain, which the peer cannot resolve. The peer's own name is what makes it a
        // local-account logon there.
        Assert.AreEqual(@"WEB02\deploysvc", NetworkShare.Qualify("deploysvc", @"\\WEB02\deploy"));
    }

    [TestMethod]
    public void AnIpAddressedShareLeavesTheUsernameBare()
    {
        // "10.0.1.20\deploysvc" is not a domain Windows will accept; bare is what works.
        Assert.AreEqual("deploysvc", NetworkShare.Qualify("deploysvc", @"\\10.0.1.20\deploy"));
        Assert.AreEqual("deploysvc", NetworkShare.Qualify("deploysvc", @"\\::1\deploy"));
    }

    [TestMethod]
    public void AnAlreadyQualifiedUsernameIsLeftExactlyAsWritten()
    {
        Assert.AreEqual(@"WEB02\deploysvc",  NetworkShare.Qualify(@"WEB02\deploysvc", @"\\WEB02\deploy"));
        Assert.AreEqual(@"CORP\deploysvc",   NetworkShare.Qualify(@"CORP\deploysvc",  @"\\WEB02\deploy"));
        Assert.AreEqual("svc@corp.local",    NetworkShare.Qualify("svc@corp.local",   @"\\WEB02\deploy"));
    }

    // -- No credentials configured ---------------------------------------------

    [TestMethod]
    public void NoUsernameMeansConnectAsWhoeverWeAlreadyAre()
    {
        // The domain / mirrored-account case, and everything the tool did before credentials
        // existed. It must stay a no-op, not an error.
        Assert.IsNull(NetworkShare.Connect(@"\\WEB02\deploy\incoming", null, null));
        Assert.IsNull(NetworkShare.Connect(@"\\WEB02\deploy\incoming", "   ", "pw"));
    }

    [TestMethod]
    public void ALocalPathIgnoresCredentialsRatherThanFailing()
    {
        // Peers are usually UNC, but a local path is legitimate for testing - and a stray
        // username in the config should not stop a delivery that needs no authentication.
        Assert.IsNull(NetworkShare.Connect(@"C:\peer\incoming", "deploysvc", "pw"));
    }

    // -- Password resolution ---------------------------------------------------

    [TestMethod]
    public void AnUnsetEnvironmentReferenceIsAnErrorRatherThanAMysteriousLogonFailure()
    {
        // Unexpanded, "%NOT_SET%" would be offered to the peer verbatim and come back as a
        // bare "the username or password is incorrect" - pointing at the wrong problem.
        var ex = Assert.ThrowsException<DeployException>(
            () => NetworkShare.ResolvePassword("%DEPLOYTOOL_TEST_UNSET_PW%"));

        StringAssert.Contains(ex.Message, "not set");

        // Names the variable so the fix is obvious, and it is only ever a variable name.
        StringAssert.Contains(ex.Message, "DEPLOYTOOL_TEST_UNSET_PW");
    }

    [TestMethod]
    public void AResolvedEnvironmentReferenceBecomesThePassword()
    {
        try
        {
            Environment.SetEnvironmentVariable("DEPLOYTOOL_TEST_PW", "s3cret");
            Assert.AreEqual("s3cret", NetworkShare.ResolvePassword("%DEPLOYTOOL_TEST_PW%"));
            Assert.AreEqual("s3cret", NetworkShare.ResolvePassword("$DEPLOYTOOL_TEST_PW"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEPLOYTOOL_TEST_PW", null);
        }
    }

    [TestMethod]
    public void ALiteralPasswordIsAllowedButNotSilent()
    {
        // Warned about rather than rejected: someone testing on a lab box should not be
        // blocked, but a secret in a committed file should never pass unremarked.
        var console = new StringWriter();
        var original = Console.Out;

        try
        {
            Console.SetOut(console);
            Assert.AreEqual("literal-pw", NetworkShare.ResolvePassword("literal-pw"));
        }
        finally
        {
            Console.SetOut(original);
        }

        StringAssert.Contains(console.ToString(), "%DEPLOY_SHARE_PASSWORD%");
    }

    [TestMethod]
    public void NoPasswordResolvesToEmptyRatherThanThrowing()
    {
        // Legitimate: a share can be granted to an account that authenticates some other way.
        Assert.AreEqual(string.Empty, NetworkShare.ResolvePassword(null));
        Assert.AreEqual(string.Empty, NetworkShare.ResolvePassword(""));
    }
}
