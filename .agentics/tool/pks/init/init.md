---
title: "pks init"
description: "Scaffold a new agentic devcontainer project from a NuGet template, then optionally spawn the resulting devcontainer locally or on a remote SSH target."
tags: [reference, cli, devcontainer]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks init [PROJECT_NAME] [options]"
examples:
  - command: "pks init"
    description: "Fully interactive: pick a template, name, and description"
  - command: "pks init MyProject --template pks-claude-dotnet9"
    description: "Non-interactive template selection"
  - command: "pks init MyProject -t pks-claude-dotnet9 -d \"Agentic demo\" --force"
    description: "Fully non-interactive scaffold, overwriting an existing directory"
  - command: "pks init MyProject --local-template-path ./my-template --force"
    description: "Test a template under development without publishing to NuGet"
  - command: "pks init MyProject -t pks-claude-dotnet9 --spawn-devcontainer --no-launch-vscode"
    description: "Scaffold and spawn the devcontainer locally, skip VS Code"
  - command: "pks init MyProject -t pks-claude-dotnet9 --ssh-target build-box"
    description: "Scaffold and spawn the devcontainer on a registered SSH target"
---

`pks init` scaffolds a new project from a devcontainer template published on NuGet, then optionally spawns the resulting devcontainer for you. It is the command to run first when starting a new agentic or devcontainer-based project.

## Overview

`pks init` searches NuGet for packages tagged `pks-templates` (or a custom tag), lets you pick one, and extracts it into a new directory named after the project. If the extracted template contains a `.devcontainer/devcontainer.json`, it offers to spawn the devcontainer immediately, either on this machine or on a remote host reachable over SSH.

- **Discovery is NuGet-based.** Templates are ordinary NuGet packages carrying the `pks-templates` tag by default; nothing is hardcoded into the CLI.
- **Every prompt is optional.** Any argument or option you leave unset falls back to an interactive Spectre.Console prompt, so `pks init` with zero flags is fully usable.
- **Spawning is optional and separate from scaffolding.** Project files are always written first; the devcontainer-spawn step only runs if the template has a devcontainer definition and you don't suppress it.

## What you get

- **A new project directory.** Named after `PROJECT_NAME`, containing the template's `content/` files.
- **A summary panel with next steps** printed after extraction.
- **An offer to spawn the devcontainer** when the template includes `.devcontainer/devcontainer.json` — locally via Docker and the `devcontainer` CLI, or on a remote host over SSH.
- **A build-log file** for any spawn attempt, so failures are diagnosable even when the console only shows a truncated summary.

## When to use it

Run `pks init` as the first command for a new agentic or devcontainer-based project, instead of hand-authoring a `.devcontainer/` folder. Use `--local-template-path` while developing a template package, before it's published to NuGet.

## Prerequisites

- **Network access to nuget.org** (or a custom `--nuget-source`), unless you pass `--local-template-path`.
- **Docker and the `@devcontainers/cli` npm package**, for a local spawn. `pks init` checks for both and reports a failure rather than installing them; install with `npm install -g @devcontainers/cli`.
- **A registered SSH target**, for a remote spawn. Register one first with `pks ssh register user@host` — `pks init` itself does not register targets.
- **A `.template.config/template.json` with a `content/` sibling folder**, if using `--local-template-path`. A missing path, or a template.json that can't be parsed, exits 1.

## Synopsis

```text
pks init [PROJECT_NAME] [options]
```

`PROJECT_NAME` is the only argument. It becomes the new directory name (created under the current working directory) and the display name shown in prompts. If omitted, you're asked for it interactively. An invalid filename exits with code 1 before anything is created.

### Options

