using System.Diagnostics;
using System.Text.Json;
using Dytools.DeployTool;
using Dytools.DeployTool.Helpers;
using Dytools.DeployTool.Models.Config;

namespace Dytools.DeployToolUnitTest;

/// <summary>
/// Covers the resource policy that keeps a deploy from starving the applications sharing the
/// box. The priority half is inherently a process-global side effect, so the tests here focus
/// on the parts that can be asserted deterministically: the switch string every build, publish
/// and test call is assembled from, and the config binding that feeds it.
///
/// These tests mutate ResourceGovernor's static state, so each one sets the settings it needs
/// rather than relying on order, and the class restores the defaults when it finishes.
/// </summary>
[TestClass]
public sealed class ResourceGovernorTests
{
    [TestCleanup]
    public void RestoreDefaults() => ResourceGovernor.Apply(new ResourceConfig());

    // -- Defaults --------------------------------------------------------------

    [TestMethod]
    public void Defaults_AreConservative()
    {
        ResourceGovernor.Apply(new ResourceConfig());

        Assert.AreEqual(ProcessPriority.BelowNormal, ResourceGovernor.Settings.Priority);
        Assert.AreEqual(1, ResourceGovernor.Settings.MaxCpuCount);
        Assert.IsTrue(ResourceGovernor.Settings.DisableBuildServers);
        Assert.IsTrue(ResourceGovernor.Settings.WorkstationGc);
        Assert.IsFalse(ResourceGovernor.Settings.BackgroundIo, "background i/o slows a deploy, so it must be opt-in");
    }

    [TestMethod]
    public void NullConfig_FallsBackToDefaults()
    {
        ResourceGovernor.Apply(null);

        Assert.AreEqual(ProcessPriority.BelowNormal, ResourceGovernor.Settings.Priority);
        Assert.AreEqual(1, ResourceGovernor.Settings.MaxCpuCount);
    }

    // -- Switch assembly -------------------------------------------------------

    [TestMethod]
    public void DefaultSwitches_DisableBothBuildServersAndPinOneCore()
    {
        ResourceGovernor.Apply(new ResourceConfig());

        var switches = ResourceGovernor.MsBuildSwitches;

        StringAssert.Contains(switches, "-p:UseSharedCompilation=false", "the Roslyn compiler server outlives the build");
        StringAssert.Contains(switches, "-nodeReuse:false", "reused MSBuild nodes keep the priority of whoever started them");
        StringAssert.Contains(switches, "-maxcpucount:1");
    }

    [TestMethod]
    public void MaxCpuCount_IsHonoured()
    {
        ResourceGovernor.Apply(new ResourceConfig { MaxCpuCount = 4 });

        StringAssert.Contains(ResourceGovernor.MsBuildSwitches, "-maxcpucount:4");
    }

    [TestMethod]
    public void MaxCpuCountZero_OmitsTheSwitchEntirely()
    {
        ResourceGovernor.Apply(new ResourceConfig { MaxCpuCount = 0 });

        Assert.IsFalse(ResourceGovernor.MsBuildSwitches.Contains("-maxcpucount"),
            "0 means 'no limit', which is expressed by passing no switch at all");
    }

    [TestMethod]
    public void DisableBuildServersFalse_DropsOnlyTheServerSwitches()
    {
        ResourceGovernor.Apply(new ResourceConfig { DisableBuildServers = false, MaxCpuCount = 2 });

        var switches = ResourceGovernor.MsBuildSwitches;

        Assert.IsFalse(switches.Contains("UseSharedCompilation"));
        Assert.IsFalse(switches.Contains("nodeReuse"));
        StringAssert.Contains(switches, "-maxcpucount:2", "the core cap is independent of the server opt-out");
    }

    [TestMethod]
    public void EverythingOff_YieldsAnEmptySwitchString()
    {
        ResourceGovernor.Apply(new ResourceConfig { DisableBuildServers = false, MaxCpuCount = 0 });

        Assert.AreEqual(string.Empty, ResourceGovernor.MsBuildSwitches,
            "an empty string must be safe to splice into a command line");
    }

    // -- Environment propagation -----------------------------------------------
    //
    // These variables are how the policy reaches processes the tool never launches directly:
    // ProcessStartInfo.Environment is seeded from this process, so descendants inherit them.

    [TestMethod]
    public void DisableBuildServers_OptsOutOfTheMsBuildServerToo()
    {
        // -nodeReuse:false does not cover the MSBuild Server; only this variable does.
        Environment.SetEnvironmentVariable("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", null);
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", null);

        ResourceGovernor.Apply(new ResourceConfig { DisableBuildServers = true });

        Assert.AreEqual("1", Environment.GetEnvironmentVariable("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"));
        Assert.AreEqual("1", Environment.GetEnvironmentVariable("MSBUILDDISABLENODEREUSE"));
    }

    [TestMethod]
    public void ExplicitEnvironmentValue_IsNotOverwritten()
    {
        Environment.SetEnvironmentVariable("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "0");
        try
        {
            ResourceGovernor.Apply(new ResourceConfig { DisableBuildServers = true });

            Assert.AreEqual("0", Environment.GetEnvironmentVariable("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"),
                "an operator setting the variable by hand must win over config");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", null);
        }
    }

    // -- Priority --------------------------------------------------------------

    [TestMethod]
    public void BelowNormal_IsActuallyAppliedToThisProcess()
    {
        ResourceGovernor.Apply(new ResourceConfig { Priority = ProcessPriority.BelowNormal });

        using var self = Process.GetCurrentProcess();
        self.Refresh();

        // Every descendant inherits this at creation - that inheritance is the whole point of
        // setting it on self rather than on each child after Start().
        Assert.AreEqual(ProcessPriorityClass.BelowNormal, self.PriorityClass);
    }

    // -- Config binding --------------------------------------------------------

    [TestMethod]
    public void OmittedResourcesBlock_LeavesConfigNullAndStillGovernsTheRun()
    {
        var config = JsonSerializer.Deserialize<DeployConfig>("""{ "projectsFolder": "src/" }""", JsonOptions.Default)!;

        Assert.IsNull(config.Resources, "the block is optional");

        ResourceGovernor.Apply(config.Resources);
        Assert.AreEqual(ProcessPriority.BelowNormal, ResourceGovernor.Settings.Priority);
    }

    [TestMethod]
    public void PriorityBindsFromString()
    {
        var json = """{ "projectsFolder": "src/", "resources": { "priority": "Idle", "maxCpuCount": 2 } }""";

        var config = JsonSerializer.Deserialize<DeployConfig>(json, JsonOptions.Default)!;

        Assert.AreEqual(ProcessPriority.Idle, config.Resources!.Priority);
        Assert.AreEqual(2, config.Resources.MaxCpuCount);
    }

    [TestMethod]
    public void PriorityBindingIsCaseInsensitive()
    {
        var json = """{ "projectsFolder": "src/", "resources": { "priority": "belowNormal" } }""";

        var config = JsonSerializer.Deserialize<DeployConfig>(json, JsonOptions.Default)!;

        Assert.AreEqual(ProcessPriority.BelowNormal, config.Resources!.Priority);
    }
}
