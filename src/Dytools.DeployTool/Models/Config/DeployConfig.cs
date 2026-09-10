using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dytools.DeployTool.Models.Config;

// -----------------------------------------------------------------------------
// Root
// -----------------------------------------------------------------------------

public sealed class DeployConfig
{
    /// <summary>
    /// Relative path from repo root to the folder containing all deployable
    /// project subdirectories. Each project lives at {projectsFolder}/{project.name}/.
    /// </summary>
    [JsonPropertyName("projectsFolder")]
    public string ProjectsFolder { get; set; } = string.Empty;

    /// <summary>
    /// Relative path from repo root to the folder containing unit test projects.
    /// Convention lookup: {unitTestsFolder}/{projectName}{unitTestProjectSuffix}/
    /// Example: "tests/"
    /// </summary>
    [JsonPropertyName("unitTestsFolder")]
    public string? UnitTestsFolder { get; set; }

    /// <summary>
    /// Suffix appended to a project name to find its unit test project by convention.
    /// Example: "UnitTest" → Admin project → tests/AdminUnitTest/AdminUnitTest.csproj
    /// </summary>
    [JsonPropertyName("unitTestProjectSuffix")]
    public string? UnitTestProjectSuffix { get; set; }

    /// <summary>
    /// Repo-wide warning codes to suppress on every dotnet publish call.
    /// Concatenated with any noWarn defined on individual build configs.
    /// Example: "CS8600,CS8601,CS8602,CS8618"
    /// </summary>
    [JsonPropertyName("noWarn")]
    public string? NoWarn { get; set; }

    [JsonPropertyName("packCache")]
    public PackCacheConfig? PackCache { get; set; }

    /// <summary>
    /// How hard the deploy pipeline should work to stay out of the way of the applications
    /// already running on this box. Omit the block to accept the defaults, which are already
    /// conservative: below-normal priority, one build core, no build server processes.
    /// </summary>
    [JsonPropertyName("resources")]
    public ResourceConfig? Resources { get; set; }

    /// <summary>
    /// When true, a commit with no pub: directive deploys nothing - publishing becomes
    /// opt-in per commit. Use pub:* to deploy everything, pub:none to deploy nothing.
    ///
    /// The --force-all flag still overrides this: it is an explicit manual action, and a
    /// button that silently did nothing would be worse than the safety it bought.
    /// Default: false - changed-file resolution behaves as it always has.
    /// </summary>
    [JsonPropertyName("doNotPublishIfNoPubInCommitMessage")]
    public bool DoNotPublishIfNoPubInCommitMessage { get; set; } = false;

    /// <summary>
    /// The server fleet. Empty (the default) means single-box: the tool behaves exactly as
    /// it always has, with no propagation.
    ///
    /// There is no "primary" flag: whichever box the deploy job lands on is the primary for
    /// that run, and every other listed server is a peer. Identity is resolved by matching
    /// hostname, so any box can fill either role without a config change.
    /// </summary>
    [JsonPropertyName("servers")]
    public List<ServerConfig> Servers { get; set; } = [];

    [JsonPropertyName("rollout")]
    public RolloutConfig? Rollout { get; set; }

    [JsonPropertyName("projects")]
    public List<ProjectConfig> Projects { get; set; } = [];
}

// -----------------------------------------------------------------------------
// Resource governance
// -----------------------------------------------------------------------------

