---
title: "pks devcontainer destroy"
description: "Permanently remove a managed devcontainer, its named Docker volumes, and any staged remote project files, behind an explicit confirmation step."
tags: [reference, devcontainer, docker, cleanup]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks devcontainer destroy [CONTAINER_ID] [options]"
examples:
  - command: "pks devcontainer destroy"
    description: "Pick a container and remove it after confirmation"
  - command: "pks devcontainer destroy 3f2a1b"
    description: "Destroy a container by ID prefix"
  - command: "pks devcontainer destroy --force"
    description: "Skip the confirmation prompt"
---

`pks devcontainer destroy` removes a managed devcontainer and the named Docker volumes mounted into it. On a remote host it also deletes the staged copy of the project files under `/tmp/pks-devcontainer/{project}`.

This is irreversible. Anything that exists only inside the container's volume — including edits made live inside a running container — is gone once the volume is removed.

## Synopsis

```text
pks devcontainer destroy [CONTAINER_ID] [options]
```

`CONTAINER_ID` is optional and matches on prefix. Without it, the command shows a selection list.

## Prerequisites

- **Docker running** on the machine holding the container.
- **A registered SSH target**, only when destroying on a remote host.
- **Anything you want to keep already committed and pushed** out of the container.

## Destroy a container

```bash
pks devcontainer destroy
```

The command discovers the container's mounted volumes and prints a confirmation listing every container, volume, and file path it is about to remove. Read that list before answering. On confirmation the container is force-removed and each named volume is deleted.

Target one directly by ID prefix:

```bash
pks devcontainer destroy 3f2a1b
```

Skip the prompt in an automated cleanup, once you are certain of the target:

```bash
pks devcontainer destroy 3f2a1b --force
```

## Verify

```bash
pks devcontainer list --all
```

The destroyed container no longer appears, including among stopped containers.

## Options

| Flag | Default | Description |
|---|---|---|
| `-f\|--force` | `false` | Skip the confirmation prompt and destroy immediately. |
| `-o\|--output-path <PATH>` | — | Inherited. Not read by this command. |
| `-v\|--verbose` | `false` | Inherited. Not read by this command. |
| `--dry-run` | `false` | Inherited. Not read by this command, and there is no preview mode. |

## Troubleshooting

**Work disappeared after a destroy.** Every named volume mounted into the container is removed along with it. Files that lived only in the volume, such as edits made inside a running container that were never synced to the host, cannot be recovered. Commit and push from inside the container before destroying it.

**`--dry-run` shows no preview.** The option is inherited from the shared settings base and has no effect here. The confirmation prompt is the only place the removal targets are listed, so run without `--force` to see them.

**The command asks `Which host?` first.** When SSH targets are registered, the host picker runs before the container selection list. There is no flag to preselect a host for destroy, so this step is interactive on machines with registered targets.

**A volume survives the destroy.** Only volumes mounted into the container at the time of removal are discovered. A volume created for a container that no longer exists must be removed with Docker directly.

## See also

- [pks devcontainer list](/tools/pks/devcontainer/list) — confirm what exists before and after a destroy
- [pks devcontainer spawn](/tools/pks/devcontainer/spawn) — recreate the container from its configuration
- [pks devcontainer connect](/tools/pks/devcontainer/connect) — reopen a container instead of removing it
- [pks devcontainer](/tools/pks/devcontainer) — the command group and its mental model
