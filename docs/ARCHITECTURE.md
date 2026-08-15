# DeployTool - Multi-Server, Blue-Green, and Commit Directives

Design and rationale for the multi-server, blue-green, and commit-directive features. The single-box planning, blue-green, and commit-directive pieces are implemented; the peer-agent and propagation half described below is the target design.

Covers four features:

1. **Multi-server rollout** - one server builds, others receive.
2. **Side-by-side IIS** - near-zero outage via folder flip.
3. **Commit-message publish override** - `pub:Web|Proc*`.
4. **Staged rollout delay** - soak the primary before peers, `wait:` override.

---

## 1. Design principles

These are load-bearing. Everything below follows from them.

**P1 - One program, one apply path.**
The primary builds `ApplyStep[]` in memory and runs them through the *same* executor a peer
uses. The manifest is not a second contract; it is that plan, serialized. A peer runs the
primary's plan, never its own reimplementation of it.

**P2 - Role differences live in the data, not in branches.**
Which steps get *emitted* differs by server. What the executor *does* with a step never does.
If `if (isPeer)` ever appears below the apply seam, the plan was built wrong.

**P3 - The planner is pure.**
Inputs → `RolloutPlan`. No disk, no IIS, no network, no clock. This makes an entire fleet
rollout assertable in a unit test with nothing installed.

**P4 - The whole rollout is decided at one instant.**
Every server's plan is produced from one set of inputs, up front. Peer plans are never
recomputed later or derived on the peer.

**P5 - The binary that writes a manifest is the binary that executes it.**
The primary copies *itself* to each peer. Version skew is impossible by construction.

---

## 2. Architecture

```
Plan    → RolloutPlan            pure; shared
Publish → artifacts/             primary only; peers arrive with this done
Apply   → ApplyRunner            shared; identical executor, identical step objects
```

```
Program.cs  (mode switch - the ONLY role branch in the codebase)
  ├─ full mode:   Plan → Publish → Apply(self plan) → Propagate(peer plans)
  └─ apply mode:  ReadManifest → Apply(steps)

Shared below the seam:
  Planner                     → RolloutPlan
  ApplyRunner                 → executes ApplyStep[]
  IDeployTypeHandler
      .GetScope(target)       → Server | Global
      .PublishAsync(...)      → artifactDir      (full mode only)
      .ApplyAsync(artifactDir, IisConfig|FolderConfig)
  DeployReport                → result.json      (both modes, same schema)
```

The honest asymmetry list is exactly two items:

- Peers skip **Publish** (artifacts arrive pre-built).
- Peers never receive **Global**-scope steps.

Both are properties of the *plan*, not of the executor.

### Mode summary

|                        | Full mode (primary)      | Apply mode (peer)          |
| ---------------------- | ------------------------ | -------------------------- |
| Invocation             | `--config … --changed …` | `--apply <incoming-dir>`   |
| Git / changed files    | yes                      | skipped                    |
| Pre-build / unit tests | yes                      | skipped                    |
| `dotnet publish`       | yes                      | skipped - uses pre-copied  |
| Apply / swap           | yes                      | yes (the only thing it does) |
| Global steps           | yes                      | never emitted              |

---

## 3. Scope model

The question is never "is this velopack?" - it is **"is this step's destination a box, or is it
global?"**

| Target     | Destination                  | Scope      | Emitted to        |
| ---------- | ---------------------------- | ---------- | ----------------- |
| `iis`      | site + path on a box         | **Server** | every server plan |
| `folder`   | path + service on a box      | **Server** | every server plan |
| `velopack` | Azure container / pack cache | **Global** | primary only      |

```csharp
DeployScope GetScope(TargetConfig target);   // Server | Global
```

Velopack is `Global` for all three deliveries today: `downloadPackAndUpload` because its
endpoint is the Azure container (which lives in the *target* config, not `servers[]`);
`packOnly` / `downloadAndPack` because they produce nothing a server serves - `packCache` is a
build-time delta cache, not a deploy destination.

