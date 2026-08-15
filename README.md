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
5. On a **fleet**, the box running the job deploys first; peers apply after a soak delay, and a
   failed target rolls back.
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

The tool exits `0` on success, `1` on failure. Which projects deploy is decided from the
changed-file list against each project's folder, its `.csproj` `ProjectReference`s (resolved
automatically), and any extra `dependentProjects` triggers. Directives `pub:<pattern>` and
`wait:<seconds>` are read from the `HEAD` commit; `--pub` / `--wait` override them field by field
(see [Running it by hand](#running-it-by-hand)).

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
| `hostname` | string | `""` | Matched case-insensitively against the machine name to decide which entry the running box is. |
| `incomingShare` | string | `null` | UNC path where the primary drops run folders for this peer, e.g. `\\WEB02\deploy\incoming`. Unused on whichever box is currently the primary (it applies inline). |

### Rollout

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `delaySeconds` | int | `3600` | Soak time between the primary going live and peers applying. Clock starts at **primary success**. Overridden per run by `wait:` / `--wait`. |
| `keepRuns` | int | `5` | Run folders retained per peer before the oldest are pruned. |

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

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `deployPath` | string | `""` | Deploy destination (slot A). In classic mode this is simply the target folder. |
| `appPool` | string | `""` | App pool name. |
| `siteName` | string | `null` | IIS site name. Required only for blue-green (the site is what gets flipped). |
| `secondaryDeployPath` | string | `null` | Slot B. Setting it turns on blue-green: the artifact is mirrored into whichever slot is **not** live, then the site's physical path is flipped to it. |
| `warmupUrl` | string | `null` | URL hit immediately after the flip so cold start lands here, not on a real user. A non-success response fails the deploy and flips back to the previous slot. |

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

`pub:` and `wait:` in the `HEAD` commit message steer a run, and `--pub` / `--wait` override them
on the command line - see the [command-line reference](#command-line-reference).
`doNotPublishIfNoPubInCommitMessage` above turns `pub:`
into a required opt-in.

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