/// <summary>
/// Caps on what a deploy is allowed to take from the box it runs on. Applied by
/// ResourceGovernor at startup; priority and environment both inherit, so these settings
/// reach every process the deploy spawns, not just the ones this tool launches directly.
/// </summary>
public sealed class ResourceConfig
{
    /// <summary>
    /// Scheduling priority for the deploy and everything it spawns.
    /// "BelowNormal" (default) yields to live applications while still making steady progress.
    /// "Idle" runs only on otherwise-spare cycles - safest for a busy production box, but a
    /// deploy can take substantially longer. "Normal" opts out entirely.
    /// </summary>
    [JsonPropertyName("priority")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessPriority Priority { get; set; } = ProcessPriority.BelowNormal;

    /// <summary>
    /// Windows only. Puts the deploy process into background mode, which lowers I/O and memory
    /// priority as well as CPU - a priority class covers CPU alone. This governs the file
    /// copying the tool performs itself, which is the I/O-heavy part of a folder or IIS deploy.
    ///
    /// Windows applies background mode to the calling process only, so it does not reach the
    /// build. Expect a noticeably slower deploy in exchange for a quieter disk. Default: false.
    /// </summary>
    [JsonPropertyName("backgroundIo")]
    public bool BackgroundIo { get; set; } = false;

    /// <summary>
    /// Cores MSBuild may build across, passed as -maxcpucount. Default 1, which is the single
    /// most effective setting here: priority only helps once cores are contended, whereas a
    /// low core count leaves cores free in the first place. 0 means "no limit" (MSBuild's own
    /// default of one node per core).
    /// </summary>
    [JsonPropertyName("maxCpuCount")]
    public int MaxCpuCount { get; set; } = 1;

    /// <summary>
    /// Stops builds using MSBuild node reuse, the MSBuild Server and the Roslyn shared
    /// compiler. Those are daemons that outlive the build that spawned them, so a later build
    /// attaching to one runs at whatever priority that earlier build had - which defeats every
    /// other setting in this block. Leave true unless build time matters more than restraint.
    /// </summary>
    [JsonPropertyName("disableBuildServers")]
    public bool DisableBuildServers { get; set; } = true;

    /// <summary>
    /// Forces workstation GC on spawned .NET processes. Server GC allocates a heap and a
    /// dedicated thread per core, which is a real cost on a many-core production host.
    /// Default: true.
    /// </summary>
    [JsonPropertyName("workstationGc")]
    public bool WorkstationGc { get; set; } = true;
}

/// <summary>Scheduling priority, mapped to ProcessPriorityClass (a nice value on Unix).</summary>
public enum ProcessPriority { Normal, BelowNormal, Idle }

// -----------------------------------------------------------------------------
// Servers / Rollout
// -----------------------------------------------------------------------------

public sealed class ServerConfig
{
    /// <summary>Short reference for this box, used in logs, manifests, and reports.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Matched case-insensitively against Environment.MachineName to decide which entry
    /// the running box is. Zero per-machine configuration.
    /// </summary>
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Where the primary drops run folders for this peer, e.g. "\\WEBSERVER02\deploy\incoming".
    /// Unused for whichever server is currently the primary (it applies inline from staging).
    /// </summary>
    [JsonPropertyName("incomingShare")]
    public string? IncomingShare { get; set; }

    /// <summary>
    /// Account the primary authenticates to this peer's share with. Leave null to connect as
    /// whoever the deploy is already running as - correct on a domain, or when the same local
    /// account exists with the same password on both boxes.
    ///
    /// Set it when neither is true, which is the usual workgroup case: a self-hosted runner
    /// service runs as NETWORK SERVICE and therefore authenticates as the machine account, a
    /// name a peer with no domain controller cannot resolve.
    ///
    /// Must name a LOCAL account on the peer. Write it as "PEERNAME\user"; a bare name is
    /// qualified with the peer's name automatically, except when the share is addressed by IP,
    /// where a bare name is what works.
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>
    /// Password for <see cref="Username"/>. Write it as "%DEPLOY_SHARE_PASSWORD%" and set the
    /// variable from a CI secret - this file lives in your repository. A literal value is
    /// accepted but warned about on every run, and an environment reference that resolves to
    /// nothing is an error rather than a mystifying logon failure.
    /// </summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }
}

public sealed class RolloutConfig
{
    /// <summary>
    /// Soak time between the primary going live and peers applying the build. The clock
    /// starts at primary success, not at plan time - see Planner. Overridden per commit
    /// with wait:N.
    /// </summary>
    [JsonPropertyName("delaySeconds")]
    public int DelaySeconds { get; set; } = 3600;

