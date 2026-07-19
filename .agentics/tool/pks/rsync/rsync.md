---
title: "pks rsync"
description: "Register rsync backup targets — a NAS, a home server, or any remote host reachable over SSH — as prerequisite setup for pks claude backup."
tags: [reference, rsync, backup, ssh]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks rsync <command>"
examples:
  - command: "pks rsync init"
    description: "Register a NAS or remote host as a backup target"
  - command: "pks rsync list"
    description: "Show every registered backup target"
  - command: "pks rsync remove"
    description: "Interactively delete a stale target"
---

`pks rsync` manages a local registry of rsync backup targets — destinations such as a home NAS or a remote server that other pks commands can sync data to over SSH. It does not run any transfer itself; it only registers, lists, and removes the destinations that `pks claude backup` later syncs to.

## Overview

`pks rsync` is a three-command branch — `init`, `list`, `remove` — that reads and writes a single JSON file, `~/.pks-cli/rsync-targets.json`. Each entry captures a host, a username, a port, an optional SSH key, a remote backup path, and an optional label.

- **Setup only.** Registering a target here does not move any data. The only consumer of this registry today is `pks claude backup`, a separate top-level command that rsyncs `~/.claude/` to every registered target.
- **Interactive, not scripted.** All three commands prompt via Spectre.Console — there are no flags to pass values on the command line, so none of them can be piped or scripted.
- **Delegated auth.** `pks rsync` has no auth model of its own; every connection goes through the OS `ssh`/`rsync` binaries and whatever key or agent setup already exists on the machine.

## What you get

- **A single JSON registry.** One file per machine, `~/.pks-cli/rsync-targets.json`, holding every backup destination this machine knows about.
- **A connectivity check at registration.** `pks rsync init` test-connects over SSH before saving, so a broken host or key surfaces immediately instead of at backup time.
- **A read-only audit view.** `pks rsync list` prints every registered target in a table with no network calls.
- **A guided removal flow.** `pks rsync remove` lists targets as a selection menu and confirms before deleting.

## How it fits together

`pks rsync init` is the setup step; `pks claude backup` is the consumer. Run `pks rsync init` once per destination — a NAS, a home server, a remote box — before ever running `pks claude backup`. When `pks claude backup` runs, it loads every target from this same registry and, for each one, shells out to the real `rsync` binary with `--delete` to mirror `~/.claude/` onto that target over SSH.

A separate, unrelated branch, `pks ssh`, registers named SSH targets for devcontainer remotes in its own file, `~/.pks-cli/ssh-targets.json`. The two registries store overlapping fields — host, port, key — but are not shared or cross-referenced in code.

- **At a glance, the write path:** you run `pks rsync init`, pks test-connects over SSH, then writes the target to `~/.pks-cli/rsync-targets.json`.
- **At a glance, the read path:** `pks claude backup` reads the same file and shells out to `rsync` for each target it finds.

## When to use it

- Run `pks rsync init` once per backup destination before ever running `pks claude backup` — with zero targets registered, `pks claude backup` exits with an error instead of silently doing nothing.
- Run `pks rsync list` to confirm what's registered and check the host, port, key, and remote-path values before trusting a backup run.
- Run `pks rsync remove` to delete a stale or wrong target interactively.
- There is no `pks rsync backup` or `pks rsync sync` subcommand. The data transfer itself lives in `pks claude backup`, not in this branch.

## Prerequisites

- **An SSH-reachable destination.** The target host must accept SSH connections on the port you plan to register.
- **A working SSH key or agent.** `pks rsync init` tests connectivity with `-o BatchMode=yes`, which will not prompt for a password — if the target needs password auth, either load a key into `ssh-agent` first or point the prompt at a private key file when asked.
- **The `rsync` binary on this machine**, required by `pks claude backup` at transfer time, not by `pks rsync` itself.

## Commands

`init` · `list` · `remove`

There is no default command on this branch — a bare `pks rsync` requires one of the three subcommands.

### pks rsync init

