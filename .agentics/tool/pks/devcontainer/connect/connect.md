---
title: "pks devcontainer connect"
description: "Open an already-running devcontainer in VS Code by container ID or interactive pick, locally or through Remote-SSH on a registered target."
tags: [reference, devcontainer, vscode]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks devcontainer connect [CONTAINER_ID] [options]"
examples:
  - command: "pks devcontainer connect"
    description: "Pick a running devcontainer and open it"
  - command: "pks devcontainer connect 3f2a1b"
    description: "Open a container by ID prefix"
  - command: "pks devcontainer connect --no-launch-vscode"
    description: "Print the folder URI instead of launching"
  - command: "pks devcontainer connect --remote buildbox"
    description: "Open a devcontainer on a registered SSH target"
---

`pks devcontainer connect` opens a devcontainer that already exists in VS Code, without going through the build and volume setup that [pks devcontainer spawn](/tools/pks/devcontainer/spawn) performs. It builds a `vscode-remote://` URI for the container and launches VS Code against it.

Use it to get back into a container you spawned earlier, or to hand someone the exact URI to open.

## Synopsis

```text
pks devcontainer connect [CONTAINER_ID] [options]
```

`CONTAINER_ID` is optional and matches on prefix. Without it, the command prompts with a list of containers.

## Prerequisites

- **VS Code installed** and discoverable on the local path. The local flow checks for it and exits with a download link when it is missing.
- **A running devcontainer.** See the troubleshooting note on stopped containers.
- **A registered SSH target and the Remote-SSH extension**, only for `--remote`.

## Connect locally

```bash
pks devcontainer connect
```

The command lists running managed containers and asks which one to open. When none are running, it falls back to showing all of them. Pick one and VS Code opens the workspace inside it.

Target a specific container by ID prefix:

```bash
pks devcontainer connect 3f2a1b
```

Get the URI without launching anything:

```bash
pks devcontainer connect --no-launch-vscode
```

The command prints the `code --folder-uri "..."` invocation for you to run yourself.

## Connect to a remote host

```bash
pks devcontainer connect --remote buildbox
```

The command connects over SSH to the registered target, lists the devcontainers running there, and builds an SSH-remote URI of the form `vscode-remote://ssh-remote+{user}@{host}/workspaces/{project}`. Opening that URI needs the Remote-SSH extension in VS Code.

## Verify

```bash
pks devcontainer list
```

The container you connected to shows as running. VS Code's window title bar names the container or host you attached to.

## Options

| Flag | Default | Description |
|---|---|---|
| `--no-launch-vscode` | `false` | Print the folder URI command instead of launching VS Code. |
| `--remote <TARGET>` | — | Connect to a devcontainer on a registered SSH target. Accepts a host, a label, or `user@host`. |
| `-o\|--output-path <PATH>` | — | Inherited. Not read by this command. |
| `-v\|--verbose` | `false` | Inherited. Not read by this command. |
| `-f\|--force` | `false` | Inherited. Not read by this command. |
| `--dry-run` | `false` | Inherited. Not read by this command. |

## Troubleshooting

**Connecting to a stopped container is a dead end.** The command offers to start it, but answering yes prints `Auto-start is not yet implemented` along with the `docker start <id>` command and exits 1. Start the container with Docker directly, then run connect again.

**VS Code is not detected.** The local flow requires the `code` installation to be discoverable and exits with a download link otherwise. Install VS Code, or use `--no-launch-vscode` and open the printed URI from a machine that has it.

**The remote URI opens nothing.** `--remote` produces an `ssh-remote+` URI that depends on the Remote-SSH extension. Install it in VS Code and retry.

**`SSH support not available`.** The optional SSH services this command depends on were not available in the session. Confirm your SSH targets are registered with `pks ssh`.

## See also

- [pks devcontainer list](/tools/pks/devcontainer/list) — find the container ID to connect to
- [pks devcontainer spawn](/tools/pks/devcontainer/spawn) — create a devcontainer in the first place
- [pks devcontainer destroy](/tools/pks/devcontainer/destroy) — remove a container you no longer need
- [pks devcontainer](/tools/pks/devcontainer) — the command group and its mental model
