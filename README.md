# DeployTool

[![CI](https://github.com/dyzenment/DeployTool/actions/workflows/ci.yml/badge.svg)](https://github.com/dyzenment/DeployTool/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Dytools.DeployTool.svg)](https://www.nuget.org/packages/Dytools.DeployTool)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://github.com/dyzenment/DeployTool/blob/main/LICENSE)

A tool that deploys your .NET applications to your own servers, driven entirely by one
`deploy-config.json` - no per-project deploy scripts.

It runs as a step in your CI pipeline (typically a **self-hosted GitHub Actions runner**): given
the files a push changed, it works out which apps are affected, tests them, builds them, and rolls
them out to **IIS** sites, Windows **services**/folders, or **Velopack** feeds - across one server
or a fleet - with blue-green cutover, staged soak delays, rollback, and a written run report.

## What it solves

Deploying several .NET apps from one repo to on-prem Windows servers is usually a pile of bespoke
PowerShell - one script per app, each reimplementing the same chores and drifting over time:

- **Figuring out what to deploy.** Which apps did this commit actually affect, including changes to
  shared libraries they depend on?
- **The mechanics.** Stop the app pool or Windows service, copy the files, start it back up - and
  don't leave it down any longer than necessary.
- **Doing it safely.** Gate on unit tests, warm the app before real traffic hits it, and roll back
  when a deploy fails.
- **Doing it across servers.** Push the same build to several boxes without taking them all down at
  once.

DeployTool folds all of that into **one config-driven tool**. Change detection, dependency-aware
selection, test gating, publish, IIS/folder/Velopack delivery, blue-green cutover, multi-server
rollout, rollback, and reporting are built in. Your CI workflow shrinks to "restore the tool, run
the tool," and adding an app or a server becomes a config edit - not another script to maintain.

Distributed as a [.NET tool](https://learn.microsoft.com/dotnet/core/tools/global-tools) on NuGet
([`Dytools.DeployTool`](https://www.nuget.org/packages/Dytools.DeployTool)); the command is
`dytools-deploy`.

## How it works

1. A push (or a manual run) hands the tool the list of **changed files**.
2. It maps those to **affected projects** via the `.csproj` dependency graph, plus any extra
   triggers you declare.
3. It runs each project's **unit tests** - a failure aborts that project.
4. It **publishes and delivers** each target: IIS (optionally blue-green), a folder (stopping and
   starting a Windows service around the copy), or a Velopack package.
5. On a **fleet**, the box running the job deploys first, then hands every peer a complete run
   folder - manifest, artifacts, and the tool itself - staged and moved into place atomically.
   Peers receive nothing unless the primary succeeded. Each peer's own agent picks the folder up,
   waits out the soak window, and applies it. See [Setting up a peer](#setting-up-a-peer).
6. It writes a **run report**.

Commit-message directives (`pub:`, `wait:`) and command-line overrides (`--pub`, `--wait`) steer
any individual run.

## Getting started (GitHub Actions)

DeployTool is designed to run from a **self-hosted GitHub Actions runner** - IIS and folder targets
need network access to your on-prem servers. A ready-to-use workflow ships at
[`examples/deploy.yml`](examples/deploy.yml). In your **application** repo:

**1. Add the tool** as a pinned local tool:

```bash
dotnet new tool-manifest          # creates .config/dotnet-tools.json
dotnet tool install Dytools.DeployTool
```

Commit `.config/dotnet-tools.json`; `dotnet tool restore` brings it back on the runner.

**2. Add `deploy-config.json`** at the repo root. Start from
[`examples/deploy-config.example.json`](examples/deploy-config.example.json) (or scaffold one with
[`dytools-deploy init`](#scaffolding-and-editing-a-config)) and keep the `$schema` line for editor
autocomplete and validation - full field docs in the [Configuration reference](#configuration-reference).

**3. Copy the workflow** to `.github/workflows/deploy.yml` (from
[`examples/deploy.yml`](examples/deploy.yml)). It triggers on push to `main` and on manual dispatch
(with a "deploy all" switch), computes the changed files, then runs `dotnet tool restore` +
`dytools-deploy`.

**4. Add any secrets** your config references. If `deploy-config.json` uses `%AZ_KEY%`, add an
`AZ_KEY` repository secret and pass it as `env` on the Deploy step:

```yaml
      - name: Deploy
        env:
          AZ_ACCOUNT: ${{ secrets.AZ_ACCOUNT }}
          AZ_KEY: ${{ secrets.AZ_KEY }}
        run: |
          dotnet dytools-deploy `
            --config deploy-config.json `
            --changed "${{ steps.changed_files.outputs.files }}" `
            --force-all "${{ inputs.force_all }}"
```

The workflow stays thin - .NET setup, git, a `HEAD~1` changed-file diff, then restore and run.
Everything else (resolution, build, test, publish, rollout, rollback, reporting) happens inside the
tool.

> **Runner:** a **self-hosted** runner is required for IIS/folder targets. A cloud runner works only
> if every target is cloud-reachable (e.g. Velopack to Azure/S3).

## Running it by hand

You can also run it locally - handy for a one-off deploy or to test a config. Install it globally:

```bash
dotnet tool install -g Dytools.DeployTool
```

Then select projects **by name** with `--pub`, independent of git - this is what makes a manual
publish clean, with no commit dance:

```bash
# publish specific projects:
dytools-deploy --config deploy-config.json --changed "" --pub "WebApp|Proc*"
# or everything, applying to peers immediately:
dytools-deploy --config deploy-config.json --changed "" --pub "*" --wait 0
```

## Command-line reference

```bash
dytools-deploy --config deploy-config.json --changed "src/App/Foo.cs|src/Lib/Bar.cs"
```

| Argument | Description |
|----------|-------------|
| `--config <path>` | Path to `deploy-config.json`. Its directory is treated as the repo root. **Required.** |
| `--changed "<a\|b\|...>"` | Pipe-delimited list of changed file paths. Empty deploys nothing (unless a `pub:`/`--pub` or `--force-all` selects projects). |
| `--force-all <true\|false>` | Deploy every enabled project regardless of changed files. |
| `--pub "<patterns>"` | Override the commit's `pub:` - pipe-separated name globs (`"Web\|Proc*"`, `"*"`, `"none"`). Selects projects by name regardless of `--changed`. |
| `--wait <seconds>` | Override the commit's `wait:` rollout soak delay. `0` = peers apply immediately. |
| `--skip-tests [true\|false]` | Bypass the unit-test gate. Usable bare (`--skip-tests`). Overrides the commit's `skiptests`; pass `false` to force the gate on for a commit that asked to skip it. |
| `--server <name>` | Pin which `servers[]` entry this box is, instead of matching on machine name. See [Which server am I?](#which-server-am-i). |

The tool exits `0` on success, `1` on failure. Which projects deploy is decided from the
changed-file list against each project's folder, its `.csproj` `ProjectReference`s (resolved
automatically), and any extra `dependentProjects` triggers. Directives `pub:<pattern>`,
`wait:<seconds>` and `skiptests` are read from the `HEAD` commit; `--pub` / `--wait` /
`--skip-tests` override them field by field (see [Running it by hand](#running-it-by-hand)).

### Other commands

| Command | Description |
|---------|-------------|
| `dytools-deploy init [path]` | Scaffold a `deploy-config.json` interactively. |
| `dytools-deploy edit <path>` | Edit an existing one. |
| `dytools-deploy hostname [--config <path>]` | Print every name this box is known by, and which `servers[]` entry it matches. |
| `dytools-deploy install-agent` | Stand this box up as a peer: folder layout, poll script, scheduled task. |
| `dytools-deploy uninstall-agent` | Remove the schedule. Folders and deploy history are left alone. |
| `dytools-deploy apply [incoming]` | Apply whatever is due in an incoming folder. Run by the agent; usually not by hand. |
| `dytools-deploy help` | Usage summary. |

## Minimal config

```jsonc
{
  "projectsFolder": "src",
  "unitTestsFolder": "tests",
  "unitTestProjectSuffix": "UnitTest",
  "projects": [
    {
      "name": "WebApp",
      "targets": [
        {
          "type": "Iis",
          "build": { "configuration": "Release", "runtime": "win-x64" },
          "iis": {
            "siteName": "MyWeb",
            "deployPath": "C:\\inetpub\\MyWeb",
            "appPool": "MyWebPool"
          }
        }
      ]
    }
  ]
}
```

Add a `servers` array (matched by hostname) and a `rollout` block to turn on multi-server
rollout. The full field-by-field reference is below; see
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the rollout model and design rationale.

## Configuration reference

`deploy-config.json` is deserialized into [`DeployConfig`](src/Dytools.DeployTool/Models/Config/DeployConfig.cs).
All paths are **repo-root-relative** (the repo root is the directory containing the config file)
unless noted. String values support `%ENV_VAR%` environment-variable expansion - the intended way
to keep secrets (e.g. storage keys) out of the file; set them as env vars on the run.

### Editor support (JSON Schema)

The repo ships a generated [`deploy-config.schema.json`](deploy-config.schema.json). Add a
`$schema` key to the top of your config and Rider / VS Code will give you autocomplete,
enum dropdowns, required-field and type validation, and hover documentation for every field:

```jsonc
{
  "$schema": "https://raw.githubusercontent.com/dyzenment/DeployTool/main/deploy-config.schema.json",
  "projectsFolder": "src",
  "projects": []
}
```

A complete, schema-bound sample lives at
[`examples/deploy-config.example.json`](examples/deploy-config.example.json).

The schema is generated from the C# models (so it can't drift) by
[`tools/Dytools.SchemaGen`](tools/Dytools.SchemaGen) - regenerate it after changing the config models with:

```bash
dotnet run --project tools/Dytools.SchemaGen
```

### Scaffolding and editing a config

`dytools-deploy init` builds a `deploy-config.json` and drops you into a small line-by-line menu
editor (navigate with the shown letter/number keys; `R#` removes an item; `S` saves). It first
offers to **scan your solution** and pre-fill projects, their kinds, deployments, and test wiring -
so a repo with dozens of projects opens mostly filled in, leaving mainly the deploy destinations.

```bash
dytools-deploy init                            # ask to scan, then open the editor
dytools-deploy init --auto                     # scan the solution in this folder (--scan is an alias)
dytools-deploy init --solution My.sln          # scan a specific solution (implies scan)
dytools-deploy init --all-csproj               # discover every .csproj instead of reading a solution
dytools-deploy init --save [file]              # write the scanned/blank config as-is, skip the editor
dytools-deploy edit deploy-config.json         # open an existing config in the editor
```

Auto-scan classifies each project from its `.csproj` (web → IIS; a project referencing Velopack →
Velopack; other console/desktop → folder; libraries and test projects are excluded) and infers
`projectsFolder`, `unitTestsFolder`, and the test-project suffix. Deploy
destinations (paths, app pools, servers) aren't in the source, so they come in as placeholders to
fill. With no solution present, `--auto` exits. Everything is built from the real `DeployConfig`
models, so the output is always valid, and each write prints the file's full path.

### Root object

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `projectsFolder` | string | **required** | Folder holding each deployable project. A project named `X` must live at `{projectsFolder}/X/X.csproj`. |
| `unitTestsFolder` | string | `null` | Folder holding unit-test projects, for convention-based test discovery. |
| `unitTestProjectSuffix` | string | `null` | Suffix appended to a project name to find its test project. `"UnitTest"` → project `Admin` → `{unitTestsFolder}/AdminUnitTest/AdminUnitTest.csproj`. |
| `noWarn` | string | `null` | Repo-wide warning codes suppressed on every build/test, comma-separated (`"CS8600,CS8618"`). Concatenated with per-project and per-build `noWarn`. |
| `packCache` | object | `null` | Velopack release-history cache (required for delta packages). See [Pack cache](#pack-cache). |
| `doNotPublishIfNoPubInCommitMessage` | bool | `false` | When `true`, a commit with no `pub:` directive deploys nothing - publishing becomes opt-in per commit. `--force-all` and `--pub` still override. |
| `servers` | array | `[]` | The server fleet. Empty = single-box (no propagation). See [Server](#server). |
| `rollout` | object | `null` | Peer soak-delay and retention. See [Rollout](#rollout). |
| `projects` | array | `[]` | The deployable projects. See [Project](#project). |

### Server

Multi-server rollout. There is no "primary" flag - whichever box the job runs on is the primary
for that run (matched by `hostname`), and every other listed server is a peer.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `name` | string | `""` | Short label used in logs, manifests, and reports. |
| `hostname` | string | `""` | Matched against the running box's name to decide which entry it is. See [Which server am I?](#which-server-am-i). |
| `incomingShare` | string | `null` | UNC path where the primary drops run folders for this peer, e.g. `\\WEB02\deploy\incoming`. Unused on whichever box is currently the primary (it applies inline). |
| `username` | string | `null` | Account the primary authenticates to this peer's share with. Leave unset on a domain, or with mirrored local accounts. See [Reaching a peer's share](#reaching-a-peers-share). |
| `password` | string | `null` | Password for `username`. Write it as `"%DEPLOY_SHARE_PASSWORD%"` - never a literal. |

`incomingShare` must be a **subfolder of a share, not the share root**: the primary copies into a
`staging` folder beside it (`\\WEB02\deploy\staging\<runId>`) and then *moves* the finished
folder into `incoming`. Within one share that move is a rename, and therefore atomic - which is
what stops a peer ever seeing a half-copied run. Point `incomingShare` at a share root and there
is nowhere to stage beside it, so the run fails saying so rather than quietly degrading to a
non-atomic copy.

The account the deploy runs as needs write access to that share, and the primary needs to reach
port 445 on the peer.

A delivered run folder is entirely self-describing - `deploy-config.json` never travels:

```
\\WEB02\deploy\incoming\20260819-154000-f419e13\
  manifest.json          <- this peer's steps, plus notBeforeUtc
  DeployTool.exe         <- the binary that planned the run, so peer and primary cannot skew
  artifacts\
    Web-folder\...       <- only what this peer's own steps reference
```

Delivery happens **only after the primary's own apply succeeded**, so a broken build never
reaches the fleet. A peer that cannot be written to fails that peer alone and fails the run;
the others are still delivered.

### Rollout

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `delaySeconds` | int | `3600` | Soak time between the primary going live and peers applying. Clock starts at **primary success**. Overridden per run by `wait:` / `--wait`. |
| `keepRuns` | int | `5` | Run folders retained per peer before the oldest are pruned. |

### Which server am I?

Every box works out which `servers[]` entry it is on its own - there is no primary flag and no
per-machine config file. The name it identifies as comes from the first of these that is set:

| Source | When to use it |
|--------|----------------|
| `--server <name>` | One run, by hand. Also handy for testing a config against another box's identity. |
| `DEPLOYTOOL_SERVER` env var | Set once on a box the fleet calls something other than what Windows does - a renamed host, a container, an image cloned from a template. |
| Machine name | The default, and the zero-configuration case. |

That name is then matched against `servers[]` in three passes, strictest first: exact `hostname`,
exact `name`, then short name (so `WEB01` and `web01.corp.local` are the same box, either way
round). All comparisons are case-insensitive. If two entries claim the same box the run stops
rather than guessing.

**A box that matches nothing still deploys itself perfectly well - it just propagates to nobody,
and a green run says nothing about it.** That silence is the reason for:

```bash
dytools-deploy hostname --config deploy-config.json
```

It prints every name the box is known by, which entry it matched and why, and - when nothing
matched - the entry to paste in. It exits `1` on no match, so a fleet check can be scripted.

### Setting up a peer

Two steps per box. On the peer, in an **elevated** shell:

```bash
dytools-deploy install-agent
```

That creates `C:\deploy\{agent,staging,incoming}`, writes the poll script, and registers a
`DeployAgent` scheduled task that runs it every minute as `SYSTEM`. It is idempotent - run it
again to upgrade a box, and existing run folders are left alone. It finishes by printing the
`servers[]` entry to paste into your `deploy-config.json`, which is step two.

| Switch | Default | Description |
|--------|---------|-------------|
| `--root <path>` | `C:\deploy` (Windows), `/var/lib/deploytool` | Agent root. `incoming` and `staging` must stay siblings - the handoff relies on it. |
| `--interval <minutes>` | `1` | How often the task polls. |
| `--task-name <name>` | `DeployAgent` | Scheduled task name. |
| `--user <account>` | `SYSTEM` | Who the scheduled **task** runs as: `SYSTEM`, `LOCALSERVICE` or `NETWORKSERVICE`. Anything needing a password is printed as a command for you to run. |
| `--account <name>` | `deploysvc` | Local account the **primary** connects as. Install offers to create it and grant it the share. |
| `--no-prompt` | off | Skip that offer and print the commands instead. Implied when stdin is not a console. |

Then share the root so the primary can reach `\\PEER\deploy\incoming`, and give the primary's
account write access to it - **both** the share permission and the NTFS permission. This is the
usual reason a first rollout fails.

**What actually happens on a peer.** The primary copies the run into `staging\<runId>` and then
*moves* it into `incoming\<runId>`; a move within one share is atomic, so the poller can never
see a half-copied run. Every minute the task runs `poll.cmd`, which finds the newest run folder
and launches **the tool that arrived inside it** - the peer has no installed copy to keep in step
with the primary. That tool scans `incoming`, skips runs whose soak window has not passed, applies
the rest oldest-first, and writes a `result.json` into each.

That `result.json` is the completion marker as well as the audit record: manifest present and no
`result.json` means pending. It is written on failure too, so a broken run is recorded once rather
than retried every minute forever. Watch it all in `C:\deploy\agent\agent.log`.

The peer needs the .NET runtime installed - it does not need the SDK, a checkout, or a
`deploy-config.json`. Everything it is meant to do arrives in the run folder's `manifest.json`.

On Linux or macOS `install-agent` creates the same layout and a `poll.sh`, then prints the crontab
line for you to add - it will not edit a server's crontab behind your back.

### Reaching a peer's share

`install-agent` prints all of this for the box you run it on, with the real names filled in.
Summarised here for reference.

**Share the root, not `incoming`.** The primary copies into `staging\<runId>` and then *moves* it
into `incoming\<runId>` - the move is what makes the handoff atomic - and prunes old runs
afterwards. It needs write and delete on both folders:

```powershell
net share deploy=C:\deploy /grant:deploysvc,CHANGE
icacls C:\deploy /grant "deploysvc:(OI)(CI)M"
```

Both commands matter: the SMB share permission and the NTFS permission are separate gates and the
stricter one wins.

**Then give the primary an identity the peer accepts.** On a domain, grant the runner's account and
you are done. In a **workgroup there is no shared identity**, and a self-hosted Actions runner
installs as a service running as `NETWORK SERVICE` - which authenticates over the network as the
*machine account* (`WEB01$`), a name a peer with no domain controller cannot resolve. Granting it
on the share will not help. Two ways out:

| | Option A - credentials in config | Option B - mirrored local account |
|---|---|---|
| Peer | create `deploysvc`, grant it the share | create `deploysvc`, grant it the share |
| Primary | nothing | create the **same** username and password, repoint the runner service at it |
| Config | `username` + `password` | nothing |
| Cost | a secret to manage | reconfiguring the runner, and two passwords to keep in step forever |

> **`install-agent` offers to do the peer half for you.** Run elevated, answer `y`, and type a
> password at the prompt: it creates the account, shares the folder, and grants both permissions.
> The password is read without echo and passed to the Win32 `NetUserAdd` API rather than to
> `net user <name> <password> /add` - a password on a command line is readable by any user on the
> box via `Win32_Process` for as long as the process lives. It skips the offer and prints the
> commands instead when stdin is not a console (CI) or you pass `--no-prompt`; `--account <name>`
> changes the name from `deploysvc`.
>
> The commands below are what it runs, for when you would rather do it yourself.

#### Option A - credentials in config

On the **peer only**, elevated:

```powershell
New-LocalUser -Name deploysvc -PasswordNeverExpires -Description "DeployTool share access" `
  -Password (Read-Host -AsSecureString "Password for deploysvc")
net share deploy=C:\deploy /grant:deploysvc,CHANGE
icacls C:\deploy /grant "deploysvc:(OI)(CI)M"
```

`Read-Host -AsSecureString` keeps the password out of your shell history. The quotes on `icacls`
are required in PowerShell - unquoted, it evaluates `(OI)` as a command.

The account needs nothing beyond the share - it never logs on interactively, so it does not
need to be an administrator and should not be one. Then in `deploy-config.json`:

```jsonc
{
  "name": "web02",
  "hostname": "EC2AMAZ-FFQRJ6U",
  "incomingShare": "\\\\10.0.1.20\\deploy\\incoming",
  "username": "EC2AMAZ-FFQRJ6U\\deploysvc",
  "password": "%DEPLOY_SHARE_PASSWORD%"
}
```

Nothing changes on the primary.

#### Option B - mirrored local account

Create the **same username with the same password** on both boxes:

```powershell
New-LocalUser -Name deploysvc -PasswordNeverExpires `
  -Password (Read-Host -AsSecureString "Password for deploysvc")
```

Grant it the share on the peer (the `net share` / `icacls` pair above), then on the primary
point the runner service at it: **Services → `actions.runner.*` → Log On → `.\deploysvc`**, and
restart. The Log On tab grants "Log on as a service" for you. Nothing goes in the config -
workgroup pass-through does the rest: the peer validates the incoming credentials against its
own SAM and lets it in.

#### How the password actually reaches the tool

Ordinary environment inheritance - there is no secret store and nothing is written to disk:

```yaml
      - name: Deploy
        env:
          DEPLOY_SHARE_PASSWORD: ${{ secrets.DEPLOY_SHARE_PASSWORD }}
        run: |
          dotnet dytools-deploy --config deploy-config.json --changed "$CHANGED_FILES"
```

Actions sets that variable on the step's shell, `dytools-deploy` is a child process and inherits
it, and `%DEPLOY_SHARE_PASSWORD%` in the config is expanded against the process environment at the
moment the connection is opened. The same `%VAR%` mechanism every other secret in the config uses -
`AZ_KEY` and friends - and it works identically on Windows and Linux runners.

Two consequences worth knowing. Actions masks the secret in job logs, so even an accidental echo
comes out as `***`. And because child processes inherit the environment, the variable is also
visible to the build and IIS commands the tool spawns - unavoidable with env-var passing, and the
reason this account should be scoped to the share and nothing else.

The tool opens a deviceless session to the share for the length of the handoff and drops it after -
no drive letters, nothing left mapped. A bare username is qualified with the peer's name
automatically, *except* when the share is addressed by IP, where a bare name is what Windows
accepts. The password is never logged, never written to `result.json`, and never reaches a
manifest; `result.json` records only which username connected.

A literal password warns on every run, and an environment reference that resolves to nothing is a
hard error rather than a mystifying logon failure.

> Credentials are a Windows facility. A Linux primary ignores them with a warning - mount the share
> with its credentials before running the deploy.

**Two things that bite on cloud VMs.** TCP 445 must be open from the primary (a security-group or
NSG rule, not just the Windows firewall); and a Windows computer name only resolves by broadcast
within one subnet, so across VPC subnets put the **private IP** in `incomingShare`. `hostname` still
stays the computer name - it is how the peer recognises itself locally, and the two fields are
independent.

Verify **as the runner's account**, not your own login - that is the mistake that makes this look
fixed when it is not:

```bat
psexec -u .\deploysvc -p "<password>" cmd /c "dir \\10.0.1.20\deploy\incoming"
```

### Pack cache

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `path` | string | `""` | Cache directory. Tokens: `{CommonAppData}` (always-writable local app data, no elevation), `{packId}`, and `%ENV_VAR%`. |
| `keepReleases` | int | `null` | Max historical releases retained per `packId`. |

### Project

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `name` | string | **required** | Must match the subfolder and `.csproj` filename under `projectsFolder`. |
| `disabled` | bool | `false` | Excluded from all runs without removing it; skips the existence check. |
| `runTests` | bool | `true` | When `false`, unit tests are skipped for this project. |
| `abortOnUnitTestFailure` | bool | `true` | When `false`, test failures are logged but deployment continues. |
| `unitTestProject` | string | convention | Override: a test project **name** (`"AdminUnitTest"`) or a repo-root-relative `.csproj` **path**. |
| `dependentProjects` | string \| string[] | `[]` | Extra change triggers beyond this project's own folder. `"Name"` → triggers on changes in that project's folder; `"some/path"` → substring match against changed paths; `"*"` → any solution-level change (outside all project folders). `.csproj` `ProjectReference`s are resolved automatically - no need to list them. |
| `preBuild` | string[] | auto | Shell commands run in the project folder before targets build. If omitted and `package.json` exists, `npm install` runs automatically. |
| `noWarn` | string | `null` | Per-project warning suppressions; concatenated with the root `noWarn`. |
| `targets` | array | `[]` | Deploy targets for this project. See [Target](#target). |

### Target

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `type` | enum | **required** | `Iis`, `Folder`, or `Velopack`. Selects which type-specific block below is required. |
| `prerequisites` | string[] | `[]` | Software auto-installed if missing, only for selected targets. Supported: `nodejs`, `vpk`. |
| `build` | object | `null` | Publish/build settings. See [Build](#build). |
| `rollback` | bool | `false` | Snapshot the destination before deploying and restore it on failure. |
| `iis` | object | - | Required when `type: Iis`. See [IIS target](#iis-target). |
| `folder` | object | - | Required when `type: Folder`. See [Folder target](#folder-target). |
| `velopack` | object | - | Required when `type: Velopack`. See [Velopack target](#velopack-target). |

### Build

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `configuration` | string | `"Release"` | dotnet build configuration. |
| `runtime` | string | `null` | Runtime identifier (RID), e.g. `win-x64`. |
| `selfContained` | bool | `null` | Self-contained publish (bundle the runtime). |
| `targetFramework` | string | inferred | Target framework moniker to build. Required for multi-target projects. A .NET Framework TFM (e.g. `net481`) switches the build to `msbuild` instead of `dotnet publish`. |
| `singleFile` | bool | `null` | Publish as a single file. |
| `noWarn` | string | `null` | Comma-separated warning codes, passed as `/nowarn:`. |

### IIS target

Classic mode: stop app pool → mirror artifact into `deployPath` → start pool.
Set `secondaryDeployPath` to enable **blue-green** instead (no pool stop during copy).
Set `loadBalancer` to drain the instance out of rotation around either of them.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `deployPath` | string | `""` | Deploy destination (slot A). In classic mode this is simply the target folder. |
| `appPool` | string | `""` | App pool name. |
| `siteName` | string | `null` | IIS site name. Required only for blue-green (the site is what gets flipped). |
| `secondaryDeployPath` | string | `null` | Slot B. Setting it turns on blue-green: the artifact is mirrored into whichever slot is **not** live, then the site's physical path is flipped to it. |
| `warmupUrl` | string | `null` | URL hit immediately after the flip so cold start lands here, not on a real user. A non-success response fails the deploy and flips back to the previous slot. |
| `stopSite` | bool | `false` | Stop and start the site as well as the pool. A stopped pool still accepts connections and answers 503; a stopped site closes its bindings entirely. Classic mode only. |
| `loadBalancer` | object | `null` | Drain this instance out of rotation before the deploy and put it back after warmup. See below. |

#### Behind a load balancer

Omit `loadBalancer` and nothing changes. With it, a classic deploy runs:

```
precheck  ── not in rotation ─→ plain routine, no drain, no restore
    │ in rotation
    ↓
drain notify → drain verify → drain wait
  → stop site → stop pool → snapshot → mirror → start pool → start site → warmup
→ restore notify → restore verify → restore wait
```

```jsonc
"loadBalancer": {
  "precheck": { "url": "http://localhost/health", "liveStatus": 200 },
  "drain": {
    "notify":       { "type": "http", "url": "http://localhost/admin/health?node={hostname}&site={site}&up=false" },
    "verifyUrl":    "http://localhost/health",
    "expectStatus": 500,
    "waitSeconds":  20
  },
  "restore": {
    "notify":       { "type": "http", "url": "http://localhost/admin/health?node={hostname}&site={site}&up=true" },
    "verifyUrl":    "http://localhost/health",
    "expectStatus": 200,
    "waitSeconds":  30
  }
}
```

Every part is optional. A phase with only `waitSeconds` is a plain pause; **verification is skipped
unless both `verifyUrl` and `expectStatus` are set**, in which case the phase assumes the notify
worked. `notify.type` is `http` (default), `file` (create/delete a marker the probe looks for), or
`command`. URLs, paths, bodies and arguments support `%ENV_VAR%` expansion and the tokens
`{hostname}`, `{site}`, `{project}`, all resolved on the box that runs the step - never at plan
time, since peers receive the same config.

**`precheck` handles the offline box.** Deploying to a server that's already out of rotation —
stopped site, maintenance, a flag flipped by hand — shouldn't try to drain what's already
drained. More than that: on an offline box the health endpoint is unreachable, so the drain
notification would fail, and a failed drain aborts the deploy. Without the precheck an offline
server couldn't be updated at all.

The test is positive: the routine runs **only** when the endpoint answers with `liveStatus`
(default 200). Anything else — 500, 503, or a connection refused — means "not in rotation" and
both phases are skipped for that run. It polls for `timeoutSeconds` (default 10) rather than
asking once, so a momentary blip on a genuinely live box isn't misread as offline, which would
stop the site on live traffic. Omit `precheck` and the phases always run.

Three more things worth knowing:

- **`drain.waitSeconds` has a floor.** A probe-based balancer only removes the node after
  `unhealthyThreshold × probeInterval`, and in-flight requests still need to finish. Set it below
  that sum and the site stops on live traffic. Verifying `localhost` proves the flag flipped; it
  cannot prove the balancer noticed, which is what the wait covers.
- **Point `verifyUrl` at localhost, not the VIP.** A VIP response proves *some* node is healthy,
  not that this one is in or out of rotation.
- **A failed drain aborts before anything stops.** The site is still serving at that point, so
  aborting costs a deploy while continuing would cost requests.

After a failed deploy the instance is restored to rotation only if the previous build is still
what's live - either the deploy rolled back, or it failed before touching live files. Otherwise it
is **left drained** and the run fails, because `mirror` deletes files the artifact doesn't contain,
so an interrupted mirror leaves neither the old build nor the new one. Note `rollback` defaults to
`false`; with it off, a mid-mirror failure leaves the instance out of rotation for a human.

### Folder target

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `destinationPath` | string | `""` | Where the published output is copied. |
| `serviceName` | string | `null` | Windows service to stop before the copy and start after. |

### Velopack target

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `packId` | string | `""` | Velopack package id. |
| `packTitle` | string | `null` | Display title. |
| `icon` | string | csproj icon | Icon path override (repo-relative, project-relative, or absolute). |
| `channel` | string | `null` | Release channel. |
| `runtime` | string | `null` | Velopack RID. A cross-compile directive (`[win]`/`[osx]`/`[linux]`) is injected automatically when the target OS differs from the runner OS. |
| `framework` | string | `null` | Velopack framework dependency string. |
| `mainExe` | string | `null` | Main executable name. |
| `delivery` | enum | `PackOnly` | `PackOnly`, `DownloadAndPack`, or `DownloadPackAndUpload`. |
| `source` | object | `null` | Release feed to download prior releases from and/or upload to. See [Velopack source](#velopack-source). |

### Velopack source

`type` selects which fields apply.

| `type` | Fields |
|--------|--------|
| `Az` (Azure Blob) | `account`, `key`, `sas`, `container`, `prefix`, `endpoint` |
| `S3` | `keyId`, `secret`, `region`, `bucket`, `prefix`, `endpoint` |
| `GitHub` / `Gitea` | `repoUrl`, `token` |
| `Local` | `path` |
| `Http` (download only) | `url` |

Upload targets also honor `keepMaxReleases` (int) to prune old releases. Credential fields
(`key`, `secret`, `token`, `sas`) should use `%ENV_VAR%` expansion rather than literals.

### Directives (recap)

Directives in the `HEAD` commit message steer a run, and the matching command-line flags override
them field by field - see the [command-line reference](#command-line-reference).

| Directive | Effect | CLI override |
|-----------|--------|--------------|
| `pub:Web\|Proc*` | Publish exactly these projects (pipe-separated name globs). `pub:*` publishes everything, `pub:none` nothing. | `--pub` |
| `wait:<seconds>` | Rollout soak delay before peers apply. | `--wait` |
| `skiptests` | Deploy without running the unit-test gate. Also spelled `skip-tests` / `skip_tests`, and `skiptests:false` forces the gate back on. | `--skip-tests` |

```
git commit -m "urgent hotfix pub:WebApp wait:0 skiptests"
```

Because a flag that was never passed must not countermand the commit, `--skip-tests` is
three-state: absent leaves the commit's decision alone, `--skip-tests` skips, and
`--skip-tests false` runs the gate even when the commit asked to skip it. A skipped gate is
recorded in `result.json`, not just printed.

`doNotPublishIfNoPubInCommitMessage` above turns `pub:` into a required opt-in.

## Architecture

For the design rationale - the pure planner, the server/global scope model, blue-green folder-flip,
the multi-server peer agent, and commit directives - see
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Building from source

```bash
dotnet test                                   # run the unit tests
dotnet pack src/Dytools.DeployTool -c Release -o ./artifacts   # produce Dytools.DeployTool.<version>.nupkg
```

## License

[Apache License 2.0](LICENSE).