Interactively registers a new backup target. Prompts, in order, for `user@host`, an SSH port, an optional SSH private key path, the remote backup directory, and an optional label — then test-connects over SSH and saves the target to `~/.pks-cli/rsync-targets.json`.

```text
pks rsync init
```

This command has no flags or arguments — every value is collected through interactive prompts, so it requires a TTY.

**Prompt sequence:**

1. `user@host` — split on the first `@`. Both the user and the host must be non-empty, or the command exits with an error.
2. SSH port — must parse as an integer between `1` and `65535`, defaults to `22`.
3. SSH private key path (optional) — leave blank to rely on `ssh-agent`. If given, the path is resolved to an absolute path and the command exits with an error immediately if the file does not exist.
4. Remote backup directory — normalized to always end with a trailing `/`, which matters for `rsync`'s source/destination semantics.
5. Label (optional) — a human-readable name shown by `pks rsync list` and `pks rsync remove`.

After the prompts, `pks rsync init` runs a connectivity test:

```text
ssh -i <key> -p <port> -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new user@host echo ok
```

```bash
pks rsync init
```

You'll be walked through the prompts above; the target is saved to `~/.pks-cli/rsync-targets.json` when they complete.

> **Note.** A failed connectivity test only prints a warning — the target is saved either way. `pks rsync list` can show a target that was never actually reachable.

### pks rsync list

Lists every registered target in a table: label, host, user, port, remote path, whether it uses a key or the agent, and when it was registered. Read-only — it reads `~/.pks-cli/rsync-targets.json` directly and makes no network calls.

```text
pks rsync list
```

This command has no flags or arguments.

```bash
pks rsync list
```

With no targets registered, this prints `No rsync targets registered.` and suggests `pks rsync init`, exiting cleanly rather than as an error.

> **Note.** `pks rsync list` reflects only what's on disk. It does not verify that a target is still reachable or that its remote path still exists.

### pks rsync remove

Interactively removes a registered target. Lists every target as a selection menu formatted `user@host (label)`, prompts for confirmation, then deletes it from `~/.pks-cli/rsync-targets.json`.

```text
pks rsync remove
```

This command has no flags or arguments — there is no way to target a specific entry non-interactively.

```bash
pks rsync remove
```

Pick a target from the list, then confirm. The confirmation prompt defaults to No, so declining it cancels cleanly with no changes.

With no targets registered, this prints `No rsync targets registered.` and exits cleanly rather than as an error.

> **Note.** Removal only deletes the local registry entry. It has no effect on the remote host or any files already synced there.

## Defaults

| Setting | Value |
|---|---|
| Registry file | `~/.pks-cli/rsync-targets.json` |
| Default SSH port | `22` |
| SSH key | unset — falls back to `ssh-agent` |
| Connectivity test | `ssh -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new` |

There is no environment variable that overrides the registry path — the location is fixed on every operating system, resolving to `C:\Users\<user>\.pks-cli\rsync-targets.json` on Windows.

## Troubleshooting

**`pks claude backup` exits with an error immediately.** No targets are registered. Run `pks rsync init` at least once before running `pks claude backup`.

**Connectivity warning during `pks rsync init`.** The `ssh … -o BatchMode=yes` test failed, which happens whenever the target would need an interactive password or passphrase prompt, the host is unreachable, or the port is wrong. The target is saved regardless — load your key into `ssh-agent`, or double-check the host and port, then re-run `pks rsync init` for the same host to overwrite the entry.

**Re-registering a target overwrites the old one silently.** `pks rsync init` matches on host, username, and port (case-insensitive) and replaces any existing entry with that combination, including its label, key, and remote path. If you meant to add a second target for the same host, use a different port or a different SSH user, or the previous entry is lost.

**A target shows in `pks rsync list` but backups fail.** `pks rsync list` never checks connectivity — it only reads the JSON file. Re-run `pks rsync init` for that host to re-verify and refresh the entry.

## See also

- [pks](/tools/pks) — the full pks command surface, including `pks claude backup`, the actual consumer of this registry
- [pks ssh](/tools/pks/ssh) — the separate, unrelated SSH-target registry used for devcontainer remotes