Velopack does **not** need to know the server list. It is excluded from peers because it is
`Global`, not because it knows anything about servers.

If a delivery mode ever does land bits on a specific box, change the one scope rule. The
planner does not move.

---

## 4. Config additions (`deploy-config.json`)

```jsonc
{
  "projectsFolder": "src/",
  "unitTestsFolder": "tests/",
  "unitTestProjectSuffix": "UnitTest",
  "noWarn": "…",
  "packCache": { "path": "{CommonAppData}/VelopackCache/{packId}", "keepReleases": 10 },

  // NEW - when true, nothing deploys unless the commit message carries a pub: directive
  "doNotPublishIfNoPubInCommitMessage": false,

  // NEW - fleet
  "servers": [
    {
      "name": "web1",
      "hostname": "WEBSERVER01",
      "incomingShare": "\\\\WEBSERVER01\\deploy\\incoming"
    },
    {
      "name": "web2",
      "hostname": "WEBSERVER02",
      "incomingShare": "\\\\WEBSERVER02\\deploy\\incoming",
      // OPTIONAL - per-server overrides, resolved by the primary at manifest-write time
      "overrides": {
        "WebApp": { "iis": { "deployPath": "D:\\inetpub\\Web_A",
                             "secondaryDeployPath": "D:\\inetpub\\Web_B" } }
      }
    }
  ],

  // NEW - staged rollout
  "rollout": {
    "delaySeconds": 3600,       // soak time after primary success, before peers
    "keepRuns": 5               // run folders retained per server
  },

  "projects": [ /* … unchanged … */ ]
}
```

**Server identity: hostname match.** `Environment.MachineName` is matched against each
`servers[].hostname`. Zero per-machine config. The matched entry is `self`; every other entry
is a peer. There is no `primary: true` flag - **whichever box the job lands on is the
primary**, so any server can fill either role without config changes.

If no entry matches, the tool runs exactly as it does today (single-box, no propagation) and
logs that it is unlisted.

### IIS target changes

```jsonc
"iis": {
  "siteName": "MyWeb",                          // NEW - required for blue-green
  "appPool": "MyWeb",                           // single pool - see §7
  "deployPath": "C:\\inetpub\\Web_A",            // slot A
  "secondaryDeployPath": "C:\\inetpub\\Web_B",   // NEW - omit → today's behavior
  "warmupUrl": "http://localhost/health"         // NEW, optional
}
```

There is deliberately **no `secondaryAppPool`**. See §7 for why.

---

## 5. Commit directives

Read from the **HEAD** commit message only (`git log -1 --format=%B`), parsed by the same
component that already produces the commit SHA.

### `pub:` - publish override

`pub:Web|Proc*` - pipe-separated globs matched against **deployable project names**.

| Directive       | Meaning                                     |
| --------------- | ------------------------------------------- |
| `pub:Web`       | just `Web`                                  |
| `pub:Web\|Proc*` | `Web`, plus every project starting `Proc`   |
| `pub:*`         | everything                                  |
| `pub:none`      | nothing - no project is named `none`, so it matches nothing. No special case. |

Semantics:

- `pub:` **replaces the selection step only**. The full pipeline still runs per selected
  project - pre-build, unit tests, targets. It does not skip anything.
- Patterns match project names **literally; no dependent/transitive expansion**. The directive
  is an explicit statement of intent. (Build dependencies still compile as part of publish;
  they are simply not independently deployed.)
- **Precedence:** `pub:` > `--force-all` > changed-files. The directive is the most specific
  expression of human intent, so it wins over the workflow button.

### `doNotPublishIfNoPubInCommitMessage`

| Flag    | `pub:` present | Result                          |
| ------- | -------------- | ------------------------------- |
| `false` | no             | today's changed-file resolution |
| `false` | yes            | `pub:` selection                |
| `true`  | no             | **deploy nothing**              |
| `true`  | yes            | `pub:` selection                |

