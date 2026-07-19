---
title: "pks devcontainer spawn"
description: "Run a project's devcontainer in a named Docker volume on this machine, a registered SSH target, or a new Azure VM, and open it in VS Code."
tags: [how-to, devcontainer, docker, ssh]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks devcontainer spawn [PROJECT_PATH] [options]"
examples:
  - command: "pks devcontainer spawn"
    description: "Spawn the devcontainer for the current directory"
  - command: "pks devcontainer spawn /path/to/project"
    description: "Spawn the devcontainer for another project path"
  - command: "pks devcontainer spawn --volume-name my-project-vol"
    description: "Use a specific Docker volume name"
  - command: "pks devcontainer spawn --no-launch-vscode"
    description: "Build and start without opening VS Code"
  - command: "pks devcontainer spawn --ssh-target buildbox"
    description: "Spawn on a registered SSH target"
  - command: "pks devcontainer spawn --force"
    description: "Skip container reuse and spawn fresh"
---

`pks devcontainer spawn` takes a project that already has a `.devcontainer/devcontainer.json` and runs it as a real devcontainer backed by a named Docker volume, then opens it in VS Code. The same command runs the container on your machine, on a registered SSH target, or on an Azure VM it starts for you.

This is the command `pks claude` and `pks vibecast` are built on — both extend it and reuse its flags and remote-spawn machinery.

## Synopsis

```text
pks devcontainer spawn [PROJECT_PATH] [options]
```

`PROJECT_PATH` defaults to the current directory.

## Prerequisites

- **A devcontainer configuration.** `.devcontainer/devcontainer.json` must exist in the target project. Spawn does not generate one — run [pks devcontainer init](/tools/pks/devcontainer/init) or [pks devcontainer wizard](/tools/pks/devcontainer/wizard) first.
- **Docker running** on whichever machine the container lands on.
- **The devcontainer CLI.** The local path needs the `@devcontainers/cli` npm package. When it is missing, the command exits with the install instruction.
- **A registered SSH target**, only for remote spawns. Register targets with `pks ssh`; `--ssh-target` accepts a label or host and errors with `SSH target not found` for anything else.

## 1. Spawn locally

```bash
pks devcontainer spawn
```

With no `--ssh-target`, the command asks where to spawn and offers Local, each registered SSH target, and `Spawn new VM...`. Choose Local. Pre-flight checks for Docker and the devcontainer CLI run first, a volume name is generated and confirmed, and the container is built and started. VS Code opens against it unless `--no-launch-vscode` is set.

Name the volume yourself when you want a predictable target for later cleanup:

```bash
pks devcontainer spawn --volume-name my-project-vol
```

## 2. Understand reuse and drift

When a managed container already exists for the project and `--force` was not passed, the command compares three copies of the configuration: the files on the host, the container's build-time label, and the contents of the Docker volume. This catches the case where `devcontainer.json` was edited on the host and also edited live inside the running container.

On a mismatch you are offered sync, discard, or cancel before the command decides to reconnect or rebuild. Choosing to discard drops one side of the edits permanently.

Force the rebuild decision instead of being asked:

```bash
pks devcontainer spawn --rebuild        # always rebuild
pks devcontainer spawn --no-rebuild     # never rebuild
pks devcontainer spawn --auto-rebuild   # rebuild on change, no prompt
```

`--rebuild` wins over `--no-rebuild`, which wins over `--auto-rebuild`. With none of the three set, the behavior is the same as `--auto-rebuild`.

## 3. Spawn on a remote host

```bash
pks devcontainer spawn --ssh-target buildbox
```

The remote path is gated by the action guard: a second-factor or consent check runs before anything executes, and a denial aborts with `Denied: ...`. Then the command ensures the target is reachable — an Azure VM that is stopped or deallocated is started and polled for SSH — checks Docker over SSH, looks for an existing container by Docker label, copies project files across unless `--no-copy-source` is set, installs Node 20 and the devcontainer CLI on the host if they are missing, and runs the devcontainer CLI there. Azure AI Foundry token environment variables are forwarded into the container where applicable.