    /// <summary>Run folders retained per peer before the oldest are pruned.</summary>
    [JsonPropertyName("keepRuns")]
    public int KeepRuns { get; set; } = 5;

    /// <summary>
    /// Prove every peer is reachable and writable before building, and abort the run if one is
    /// not. On by default: propagation is the last thing a run does, so without it an
    /// unreachable peer costs the whole build and test cycle to discover.
    ///
    /// Turn it off only if you would rather a peer that is down not block a deploy to the boxes
    /// that are up - the run will still fail at propagation, just later.
    /// </summary>
    [JsonPropertyName("precheckPeers")]
    public bool PrecheckPeers { get; set; } = true;
}

// -----------------------------------------------------------------------------
// Pack Cache
// -----------------------------------------------------------------------------

public sealed class PackCacheConfig
{
    /// <summary>
    /// Path with supported tokens:
    ///   {CommonAppData} → writable local app data (always writable, no elevation)
    ///                      Windows (NetworkService): C:\Windows\ServiceProfiles\NetworkService\AppData\Local
    ///                      macOS: ~/Library/Application Support
    ///                      Linux: ~/.local/share
    ///   {packId}        → replaced with the velopack.packId value
    ///   %ENV_VAR%       → environment variable expansion
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Maximum number of historical releases to retain per packId.</summary>
    [JsonPropertyName("keepReleases")]
    public int? KeepReleases { get; set; }
}

// -----------------------------------------------------------------------------
// Project
// -----------------------------------------------------------------------------

public sealed class ProjectConfig
{
    /// <summary>Must match the subfolder name and .csproj filename under projectsFolder.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// When true, this project is excluded from all deploy runs without removing it.
    /// Disabled projects skip the existence check - the folder does not need to exist.
    /// </summary>
    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; } = false;

    /// <summary>
    /// When false, unit tests are skipped for this project.
    /// Default: true.
    /// </summary>
    [JsonPropertyName("runTests")]
    public bool RunTests { get; set; } = true;

    /// <summary>
    /// When true, a unit test failure aborts deployment of this project.
    /// When false, test failures are logged but deployment continues.
    /// Default: true.
    /// </summary>
    [JsonPropertyName("abortOnUnitTestFailure")]
    public bool AbortOnUnitTestFailure { get; set; } = true;

    /// <summary>
    /// Override for the unit test project. Can be:
    ///   - A project name:           "AdminUnitTest"
    ///   - A repo-root-relative path: "tests/AdminUnitTest/AdminUnitTest.csproj"
    /// If absent, the convention is used: {unitTestsFolder}/{name}{unitTestProjectSuffix}/
    /// </summary>
    [JsonPropertyName("unitTestProject")]
    public string? UnitTestProject { get; set; }

    /// <summary>
    /// Additional change triggers beyond this project's own folder.
    /// "ProjectName" - triggers if any file in that project folder changed.
    /// "some/path"   - substring match against any changed file path.
    /// "*"           - catch-all: any solution-level change (outside all project folders).
    /// .csproj ProjectReferences are resolved automatically - no need to repeat them here.
    /// </summary>
    [JsonPropertyName("dependentProjects")]
    [JsonConverter(typeof(StringOrArrayConverter))]
    public List<string> DependentProjects { get; set; } = [];

    /// <summary>
    /// Shell commands to run in the project folder before any targets build.
    /// If present, replaces the default auto npm install behavior.
    /// Default: if package.json exists, "npm install" runs automatically.
    /// </summary>
    [JsonPropertyName("preBuild")]
    public List<string>? PreBuild { get; set; }