### `wait:` - rollout delay override

`wait:0` (immediate), `wait:60` (60 seconds). Seconds. Overrides `rollout.delaySeconds`.

**The soak clock starts at primary success, not at plan time.** If the primary's build+test+
deploy takes 20 minutes and the delay is 1 hour, peers must fire 1 hour *after the primary went
live* - not 40 minutes after. So:

- The **plan carries `waitSeconds`** (pure, no clock - preserves P3).
- **`notBeforeUtc` is stamped at manifest-write time** = `primarySuccessTime + waitSeconds`.

---

## 6. Contracts

### 6.1 `RolloutPlan` (in-memory; also dumped to staging as an audit record)

```csharp
sealed class RolloutPlan
{
    string RunId;                 // 20260715-140312-a4235d4
    string CommitSha;
    int    WaitSeconds;           // notBefore is NOT computed here - see §5
    Selection Selection;          // chosen projects + why (changed-file reason / pub: pattern)
    List<ServerPlan> ServerPlans;
}

sealed class ServerPlan
{
    string  ServerName;
    bool    IsSelf;
    List<ApplyStep> Steps;
}
```

Dispatch in `Program`:

```
plan.ServerPlans.Single(p => p.IsSelf)  → ApplyRunner            (inline)
plan.ServerPlans.Where(p => !p.IsSelf)  → manifest + copy        (only after self succeeds)
```

### 6.2 `manifest.json` - the wire format

Fully resolved and self-describing. **`deploy-config.json` does not travel to peers** - the
manifest is the complete instruction. This removes fragile index pointers into config, makes a
peer's work auditable by reading one file, and lets each peer's manifest differ (per-server
overrides).

```jsonc
{
  "schemaVersion": 1,
  "runId": "20260715-140312-a4235d4",
  "commitSha": "a4235d43f…",
  "createdAtUtc": "2026-07-15T19:03:12Z",
  "createdBy": "web1",
  "targetServer": "web2",
  "notBeforeUtc": "2026-07-15T20:03:12Z",   // primarySuccess + waitSeconds
  "steps": [
    {
      "project": "WebApp",
      "type": "iis",
      "artifact": "artifacts/WebApp-iis",     // relative to the run folder
      "rollback": false,
      "iis": {
        "siteName": "MyWeb",
        "appPool": "MyWeb",
        "deployPath": "C:\\inetpub\\Web_A",
        "secondaryDeployPath": "C:\\inetpub\\Web_B",
        "warmupUrl": "http://localhost/health"
      }
    },
    {
      "project": "Processor",
      "type": "folder",
      "artifact": "artifacts/Processor-folder",
      "rollback": true,
      "folder": {
        "destinationPath": "C:\\Apps\\Processor",
        "serviceName": "MyProcessor"
      }
    }
  ]
}
```

A manifest never contains `Global` steps.

**`%ENV%` tokens are written unexpanded.** The peer expands them against its own environment via
the `FileHelper.ExpandEnvVars` call already inside the handler - no new code. This is both more
correct (`%ProgramData%` may differ per box) and safer (no secrets baked into a file sitting on
a share). The split:

- **Primary resolves:** per-server `overrides` from `servers[]`.
- **Peer resolves:** `%ENV%` tokens, via the handler it already runs.

### 6.3 `result.json` - just `DeployReport`, serialized

`Models/Reporting/DeployReport.cs` is already the right shape; today it is only rendered to
console. Add `WriteReportJson()` beside `WriteReport()`. **Same schema for a primary's full run
and a peer's apply run** - a peer's report simply has empty `preBuildResults` / `testResults`
and a null `publishResult`.

Two new fields on the model: `RunId`, `ServerName`.

