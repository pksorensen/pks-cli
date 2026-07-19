---
title: "pks claude backup"
description: "Mirror your entire ~/.claude directory to every registered rsync target over SSH, with a per-target results table and an all-or-nothing exit code."
tags: [how-to, backup, rsync, claude-code]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks claude backup"
---

`pks claude backup` copies the whole `~/.claude` directory — sessions, projects, and settings — to every rsync target you have registered, one after another. It is the one-shot way to get your Claude Code state onto a NAS or a backup host.

## Prerequisites

- **At least one registered rsync target.** Register targets with `pks rsync init`. With none registered the command exits `1` and says so.
- **The `rsync` binary on your PATH.** Missing it exits `1` with an install hint.
- **Key-based SSH to each target.** The transfer runs with `BatchMode=yes`, so nothing can prompt for a password.

## This is a mirror, not an archive

The transfer is `rsync -avz --delete`. Files you delete locally from `~/.claude` are deleted on the remote target at the next backup. Old sessions you clean up locally do not survive on the far end. If you need accumulate-only history, snapshot the target separately.

## 1. Register a target

```bash
pks rsync init
```

Complete this once per destination. Targets are stored in `~/.pks-cli/rsync-targets.json`.

## 2. Run the backup

```bash
pks claude backup
```

Each target gets a spinner while it transfers. At the end you get a results table with success or failure, duration, and the rsync stats line per target. The command takes no arguments and no flags.

## 3. Check the exit code

```bash
pks claude backup && echo ok
```

The exit code is `0` only when every target succeeded. A single failed target fails the whole run, which is what makes it safe to chain in a cron line.

## Verify

Run the backup, then confirm the mirror landed on a target:

```bash
ssh my-backup-host ls ~/.claude
```

You should see the same top-level entries as your local `~/.claude`.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Exit `1` naming `pks rsync init` | No targets are registered. Register one first. |
| Exit `1` with an install hint | `rsync` is not on your PATH. Install it with your package manager. |
| A target shows FAILED with no prompt | SSH runs with `BatchMode=yes` and `StrictHostKeyChecking=accept-new`. A target needing interactive password auth, or with an unresolvable host key, fails into the row instead of asking. Fix key-based access to that host. |
| Files you deleted locally are gone remotely too | `--delete` makes this a mirror. Expected. Snapshot the destination if you need retention. |

## See also

- [pks claude stats](/tools/pks/claude/stats) — what lives in the transcripts you are backing up
- [pks claude managed-settings](/tools/pks/claude/managed-settings) — the other small utility in the branch
- [pks claude reference](/tools/pks/claude/reference) — every command and flag in the branch