    /// <summary>
    /// Warning codes to suppress for this project's builds and tests.
    /// Concatenated with the repo-wide noWarn from the root config.
    /// Example: "NU1701,CS0618"
    /// </summary>
    [JsonPropertyName("noWarn")]
    public string? NoWarn { get; set; }

    [JsonPropertyName("targets")]
    public List<TargetConfig> Targets { get; set; } = [];
}

// -----------------------------------------------------------------------------
// Target
// -----------------------------------------------------------------------------

public sealed class TargetConfig
{
    /// <summary>
    /// Software prerequisites required by this target.
    /// Installed automatically if missing. Only for selected targets.
    /// Supported: "nodejs", "vpk"
    /// </summary>
    [JsonPropertyName("prerequisites")]
    public List<string> Prerequisites { get; set; } = [];

    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeployType Type { get; set; }

    [JsonPropertyName("build")]
    public BuildConfig? Build { get; set; }

    /// <summary>
    /// Whether to snapshot before deploying and restore on failure.
    /// Default: false.
    /// </summary>
    [JsonPropertyName("rollback")]
    public bool Rollback { get; set; } = false;

    [JsonPropertyName("velopack")]
    public VelopackConfig? Velopack { get; set; }

    [JsonPropertyName("iis")]
    public IisConfig? Iis { get; set; }

    [JsonPropertyName("folder")]
    public FolderConfig? Folder { get; set; }
}

// -----------------------------------------------------------------------------
// Build Config
// -----------------------------------------------------------------------------

public sealed class BuildConfig
{
    [JsonPropertyName("configuration")]
    public string Configuration { get; set; } = "Release";

    [JsonPropertyName("runtime")]
    public string? Runtime { get; set; }

    [JsonPropertyName("selfContained")]
    public bool? SelfContained { get; set; }

    /// <summary>
    /// Target framework moniker to build for.
    /// Required when the project has multiple target frameworks (TargetFrameworks).
    /// Optional for single-target projects - inferred from the .csproj if absent.
    /// Examples: "net8.0-windows", "net481", "net8.0"
    /// For .NET Framework projects this triggers msbuild instead of dotnet publish.
    /// </summary>
    [JsonPropertyName("targetFramework")]
    public string? TargetFramework { get; set; }

    [JsonPropertyName("singleFile")]
    public bool? SingleFile { get; set; }

    /// <summary>
    /// Comma-separated list of warning codes to suppress during build.
    /// Example: "CS8600,CS8601,CS8602,CS8618"
    /// Passed to dotnet publish as /nowarn:{value}
    /// </summary>
    [JsonPropertyName("noWarn")]
    public string? NoWarn { get; set; }
}

// -----------------------------------------------------------------------------
// Deploy Types
// -----------------------------------------------------------------------------

public enum DeployType { Velopack, Iis, Folder }

/// <summary>
/// Where a target's output actually lands, which decides whether the step is emitted
/// into every server's plan or only the primary's.
///   Server - destination is a path/site/service on a specific box (iis, folder).
///            Emitted to every server plan.
///   Global - destination is shared infrastructure reachable from anywhere, e.g. an
///            Azure container (velopack). Emitted once, on the primary only.
/// Ask the handler via GetScope(target) - never special-case the DeployType.
/// </summary>
public enum DeployScope { Server, Global }

// -----------------------------------------------------------------------------
// Velopack
// -----------------------------------------------------------------------------

public sealed class VelopackConfig
{
    [JsonPropertyName("packId")]
    public string PackId { get; set; } = string.Empty;

    [JsonPropertyName("packTitle")]
    public string? PackTitle { get; set; }

    /// <summary>
    /// Optional icon path override. Can be repo-root-relative, project-relative, or absolute.
    /// If absent, uses the ApplicationIcon discovered from the .csproj at startup.
    /// Icon retry logic: tries configured extension → .icns → no icon.
    /// </summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    /// <summary>
    /// Velopack runtime identifier. Cross-compilation directive ([win]/[osx]/[linux])
    /// is automatically injected when the target OS differs from the runner OS.
    /// </summary>
    [JsonPropertyName("runtime")]
    public string? Runtime { get; set; }