```jsonc
{
  "runId": "20260715-140312-a4235d4",
  "serverName": "web2",
  "commitSha": "a4235d43f…",
  "startedAt": "2026-07-15T15:03:41-05:00",
  "completedAt": "2026-07-15T15:04:02-05:00",
  "duration": "00:00:21.4",
  "success": true,
  "results": [
    {
      "projectName": "WebApp",
      "success": true,
      "testsFailed": false,
      "targetResults": [
        {
          "targetLabel": "IIS / MyWeb → Web_B",
          "type": "Iis",
          "success": true,
          "rolledBack": false,
          "publishResult": null,
          "deploySteps": [
            { "stepName": "Resolve active slot",       "success": true,
              "stdout": "active=Web_A, deploying to Web_B" },
            { "stepName": "Mirror files to idle slot",  "success": true },
            { "stepName": "Flip site physicalPath",     "success": true },
            { "stepName": "Warmup /health",             "success": true }
          ]
        }
      ]
    }
  ]
}
```

`Success` and `Duration` are computed getters - they serialize out and recompute correctly from
`results` on read-back, so round-tripping works.

**Cap `StepResult.Stdout` at a few KB per step before writing JSON** so a runaway build log
cannot bloat the file.

### 6.4 Shared model boundary

**Reuse the leaf destination types. Do not reuse `TargetConfig`.**

Shared with `Models/Config`: `IisConfig`, `FolderConfig`, `DeployType`.

The justification is the handler seam - both paths converge on one method:

```csharp
Task<TargetDeployResult> ApplyAsync(string artifactDir, IisConfig iis, …);
```

Full mode passes `target.Iis`; apply mode passes `step.Iis`. Two identically-shaped types would
force a mapper whose only job is copying six properties, and every new field (`warmupUrl`,
`secondaryDeployPath`) would need adding in three places.

`TargetConfig` is where it stops: it drags `build`, `velopack`, and `prerequisites` - build-time
concerns that are meaningless on a peer. So the envelope stays manifest-specific:

```csharp
// Models/Manifest/DeployManifest.cs
sealed class DeployManifest
{
    int SchemaVersion = 1;
    string RunId, CommitSha, CreatedBy, TargetServer;
    DateTimeOffset CreatedAtUtc, NotBeforeUtc;
    List<ApplyStep> Steps;
}

sealed class ApplyStep
{
    string     Project;
    DeployType Type;          // reused enum
    string     Artifact;      // relative to run folder
    bool       Rollback;
    IisConfig?    Iis;        // ← reused from Models.Config
    FolderConfig? Folder;     // ← reused from Models.Config
}
```

**Why the usual "don't couple your wire format to internal models" objection does not apply
here:** the primary copies *itself* to the peer (P5), so writer and reader are the same binary.
Zero skew by construction.

**The one real skew vector, and why `schemaVersion` stays:** a stale run folder left in
`incoming` from an older deploy, picked up by a newer binary. Old manifest, new binary. The
tool rejects a mismatched `schemaVersion` outright rather than silently misreading it.

---

## 7. Blue-green IIS - folder flip

### Why folder flip and not a site/pool shuffle

**Warmup cannot survive an app pool swap.** An IIS app domain is keyed by site + app path +
pool. Warming the idle slot in pool B and then repointing the live site to pool B produces a
*different* app instance than the one warmed - the warmup is wasted. The only way warmup
survives a cutover is shuffling the **binding** between two fully separate sites, each with its
own permanent pool and staging binding.

| Approach                                | Outage                        | Config cost |
| --------------------------------------- | ----------------------------- | ----------- |
| Today                                   | file-copy time **+** cold start | -           |
| **Folder flip** (1 site, 1 pool, 2 dirs) | cold start only               | tiny        |
| Binding shuffle (2 sites, 2 pools)      | ~zero                         | significant |

**Decision: folder flip.** The copy lands in the *idle folder*, which nothing is serving - so
**the app pool is never stopped during the copy at all.** That deletes the entire file-copy
outage, which is the multi-minute part actually worth killing. What remains is only the app cold
start on flip.

