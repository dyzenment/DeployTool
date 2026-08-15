using Dytools.DeployTool.Services;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers blue-green slot selection. IIS is not present on a dev machine, so the choice
/// of which slot to deploy into is kept pure and verified here rather than against appcmd.
/// </summary>
[TestClass]
public sealed class IisSlotTests
{
    private const string SlotA = @"C:\inetpub\Web_A";
    private const string SlotB = @"C:\inetpub\Web_B";

    [TestMethod]
    public void ActiveIsSlotA_DeploysToSlotB()
    {
        Assert.AreEqual(SlotB, IisHandler.ResolveIdleSlot(SlotA, SlotA, SlotB));
    }

    [TestMethod]
    public void ActiveIsSlotB_DeploysToSlotA()
    {
        Assert.AreEqual(SlotA, IisHandler.ResolveIdleSlot(SlotB, SlotA, SlotB));
    }

    [TestMethod]
    public void SlotsAlternateAcrossConsecutiveDeploys()
    {
        var active = SlotA;

        active = IisHandler.ResolveIdleSlot(active, SlotA, SlotB);
        Assert.AreEqual(SlotB, active);

        active = IisHandler.ResolveIdleSlot(active, SlotA, SlotB);
        Assert.AreEqual(SlotA, active);

        active = IisHandler.ResolveIdleSlot(active, SlotA, SlotB);
        Assert.AreEqual(SlotB, active, "slots must keep alternating, never stick");
    }

    // -- Bootstrap: the site points at neither slot ----------------------------

    [TestMethod]
    public void ActiveIsAPreBlueGreenPath_BootstrapsToSlotA()
    {
        // First blue-green deploy: the site is still on its old single-slot path.
        // Landing on slot A and flipping to it is the correct bootstrap.
        Assert.AreEqual(SlotA, IisHandler.ResolveIdleSlot(@"C:\inetpub\Web", SlotA, SlotB));
    }

    [TestMethod]
    public void ActiveIsEmpty_BootstrapsToSlotA()
    {
        Assert.AreEqual(SlotA, IisHandler.ResolveIdleSlot("", SlotA, SlotB));
    }

    // -- Path comparison tolerances -------------------------------------------

    [TestMethod]
    public void ActivePathCasingIsIgnored()
    {
        Assert.AreEqual(SlotB, IisHandler.ResolveIdleSlot(@"c:\INETPUB\web_a", SlotA, SlotB),
            "appcmd casing must not cause a redeploy onto the live slot");
    }

    [TestMethod]
    public void TrailingSlashIsIgnored()
    {
        Assert.AreEqual(SlotB, IisHandler.ResolveIdleSlot(@"C:\inetpub\Web_A\", SlotA, SlotB));
    }

    [TestMethod]
    public void SurroundingWhitespaceIsIgnored()
    {
        // appcmd /text: output arrives with a trailing newline.
        Assert.AreEqual(SlotB, IisHandler.ResolveIdleSlot("  " + SlotA + "  ", SlotA, SlotB));
    }

    /// <summary>
    /// The failure this guards against is the worst one available: mirroring files into the
    /// folder that is currently serving traffic, which is exactly the outage blue-green exists
    /// to remove.
    /// </summary>
    [TestMethod]
    public void ResolvedSlotIsNeverTheActiveSlot()
    {
        foreach (var active in new[] { SlotA, SlotB, SlotA + @"\", "c:\\inetpub\\web_b" })
        {
            var idle = IisHandler.ResolveIdleSlot(active, SlotA, SlotB);
            Assert.AreNotEqual(
                active.Trim().TrimEnd('\\').ToLowerInvariant(),
                idle.ToLowerInvariant(),
                $"deploying into the live slot for active='{active}'");
        }
    }
}