    [JsonPropertyName("framework")]
    public string? Framework { get; set; }

    [JsonPropertyName("mainExe")]
    public string? MainExe { get; set; }

    [JsonPropertyName("delivery")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VelopackDelivery Delivery { get; set; } = VelopackDelivery.PackOnly;

    [JsonPropertyName("source")]
    public VelopackSource? Source { get; set; }
}

public enum VelopackDelivery { PackOnly, DownloadAndPack, DownloadPackAndUpload }

public sealed class VelopackSource
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VelopackSourceType Type { get; set; }

    // Azure Blob Storage
    [JsonPropertyName("account")]
    public string? Account { get; set; }
    [JsonPropertyName("key")]
    public string? Key { get; set; }
    [JsonPropertyName("sas")]
    public string? Sas { get; set; }
    [JsonPropertyName("container")]
    public string? Container { get; set; }

    // S3
    [JsonPropertyName("keyId")]
    public string? KeyId { get; set; }
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }
    [JsonPropertyName("region")]
    public string? Region { get; set; }
    [JsonPropertyName("bucket")]
    public string? Bucket { get; set; }

    // Shared: az + s3
    [JsonPropertyName("prefix")]
    public string? Prefix { get; set; }
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    // GitHub / Gitea
    [JsonPropertyName("repoUrl")]
    public string? RepoUrl { get; set; }
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    // Local
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    // HTTP (download only)
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    // Upload options
    [JsonPropertyName("keepMaxReleases")]
    public int? KeepMaxReleases { get; set; }
}

public enum VelopackSourceType { Az, S3, GitHub, Gitea, Local, Http }

// -----------------------------------------------------------------------------
// IIS
// -----------------------------------------------------------------------------

public sealed class IisConfig
{
    /// <summary>
    /// IIS site name, e.g. "MyWeb". Required only for blue-green (secondaryDeployPath),
    /// because the site is what gets flipped between slots.
    /// </summary>
    [JsonPropertyName("siteName")]
    public string? SiteName { get; set; }

    /// <summary>
    /// Slot A. In classic (single-slot) mode this is simply the deploy destination.
    /// </summary>
    [JsonPropertyName("deployPath")]
    public string DeployPath { get; set; } = string.Empty;

    /// <summary>
    /// Slot B. Setting this enables blue-green: the artifact is mirrored into whichever
    /// slot is NOT currently live (so the app pool is never stopped during the copy),
    /// then the site's physicalPath is flipped to it.
    ///
    /// Omit for the original behavior: stop pool → mirror in place → start pool.
    ///
    /// There is deliberately no secondaryAppPool. Warmup cannot survive an app pool swap
    /// (an IIS app domain is keyed by site + app path + pool), so a second pool would buy
    /// nothing here. Preserving warmup across a cutover requires shuffling bindings
    /// between two separate sites, which is a much larger IIS setup.
    /// </summary>
    [JsonPropertyName("secondaryDeployPath")]
    public string? SecondaryDeployPath { get; set; }

    [JsonPropertyName("appPool")]
    public string AppPool { get; set; } = string.Empty;

    /// <summary>
    /// Optional URL hit immediately after the flip so the app's cold start lands on this
    /// request rather than a real user's. A non-success response is treated as a failed
    /// deploy and triggers an automatic flip back to the previous slot.
    /// </summary>
    [JsonPropertyName("warmupUrl")]
    public string? WarmupUrl { get; set; }

    /// <summary>
    /// Stop and start the IIS site as well as its app pool. Stopping the site closes its
    /// bindings, so nothing can connect at all - a stopped pool alone still accepts the
    /// connection and answers 503. Only meaningful in classic mode; blue-green never stops
    /// anything, which is the point of it. Default: false.
    /// </summary>
    [JsonPropertyName("stopSite")]
    public bool StopSite { get; set; } = false;