That is also why there is no `secondaryAppPool`: a second pool buys nothing in this model.

If cold start still hurts, the fix is **IIS Application Initialization** (`preloadEnabled` +
`AlwaysRunning` pool) - a server config change, not a tool change. The binding shuffle stays in
the back pocket.

### Apply sequence

1. **Resolve active slot** - `appcmd list vdir "MyWeb/"` → read `physicalPath`. No state file.
2. **Target = the idle slot** (the one that is not live).
3. **Mirror artifacts into the idle folder** - zero outage; nothing is serving it.
4. **Flip** - `appcmd set vdir "MyWeb/" /physicalPath:"…\Web_B"`. IIS restarts the app domain
   on the config change; an explicit `recycle` is belt-and-braces.
5. **Warm** - GET `warmupUrl`. The cold start lands on the tool's request, not the first user's.
6. Old slot is untouched and becomes the new standby.

**Rollback is free:** flip back. The previous folder still holds the last-known-good build.

If `secondaryDeployPath` is absent, the handler does exactly what it does today (stop pool →
mirror → start).

---

## 8. The peer agent

### Folder layout

```
C:\deploy\
  agent\
    poll.cmd              <- the ONLY permanent file on a peer. Never changes.
    agent.log             <- rolling; poll.cmd redirects here
  staging\
    <runId>\              <- primary robocopies here (incomplete; ignored by the poller)
  incoming\
    <runId>\              <- atomically MOVED here once the copy completes
      DeployTool.exe      <- this run's freshly-built binary
      manifest.json       <- the switches (no deploy-config.json - see §6.2)
      artifacts\
        WebApp-iis\…
      result.json         <- written on completion
```

**`C:\deploy\incoming` is the only fixed path in the entire system.** The run folder is simply
where the exe lives; the tool needs no configuration to find its own payload.

### `poll.cmd` - permanent, dumb, ~6 lines

```bat
@echo off
REM Newest run folder wins - its (freshest) binary processes all pending runs.
for /f "delims=" %%d in ('dir /b /ad /o-d "C:\deploy\incoming" 2^>nul') do (
  if exist "C:\deploy\incoming\%%d\DeployTool.exe" (
    "C:\deploy\incoming\%%d\DeployTool.exe" --apply "C:\deploy\incoming" >> "C:\deploy\agent\agent.log" 2>&1
    exit /b
  )
)
```

Note what it deliberately does **not** do: parse `notBeforeUtc`. JSON in batch is misery, and
the wait logic belongs where it can be tested. The batch just launches the tool; the tool scans
`incoming`, skips runs whose `notBeforeUtc` has not passed, applies those due, writes
`result.json`, prunes old folders, and exits. **The batch file is frozen forever.**

Task Scheduler discards console output, hence the log redirect - so "waiting until 20:03" is
visible when you go looking.

### Scheduled task

| Setting                  | Value                                              |
| ------------------------ | -------------------------------------------------- |
| Name                     | `DeployAgent`                                    |
| Trigger                  | every 1 minute, indefinitely                        |
| Action                   | `C:\deploy\agent\poll.cmd`                          |
| Run as                   | `SYSTEM` (needs IIS + service control rights)       |
| MultipleInstancesPolicy  | **IgnoreNew** - built-in guard for deploys > 1 min  |

`IgnoreNew` removes any need for a lock file for task-vs-task overlap.

### `--install-agent`

One-time per box: creates `C:\deploy\{agent,staging,incoming}`, writes `poll.cmd`, registers the
scheduled task. Standing up a new server becomes:

1. Run `DeployTool.exe --install-agent` once.
2. Add the box to `servers[]`.

Because the tool ships self-contained (§10), the peer needs **no .NET prerequisite** for this to
work on a fresh box.

### `result.json` is the idempotency marker

This matters more than it looks. The poller fires **every minute, forever**. Without a
completion marker it would re-apply a successful run on a loop.

> **Rule: manifest present + no `result.json` = pending.**