The Node and CLI install is a one-time cost of roughly two minutes on a fresh VM.

## 4. Verify

```bash
pks devcontainer list
```

The container you spawned appears with its status, volume, image, and creation time. Reopen it later without rebuilding using [pks devcontainer connect](/tools/pks/devcontainer/connect).

## Options

| Flag | Default | Description |
|---|---|---|
| `--volume-name <NAME>` | generated and confirmed | Docker volume name backing the devcontainer. |
| `--no-launch-vscode` | `false` | Do not open VS Code after spawning or connecting. |
| `--no-copy-source` | `false` | Copy only the `.devcontainer` configuration, not the source files. |
| `--no-bootstrap` | `false` | Use direct execution instead of the bootstrap container. |
| `--forward-docker-config` | `true` | Forward Docker credentials from the host into the container. |
| `--no-forward-docker-config` | `false` | Disable Docker credential forwarding. Takes priority over the flag above. |
| `--docker-config-path <PATH>` | `~/.docker/config.json` | Docker config file to forward. |
| `--rebuild` | `false` | Always rebuild, even with no configuration change. |
| `--no-rebuild` | `false` | Never rebuild, even with configuration changes. |
| `--auto-rebuild` | `false` | Rebuild without prompting when changes are detected. Also the default behavior. |
| `--ssh-target <TARGET>` | — | Registered SSH target label or host to spawn on. |
| `--env <ENV>` | — | Extra environment variables in `KEY=VALUE` form, forwarded into the container. |
| `--server <URL>` | — | Agentic server URL, forwarded into the container as `AGENTIC_SERVER`. |
| `--inline` | `false` | Run inline on this machine with no devcontainer. Honored by subclasses such as `pks claude`, not by this command. |
| `-o\|--output-path <PATH>` | — | Inherited. `PROJECT_PATH` is the path input for this command. |
| `-v\|--verbose` | `false` | Verbose output. |
| `-f\|--force` | `false` | Skip container reuse and drift detection, and spawn fresh. |
| `--dry-run` | `false` | Inherited. Does not gate spawn behavior. |

## Environment

| Variable | Default | Purpose |
|---|---|---|
| `AGENTIC_SERVER` | (unset) | Set inside the spawned container from `--server`, pointing a session at a specific server or dev tunnel. |

## Troubleshooting

**Docker credentials end up inside the container.** That is the default, matching VS Code's own devcontainer behavior. Pass `--no-forward-docker-config` to opt out, or point `--docker-config-path` at a config that holds only what you want shared.

**The command is waiting for a choice in a script.** Without `--ssh-target`, a host picker is shown whenever SSH targets are registered. Pass `--ssh-target` to select non-interactively.

**`SSH target not found`.** The label or host is not registered. Register it with `pks ssh` first.

**`SSH did not become available in time`.** The remote Azure VM was started but SSH did not come up within the polling window. The command warns rather than failing hard — retry once the VM finishes booting.

**A remote spawn pauses for about two minutes.** Node 20 and the devcontainer CLI are being installed on the remote host. This happens once per fresh machine.

**The command reports the wrong project path.** Spectre's argument binding can place a sibling command's own positional argument into the inherited `PROJECT_PATH` slot when both sit at index 0. The remote path falls back to the current working directory when the resolved path does not exist on disk. Pass `PROJECT_PATH` explicitly to be certain.

**`Denied: ...` on a remote spawn.** The action guard refused the remote-spawn action. Remote spawning runs code on, and bills, another machine — approve the action to continue.

## See also

- [pks devcontainer connect](/tools/pks/devcontainer/connect) — reopen a spawned container in VS Code
- [pks devcontainer list](/tools/pks/devcontainer/list) — see what is running, locally or remotely
- [pks devcontainer destroy](/tools/pks/devcontainer/destroy) — remove a container and its volumes
- [pks devcontainer init](/tools/pks/devcontainer/init) — create the configuration spawn requires
- [pks devcontainer](/tools/pks/devcontainer) — the command group and its mental model