    /// <summary>
    /// Takes this instance out of rotation before the deploy and puts it back afterwards.
    /// Omit the block entirely and nothing changes: no notification, no wait.
    /// </summary>
    [JsonPropertyName("loadBalancer")]
    public LoadBalancerConfig? LoadBalancer { get; set; }
}

// -----------------------------------------------------------------------------
// Load balancer drain / restore
// -----------------------------------------------------------------------------

/// <summary>
/// Wraps the deploy in a drain phase and a restore phase, so a box behind a load balancer
/// stops receiving traffic before its site goes down and only receives it again once the
/// new build has warmed.
///
/// Scoped per target rather than per server on purpose: rotation state is keyed per app
/// instance, so two sites on one box drain independently and deploying one does not pull
/// the other out of rotation.
/// </summary>
public sealed class LoadBalancerConfig
{
    /// <summary>
    /// Decides whether any of this applies to the current run. Omit it and the drain/restore
    /// phases always run.
    /// </summary>
    [JsonPropertyName("precheck")]
    public LoadBalancerPrecheck? Precheck { get; set; }

    /// <summary>Runs before the app pool and site are stopped.</summary>
    [JsonPropertyName("drain")]
    public LoadBalancerPhase? Drain { get; set; }

    /// <summary>
    /// Runs after warmup succeeds. Skipped when the deploy failed and left a build that was
    /// never verified live - see the restore policy on LoadBalancerGate.
    /// </summary>
    [JsonPropertyName("restore")]
    public LoadBalancerPhase? Restore { get; set; }
}

/// <summary>
/// Decides up front whether this instance is actually in rotation, so a box that is already
/// out of it - stopped site, maintenance, a flag someone flipped by hand - deploys with the
/// plain routine instead of trying to drain something that is already drained.
///
/// This is a real correctness fix, not just an optimisation: on an offline box the health
/// endpoint is unreachable, so the drain notification would fail, and a failed drain aborts
/// the deploy by design. Without the precheck an offline server could not be updated at all.
///
/// The test is positive - the load-balancer routine runs only when the instance answers with
/// <see cref="LiveStatus"/>. Anything else, including a connection that is refused or times
/// out, means "not in rotation" and the whole block is skipped. Polling for a few seconds
/// rather than asking once keeps a momentary blip on a genuinely live box from being read as
/// "offline", which would stop the site on live traffic.
/// </summary>
public sealed class LoadBalancerPrecheck
{
    /// <summary>
    /// Health endpoint to consult. Point it at localhost: the question is whether THIS
    /// instance is serving, which a load balancer VIP cannot answer.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// Status that means "in rotation, drain me properly". Default: 200. Any other response -
    /// or none at all - skips the drain and restore phases for this run.
    /// </summary>
    [JsonPropertyName("liveStatus")]
    public int LiveStatus { get; set; } = 200;

    /// <summary>
    /// How long to keep asking before concluding the instance is not in rotation. Kept short
    /// by default: this is dead time on every deploy of an offline box. Default: 10.
    /// </summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Gap between attempts. Default: 2.</summary>
    [JsonPropertyName("intervalSeconds")]
    public int IntervalSeconds { get; set; } = 2;
}

/// <summary>
/// One half of the rotation change: tell something, optionally confirm it took, then wait.
/// Every part is optional. A phase with only waitSeconds is just a pause; a phase with only
/// a notify is fire-and-forget.
/// </summary>
public sealed class LoadBalancerPhase
{
    /// <summary>
    /// The call that changes rotation state - typically an HTTP request to the endpoint that
    /// flips this instance's health flag. Omit to only verify and/or wait.
    /// </summary>
    [JsonPropertyName("notify")]
    public NotifyHook? Notify { get; set; }