One file provides idempotency, the audit trail, and the failure record.

---

## 9. Propagation sequence

Runs on the primary, **only after its own apply succeeds**.

1. **Stamp** `notBeforeUtc = now + waitSeconds` (now = primary success time - §5).
2. For each peer plan:
   a. Serialize `ServerPlan.Steps` → `manifest.json`.
   b. `robocopy` artifacts + `File.Copy(Environment.ProcessPath, …)` → `\\PEER\deploy\staging\<runId>\`.
   c. **Move** `staging\<runId>` → `incoming\<runId>`.
3. Prune peer run folders beyond `rollout.keepRuns`.
4. Exit. **The runner is not held for the soak window.**

**Atomic handoff.** A move within the same share is atomic on NTFS, so the poller can never
observe a half-copied run folder. This is why the primary stages first rather than copying
straight into `incoming` (manifest-written-last is the weaker fallback).

**The primary never writes into its own `incoming`.** It publishes to `staging\<runId>\` and
applies inline from there. Otherwise its own poller could pick up the run folder mid-apply and
execute it concurrently - Task Scheduler's `IgnoreNew` guards task-vs-task, not
task-vs-primary-process. Keeping the primary out of `incoming` removes the race entirely, and
any box can still be either role (role is decided by hostname at plan time, not by what is
installed).

**Failure semantics fall out for free.** Manifests are only written after the primary succeeds -
so if the primary fails, no peer ever receives anything. There is no guard to forget. The full
`RolloutPlan` is still dumped to the primary's staging folder even on failure, as an audit
record of what *would* have shipped.

### Round-trip the primary's own steps

The primary writes its own `manifest.json` to staging, **deserializes it back, and executes from
that copy.**

Not for purity - for the failure mode. The executor cannot diverge (same code, same objects).
The only thing that *can* silently break is serialization fidelity. Executing from in-memory
objects would hide a serialization bug until web2 tries to apply it an hour later, runner gone,
nobody watching. Round-tripping locally turns that into an immediate, loud failure on the
primary before any peer receives the file - and means **the remote path is exercised on every
single deploy**, even when there are no peers.

---

## 10. Shipping the tool itself

The primary copies **itself** to peers, so the running process *is* the artifact:

```csharp
File.Copy(Environment.ProcessPath, dest);   // one ~70 MB file
```

Use `Environment.ProcessPath`, **not** `AppContext.BaseDirectory` - the latter is unreliable for
single-file apps.

### Workflow change (`.github/workflows/deploy.yml`)

Replace the build step with a publish, and run the exe directly instead of `dotnet run`:

```yaml
- name: Publish DeployTool (self-contained, single-file)
  shell: powershell
  run: |
    dotnet publish src/DeployTool/DeployTool.csproj `
      -c Release -r win-x64 --self-contained true `
      -p:PublishSingleFile=true `
      -o src/DeployTool/_dist --nologo

- name: Run DeployTool
  shell: powershell
  env:
    AZ_ACCOUNT: ${{ secrets.AZ_ACCOUNT }}
    AZ_KEY: ${{ secrets.AZ_KEY }}
  run: |
    src\DeployTool\_dist\DeployTool.exe `
      --config deploy-config.json `
      --changed "${{ steps.changed_files.outputs.files }}" `
      --force-all "${{ inputs.force_all }}"
```

Dropping `dotnet run --no-build` also removes the `--` argument-forwarding quirk.
`--config deploy-config.json` still resolves - Actions sets the working directory to the
workspace.

### `DeployTool.csproj`

- **Keep the RID out of the csproj.** Development happens on macOS; a hard-coded
  `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` would break local `dotnet run` and the
  `#if DEBUG` path in `Program.cs`. Pass `-r win-x64 --self-contained true` on the CLI in the
  workflow only; the csproj stays platform-neutral.