| Flag | Default | Description |
|---|---|---|
| `-t, --template <TEMPLATE>` | — | Template short name or NuGet package ID to select non-interactively. Matched case-insensitively against each discovered template's short names or package ID. |
| `-d, --description <DESCRIPTION>` | — | Project description/objective. Prompted interactively when omitted, defaulting to the selected template's own description or "An agentic development project". |
| `-f, --force` | `false` | Overwrite the target directory if it already exists. Without it, an existing directory with the same name is an immediate error and exit code 1. |
| `--nuget-source <SOURCE>` | `https://api.nuget.org/v3/index.json` | Custom NuGet feed URL to search for templates. Ignored when `--local-template-path` is set. |
| `--local-template-path <PATH>` | — | Path to a local template directory, bypassing NuGet. Must contain `.template.config/template.json`; its `content/` sibling is copied into the new project directory. |
| `--tag <TAG>` | `pks-templates` | NuGet package tag used to filter template search results. Zero matches prints a warning and a hint to run `pks template list --all`, then exits 1 — note that hint is stale: there is no `pks template` command anywhere in the CLI, so following it is a dead end. Search nuget.org directly for the tag instead. |
| `--prerelease` | `false` | Include prerelease/preview versions of template packages in discovery results. |
| `--agentic` | `false` | Adds a `pks agent create` bullet to the post-init "Next steps" panel. Does not configure anything in the extracted files. |
| `--mcp` | `false` | Adds a `pks mcp init` bullet to the post-init "Next steps" panel. Does not configure anything in the extracted files. |
| `--spawn-devcontainer` | `false` | Skip the spawn-confirmation prompt and spawn immediately. Only relevant if the extracted template has `.devcontainer/devcontainer.json`. |
| `--no-devcontainer-prompt` | `false` | Suppress the devcontainer-spawn prompt entirely, even if the template has a devcontainer definition. Project files are still created. |
| `--volume-name <NAME>` | generated | Docker volume name for the devcontainer, supplied non-interactively. When omitted, a name is generated and you're prompted to confirm or edit it before spawning. |
| `--no-launch-vscode` | `false` | Spawn the devcontainer without opening VS Code afterward; connect manually via the Dev Containers extension. |
| `--build-arg <ARG>` | — | Docker build argument in `KEY=VALUE` form. Repeatable. Malformed entries (no `=`) print a warning and are skipped. |
| `--prompt-build-args` | `false` | After parsing any `--build-arg` values, also interactively prompt for every `build.args` key found in the template's `devcontainer.json`, pre-filled with its default value. |
| `--build-log <PATH>` | — | Redirect devcontainer build output to a log file. For remote spawns this is overridden — a log under the OS temp directory is always created and tailed live. |
| `--ssh-target <TARGET>` | — | Spawn on a remote host: `user@host`, or the name/label of a target registered via `pks ssh register`. Unresolved targets fail fast with a registration hint. |

## Behavior details

**Template discovery and selection.** With no `--template`, discovered packages are shown in an interactive selection prompt. With `--template` set but not matching any discovered template's short names or package ID, the command prints an error and a table of the available templates, then exits 1. Duplicate package IDs across sources are deduplicated to the highest version only — older matches aren't shown even with `--prerelease`.

**Directory creation.** Project-name validation runs first, before anything else. After that, template discovery/selection (the NuGet lookup or `--local-template-path` read) and the description prompt always run — the target directory is checked for existence only afterward, as the last gate before extraction. So an "already exists" failure (exit 1, without `--force`) always comes after a NuGet network call (or local template read) and any prompts, never before them.

**Devcontainer offer.** The spawn offer only appears when the extracted template contains `.devcontainer/devcontainer.json`. Templates without one complete silently with no error and no prompt.

**Local vs. remote spawn.** Local spawn preflight checks Docker availability and whether the `devcontainer` CLI is installed; either check failing aborts the spawn (project files remain) with a hint to retry via `pks devcontainer spawn`. Remote spawn (`--ssh-target`) skips these local checks entirely and assumes the remote host has both Docker and the `devcontainer` CLI — failures there surface in the streamed build log, not local preflight.

