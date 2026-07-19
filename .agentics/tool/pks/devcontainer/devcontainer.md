---
title: "pks devcontainer"
description: "Author, validate, spawn, and tear down VS Code devcontainers — locally or over SSH — from one command group in the pks CLI."
tags: [concept, devcontainer, docker, vscode]
category: infrastructure
platform: [linux, macos, windows]
status: stable
author: Poul Kjeldager
component: pks
usage: "pks devcontainer <command> [options]"
examples:
  - command: "pks devcontainer init MyProject"
    description: "Generate .devcontainer/devcontainer.json from flags"
  - command: "pks devcontainer wizard"
    description: "Build a configuration through guided prompts"
  - command: "pks devcontainer validate --strict"
    description: "Lint the configuration and fail on warnings"
  - command: "pks devcontainer spawn"
    description: "Run the devcontainer and open it in VS Code"
  - command: "pks devcontainer list --all"
    description: "Show managed devcontainers including stopped ones"
  - command: "pks devcontainer destroy"
    description: "Remove a container and its Docker volumes"
---

`pks devcontainer` manages devcontainer configurations for isolated development environments. It covers two jobs that share a filename and nothing else: writing the `.devcontainer/devcontainer.json` file, and running the container that file describes.

## Overview

`pks devcontainer` is a command group in the `pks` CLI. It generates a devcontainer configuration for a repository, checks that configuration for errors, and then runs it as a real Docker container backed by a named volume — on your machine, on a registered SSH target, or on an Azure VM it starts for you.

- **Configuration authoring.** `init`, `wizard`, and `validate` read and write files on disk. Docker is never involved.
- **Container lifecycle.** `spawn`, `list`, `connect`, and `destroy` operate on running containers and their Docker volumes, locally or over SSH.

## What you get

- **Two ways to write a configuration.** `init` is flag-driven and scriptable. `wizard` walks through templates, features, required environment variables, and extensions with prompts.
- **A linter that runs before Docker does.** `validate` checks structure, features, extensions, ports, base image, and referenced file paths in one pass, with a `--strict` mode for CI.
- **Volume-backed containers.** `spawn` puts the workspace in a named Docker volume rather than a bind mount, so the container owns its own copy of the files.
- **Remote execution.** `spawn --ssh-target` runs the same flow on another machine, installing Node 20 and the devcontainer CLI there if they are missing.
- **Drift detection.** When reconnecting to an existing container, `spawn` compares the host configuration, the container's build-time label, and the volume contents, and asks how to resolve a mismatch.
- **A clean teardown path.** `destroy` removes the container, its named volumes, and any staged remote copy of the project.

## How it fits together

Start in a repository. `init` or `wizard` writes `.devcontainer/devcontainer.json`. `validate` reads that file and reports problems. Neither command needs Docker, so both run in CI.

`spawn` picks the file up from there. It requires `.devcontainer/devcontainer.json` to already exist, checks that Docker and the `@devcontainers/cli` npm package are available, generates or accepts a Docker volume name, builds the container, and launches VS Code against it. `list` shows what `spawn` produced, `connect` reopens one of those containers in VS Code without rebuilding, and `destroy` removes it along with its volumes.

- **Config commands** (`init`, `wizard`, `validate`) fail on missing files and bad JSON.
- **Container commands** (`spawn`, `list`, `connect`, `destroy`) fail on missing Docker, a missing devcontainer CLI, or an unreachable SSH target.

Two other `pks` commands build directly on this machinery: `pks claude` and `pks vibecast` both extend the spawn command and reuse its flags and remote-spawn behavior.

## Commands

`init` · `wizard` · `validate` · `spawn` · `list` · `connect` · `destroy`

| Command | What it does |
|---|---|
| [`init`](/tools/pks/devcontainer/init) | Generate `.devcontainer/devcontainer.json` from flags. |
| [`wizard`](/tools/pks/devcontainer/wizard) | Build the same configuration through guided prompts. |
| [`validate`](/tools/pks/devcontainer/validate) | Lint an existing configuration and report errors and warnings. |
| [`spawn`](/tools/pks/devcontainer/spawn) | Run the devcontainer in a Docker volume and open VS Code. |
| [`list`](/tools/pks/devcontainer/list) | Show managed devcontainers as a table or JSON. |
| [`connect`](/tools/pks/devcontainer/connect) | Reopen a running devcontainer in VS Code. |
| [`destroy`](/tools/pks/devcontainer/destroy) | Remove a container and its named Docker volumes. |

Every subcommand inherits four options from the shared settings base: `-o|--output-path <PATH>`, `-v|--verbose`, `-f|--force`, and `--dry-run`. Several commands ignore some of them — each page states which.

## Next steps

- [pks devcontainer init](/tools/pks/devcontainer/init) — generate a configuration non-interactively, with templates and features
- [pks devcontainer wizard](/tools/pks/devcontainer/wizard) — the guided path, including NuGet-discovered templates
- [pks devcontainer validate](/tools/pks/devcontainer/validate) — check a configuration before it reaches Docker
- [pks devcontainer spawn](/tools/pks/devcontainer/spawn) — run the container locally or on a remote host
- [pks devcontainer connect](/tools/pks/devcontainer/connect) — reopen an existing container in VS Code
- [pks devcontainer destroy](/tools/pks/devcontainer/destroy) — remove a container and reclaim its volumes