- **Fix the existing contradiction.** The csproj currently reads:
  ```xml
  <!-- Self-contained so it runs regardless of installed SDK version on the runner -->
  <SelfContained>false</SelfContained>
  ```
  The comment states the opposite of the setting. The CLI flag overrides it, but the property
  should be deleted and the comment corrected so nobody trusts it later.

**Cost:** self-contained single-file publish is ~30–60 s vs ~10 s for `dotnet build`, on every
deploy, even with no peers. Next to the test suite this is noise - and it buys peers with zero
.NET prerequisites, which is what makes `--install-agent` a genuine one-liner on a fresh box.

---

## 11. Required refactor (foundation)

Today each handler does publish **and** swap in one method
(`IisHandler.DeployAsync`, `FolderHandler.DeployAsync`). Split each into:

```csharp
Task<StepResult>          PublishAsync(project, build, outputDir, noWarn);
Task<TargetDeployResult>  ApplyAsync(artifactDir, IisConfig|FolderConfig, rollback);
DeployScope               GetScope(TargetConfig target);
```

Full mode runs both. Apply mode runs only `ApplyAsync`. This split is the enabler for
everything else, and it is what makes blue-green clean.

---

## 12. Build order

Each phase is independently shippable and leaves the tool working.

| Phase | Work                                                        | Notes                                    |
| ----- | ----------------------------------------------------------- | ---------------------------------------- |
| **0** | Handler publish/apply split + `GetScope`                     | No behavior change. Unblocks everything. |
| **1** | Blue-green IIS (`siteName`, `secondaryDeployPath`, `warmupUrl`) | Independent. Ships alone. Immediate win. |
| **2** | `pub:` / `wait:` parsing + `doNotPublishIfNoPubInCommitMessage` | Small, independent, pure - easy to test. |
| **3** | Planner → `RolloutPlan`; `result.json` writer                | Pure. Fully unit-testable.               |
| **4** | Apply mode + manifest + `--install-agent` + `poll.cmd`       | The peer half.                           |
| **5** | Propagation (copy self + artifacts, atomic move, pruning)    | Uses 3 & 4.                              |

Phases 1 and 2 deliver standalone value before any multi-server work exists.

---

## 13. Edge cases and how they are handled

| Case                                        | Handling                                                          |
| ------------------------------------------- | ----------------------------------------------------------------- |
| Poller sees a half-copied run folder        | Staging + atomic move (§9)                                         |
| Poller re-applies a finished run every minute | `result.json` presence = done (§8)                                 |
| Deploy outlasts the 1-minute poll interval  | Task Scheduler `MultipleInstancesPolicy = IgnoreNew`               |
| Primary's poller grabs the primary's own run | Primary uses `staging`, never `incoming` (§9)                      |
| Stale run folder, newer binary              | `schemaVersion` mismatch → rejected (§6.4)                         |
| Primary fails                               | No manifests written → peers never receive it (§9)                 |
| Running exe blocks folder pruning           | Each run has its own folder; pruned on a later run (§8)            |
| Host not in `servers[]`                     | Runs as today, single-box, no propagation; logs that it is unlisted |
| `%ENV%` differs per box                     | Written unexpanded; peer expands via existing handler code (§6.2)  |
| Soak window vs. runner occupancy            | `notBeforeUtc` in the manifest; primary exits immediately (§9)     |

---

## 14. Open item

**Peer failure policy.** If web2's apply fails an hour after the primary's run has ended, the
GitHub runner is long gone. Options:

- **(a) Alert only** - `result.json` records the failure; the next run surfaces it.
- **(b) Auto flip-back** - the peer flips its IIS site back to the previous slot on failure.

Blue-green makes (b) cheap: the previous folder still holds the last-known-good build, so
flip-back is one `appcmd` call. Leaning **(b) for IIS steps + (a) as the notification path**,
but this is deferrable - it does not affect any contract above.

There is currently no notification channel (the tool reports to console/GitHub Actions only).
If (a) needs to reach a human, that is a separate feature.