**SSH target picker.** When `--ssh-target` is omitted and one or more targets are registered via `pks ssh register`, you get an interactive picker offering "Local (this machine)" plus each registered target. With no targets registered, it spawns locally without asking.

**Failure logs.** Failed devcontainer spawns write a full log to `%TEMP%/pks-cli/logs/spawn-{project}-{timestamp}.log` — never inside the project directory — and print only a truncated summary line to the console.

## Examples

```bash
pks init
```

Fully interactive: prompts for project name, lists templates tagged `pks-templates` from NuGet, prompts for description, then offers to spawn the devcontainer.

```bash
pks init MyProject --template pks-claude-dotnet9
```

Non-interactive template selection; still prompts for description unless `-d` is also given.

```bash
pks init MyProject -t pks-claude-dotnet9 -d "Agentic demo" --force
```

Fully non-interactive scaffold that overwrites an existing `MyProject/` directory.

```bash
pks init MyProject --local-template-path ./my-template --force
```

Test a template under development (must contain `.template.config/template.json` and `content/`) without publishing to NuGet.

```bash
pks init MyProject -t pks-claude-dotnet9 --spawn-devcontainer --no-launch-vscode
```

Scaffold and spawn the devcontainer locally without opening VS Code.

```bash
pks init MyProject -t pks-claude-dotnet9 --ssh-target build-box --volume-name myproject-vol
```

Scaffold and spawn the devcontainer on a previously registered remote SSH target, streaming the remote build log.

```bash
pks init MyProject -t pks-claude-dotnet9 --build-arg NODE_VERSION=20 --build-arg FEATURE_X=true
```

Pass explicit Docker build args to the devcontainer build.

## Troubleshooting

**"No templates found" or the tag search returns nothing.** The default `--tag` is `pks-templates`. Confirm the tag and `--nuget-source` before assuming the CLI is broken. The command's own hint to run `pks template list --all` is stale — no such command exists; search nuget.org directly for packages carrying your tag instead. (`pks tools` is unrelated: it only generates tool-registry documentation Markdown from `[ToolRegistryExport]`-tagged commands and has nothing to do with NuGet templates.)

**"Directory already exists" on a fresh-looking run.** `pks init` fails fast on an existing target directory unless `--force` is passed — pass `--force` if overwriting is intended, or choose a different `PROJECT_NAME`.

**`--agentic` / `--mcp` didn't configure anything.** These flags only add bullets to the printed "Next steps" panel. They don't wire up agent or MCP configuration in the generated project — run `pks agent create` or `pks mcp init` yourself afterward.

**No spawn prompt appeared.** The template you selected has no `.devcontainer/devcontainer.json`. This is not an error; the command completes after writing project files.

**Local spawn aborted with a Docker or CLI check failure.** Project files were still created. Install Docker and `npm install -g @devcontainers/cli`, then retry with `pks devcontainer spawn` instead of re-running `pks init`.

**`--ssh-target` fails immediately with an unresolved-target error.** The value didn't match a registered target's name/label or a raw `user@host`. Register the target first: `pks ssh register user@host`.

**A remote or local spawn failed with only a truncated console message.** Check the full build log at `%TEMP%/pks-cli/logs/spawn-{project}-{timestamp}.log` — it is never written inside the project directory.

> **Note.** CLAUDE.md's description of an `IInitializer` pipeline (`src/Infrastructure/Initializers/`) does not describe this command. `pks init` is purely NuGet-template-based and never calls that pipeline — treat any reference to an "initializer pipeline" for `pks init` as stale.

## See also

- [pks ssh](/tools/pks/ssh) — register the SSH targets `--ssh-target` resolves against
- [pks devcontainer](/tools/pks/devcontainer) — spawn a devcontainer manually after a failed or skipped spawn
- [pks agent](/tools/pks/agent) — configure agent capabilities the `--agentic` next-step hint points at