    /// <summary>
    /// Polled until it answers with <see cref="ExpectStatus"/>. Verification is skipped
    /// entirely unless both this and expectStatus are set - with nothing to check against,
    /// the phase assumes the notify worked and moves on.
    ///
    /// Point this at localhost, not at the load balancer VIP: a VIP response proves some
    /// node is healthy, not that this one is in or out of rotation.
    /// </summary>
    [JsonPropertyName("verifyUrl")]
    public string? VerifyUrl { get; set; }

    /// <summary>
    /// HTTP status verifyUrl must return before the phase proceeds - typically 500 after a
    /// drain and 200 after a restore. Null disables verification.
    /// </summary>
    [JsonPropertyName("expectStatus")]
    public int? ExpectStatus { get; set; }

    /// <summary>How long to keep polling verifyUrl before giving up. Default: 60.</summary>
    [JsonPropertyName("verifyTimeoutSeconds")]
    public int VerifyTimeoutSeconds { get; set; } = 60;

    /// <summary>Gap between verifyUrl polls. Default: 2.</summary>
    [JsonPropertyName("verifyIntervalSeconds")]
    public int VerifyIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Fixed pause after the notify (and any verification) completes.
    ///
    /// On a drain this has a floor: a health-probe based balancer only removes the node
    /// after unhealthyThreshold x probeInterval, and requests already in flight still need
    /// to finish. Set it below that sum and the site stops on live traffic, which is the
    /// outage this whole block exists to avoid. Verification confirms the flag flipped; it
    /// cannot confirm the balancer noticed, so the wait is doing real work either way.
    /// </summary>
    [JsonPropertyName("waitSeconds")]
    public int WaitSeconds { get; set; } = 0;
}

/// <summary>
/// A single side-effecting call. Which fields apply depends on <see cref="Type"/>:
///   http    - url (required), method, headers, body, expectStatus
///   file    - path (required), action
///   command - executable (required), arguments
///
/// url, path, body, executable and arguments all support %ENV_VAR% expansion for credentials
/// and the tokens {hostname}, {site} and {project}. Both are resolved on the box that runs
/// the step, never at plan time, because a peer receives this config verbatim and its
/// hostname and environment differ from the primary's.
/// </summary>
public sealed class NotifyHook
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NotifyHookType Type { get; set; } = NotifyHookType.Http;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>HTTP verb. Default: POST.</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "POST";

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>
    /// Status the call itself must return to count as successful. Null accepts any 2xx.
    /// </summary>
    [JsonPropertyName("expectStatus")]
    public int? ExpectStatus { get; set; }

    /// <summary>File to create or delete, for the "file" type.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>Whether the "file" type creates or deletes <see cref="Path"/>. Default: create.</summary>
    [JsonPropertyName("action")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FileHookAction Action { get; set; } = FileHookAction.Create;

    /// <summary>Program to run, for the "command" type.</summary>
    [JsonPropertyName("executable")]
    public string? Executable { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

    /// <summary>Applies to http and command. Default: 30.</summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 30;
}

public enum NotifyHookType { Http, File, Command }

public enum FileHookAction { Create, Delete }

// -----------------------------------------------------------------------------
// Folder
// -----------------------------------------------------------------------------

public sealed class FolderConfig
{
    [JsonPropertyName("destinationPath")]
    public string DestinationPath { get; set; } = string.Empty;

    [JsonPropertyName("serviceName")]
    public string? ServiceName { get; set; }
}

// -----------------------------------------------------------------------------
// JSON Converters
// -----------------------------------------------------------------------------

public sealed class StringOrArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String     => [reader.GetString()!],
            JsonTokenType.StartArray => JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? [],
            JsonTokenType.Null       => [],
            _                        => throw new JsonException(
                $"Expected string or array for dependentProjects, got {reader.TokenType}.")
        };

    public override void Write(
        Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
}