---
title: "pks devcontainer list"
description: "Show managed devcontainers with status, volumes, image, and creation time, on this machine or a registered SSH target, as a table or JSON."
tags: [reference, devcontainer, docker]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks devcontainer list [options]"
examples:
  - command: "pks devcontainer list"
    description: "List running managed devcontainers"
  - command: "pks devcontainer list --all"
    description: "Include stopped containers"
  - command: "pks devcontainer list --format json"
    description: "Emit machine-readable output"
---

`pks devcontainer list` shows the devcontainers `pks` manages — containers carrying the pks devcontainer Docker labels — with their status, associated volumes, creation time, and image. Only running containers are shown by default.

It reads Docker on the local machine, or on a registered SSH target when you pick one from the host prompt.

## Synopsis

```text
pks devcontainer list [options]
```

## Prerequisites

- **Docker running** on the machine being listed.
- **A registered SSH target**, only when listing a remote host. Targets are registered with `pks ssh`.

## List containers

```bash
pks devcontainer list
```

The command prints a table of running managed devcontainers. Add stopped ones:

```bash
pks devcontainer list --all
```

For scripting, switch the output format:

```bash
pks devcontainer list --all --format json
```

JSON output is an array. Zero matches print `[]`, so a consumer does not need a special case for an empty result.

## Verify

```bash
pks devcontainer list --format json
```

Every container you spawned with [pks devcontainer spawn](/tools/pks/devcontainer/spawn) appears in the output, each with the volume that backs it.

## Options

| Flag | Default | Description |
|---|---|---|
| `--all\|-a` | `false` | Show all containers, not only running ones. |
| `--format <FORMAT>` | `table` | Output format: `table` or `json`. |
| `-o\|--output-path <PATH>` | — | Inherited. Not read by this command. |
| `-v\|--verbose` | `false` | Inherited. Not read by this command. |
| `-f\|--force` | `false` | Inherited. Not read by this command. |
| `--dry-run` | `false` | Inherited. Not read by this command. |

## Troubleshooting

**The command asks `Which host?` every time.** When SSH targets are registered, the host picker always runs and offers Local plus each target. There is no flag to preselect a host for `list`, which makes it awkward to script on a machine with registered targets. `spawn --ssh-target` and `connect --remote` do accept a host non-interactively.

**No host prompt appears even though targets exist.** Remote listing depends on optional SSH services being available. When they are not, the picker is skipped and only local containers are shown.

**An invalid `--format` value errors out.** Anything other than `table` or `json` fails immediately rather than falling back to the default.

**A container you expected is missing.** It is either stopped — add `--all` — or it does not carry the pks devcontainer labels, in which case this command does not manage it.

## See also

- [pks devcontainer connect](/tools/pks/devcontainer/connect) — open one of the listed containers in VS Code
- [pks devcontainer destroy](/tools/pks/devcontainer/destroy) — remove a listed container and its volumes
- [pks devcontainer spawn](/tools/pks/devcontainer/spawn) — create the containers this command lists
- [pks devcontainer](/tools/pks/devcontainer) — the command group and its mental model
