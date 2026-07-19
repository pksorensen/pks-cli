---
title: "Storage"
description: "Browse and sync file-share storage from any authenticated pks provider, with uploads gated behind an interactive confirmation."
tags: [reference, cli, storage]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks storage <command> [options]"
examples:
  - command: "pks storage list"
    description: "List every account and share across authenticated providers"
  - command: "pks storage ls /users --count"
    description: "Browse /users with per-directory item counts"
  - command: "pks storage ls --json"
    description: "Browse the root share as machine-readable JSON"
  - command: "pks storage sync --direction download ./local"
    description: "Pull a share down to ./local (read-only, no prompt)"
  - command: "pks storage sync --direction upload ./local"
    description: "Push ./local up to a share (requires confirmation)"
  - command: "pks storage sync --dry-run"
    description: "Preview a sync without transferring anything"
---

`pks storage` is the provider-agnostic operational surface for browsing and moving files through whichever file-share provider you've authenticated with `pks fileshare init`. It never authenticates anything itself, and it deliberately splits its three commands by risk: reads run freely, writes stop for a human.

## Overview
`pks storage` layers three commands — `list`, `ls`, and `sync` — over the `IFileShareProvider` interface, so the same commands work against any registered provider without provider-specific tooling like Azure Storage Explorer or the `az` CLI. Today exactly one provider is implemented: Azure File Share (`azure-fileshare`).

- **Discover** what you have access to with `list`.
- **Browse** a specific share's contents with `ls`, including in `--json` mode for agent consumption.
- **Move files** in bulk with `sync` — download, upload, or bidirectional, with dry-run preview and checksum verification.

## What you get
- **Provider-agnostic browsing.** `list` and `ls` work against any authenticated `IFileShareProvider` without touching provider-specific tools.
- **Agent-safe reads.** `list` and `ls` are read-only and need no path or account argument to get started; `ls --json` is built specifically for machine consumption.
- **A hard write gate.** `sync` refuses to upload, mirror bidirectionally, or delete without an interactive human confirmation — and hard-fails outright in a non-interactive context rather than silently skipping the prompt.
- **Bulk transfer controls.** `sync` supports dry-run preview, MD5 checksum verification, glob include/exclude filtering, and parallel transfer.

## How it fits together
`pks storage` sits on top of `pks fileshare`, which owns authentication. Every `pks storage` command starts by calling `FileShareProviderRegistry.GetAuthenticatedProvidersAsync()`; if nothing is authenticated, `list` prints a warning and exits 0, while `ls` and `sync` exit 1, both pointing at `pks fileshare init`. If more than one provider is authenticated, `ls` and `sync` prompt interactively to pick one (or accept `--account`/`--share`/`--provider` up front), while `list` aggregates across all of them.

The load-bearing design decision is the risk split baked into the branch description itself: "download is agent-safe, upload requires consent." `list` and `ls` never write. `sync` is the only command that can write, and it checks whether the operation is a write (`Direction` is `Upload` or `Bidirectional`, or `--delete` is set) before doing anything — if so, and `--dry-run` isn't set, it requires an interactive terminal and an explicit confirmation that defaults to **No**.

- **Read path:** `list` → `ls` → done. No confirmation, no interactivity requirement.
- **Write path:** `sync --direction upload` (or `--direction bidirectional`, or `--delete`) → interactive confirmation required → hard failure if run non-interactively.

## Commands
`list` · `ls` · `sync`. Full flags, arguments, and examples for each are below.

## Reference

### pks storage list

Enumerates every storage resource (account + share/container pairs) visible across all currently authenticated providers and prints them as a table with Provider, Account, Share, and Details columns. Read-only — makes no writes and takes no path, account, or share argument. Run this first to discover what's available before running `ls` or `sync` against a specific resource.

```text
pks storage list [options]
```

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |

```bash
pks storage list
```

> **Note.** If zero providers are authenticated this prints a warning and "Run pks fileshare init to authenticate with a provider." and exits **0**, not an error — an empty table here means no provider is authenticated, not a crash. For each authenticated provider it makes one or more API calls with no caching, so a list against many providers or accounts costs proportionally more round-trips.

### pks storage ls

Lists files and directories inside a specific share and path — the directory browser, as opposed to `list`'s account/share inventory. Read-only and agent-safe, with a `--json` mode built for machine consumption. When `--account` or `--share` is omitted and more than one choice exists, it resolves the ambiguity with an interactive selection prompt.

```text
pks storage ls [path] [options]
```

| Argument | Required | Description |
|---|---|---|
| `[path]` | no | Path within the share. Defaults to `/`. |

| Flag | Default | Description |
|---|---|---|
| `--share <text>` | — | File share name. |
| `--account <text>` | — | Storage account name. |
| `--limit <int>` | `100` | Maximum items to return. |
| `--count` | `false` | Show item count per directory (costs extra API calls). |
| `--dirs-only` | `false` | Only show directories. |
| `--json` | `false` | Output as JSON (agent-friendly). |
| `-v`, `--verbose` | `false` | Enable verbose output. |

```bash
pks storage ls
```

Browses the root of the auto-resolved share.

```bash
pks storage ls /users --count
```

Lists `/users` with per-directory item counts.

```bash
pks storage ls --json
```

Prints a machine-readable payload shaped as:

```json
{
  "share": "string",
  "path": "string",
  "items": [
    { "type": "file", "name": "string", "sizeBytes": 0, "itemCount": 0 }
  ],
  "returned": 0,
  "truncated": false
}
```

`sizeBytes` and `itemCount` are omitted entirely (not `null`) when a value doesn't apply to that item — a `directory` entry, for example, may omit `sizeBytes`.

> **Availability.** Unlike `list`, `ls` exits **1** when nothing is authenticated, pointing at `pks fileshare init`. When neither `--account` nor `--share` is given and more than one account or share exists, `ls` drops into an interactive selection prompt — this will hang in a non-interactive agent context, so pass `--account`/`--share` explicitly for scripted or agent use. In human-readable output, a truncated result prints a visible warning telling you to raise `--limit` or narrow the path; in `--json` mode that warning does not print — only the `truncated: true` field signals it, so JSON consumers must check that field explicitly.

### pks storage sync

Bulk transfer between a remote share and a local directory — download (the default), upload, or bidirectional — with dry-run preview, MD5 checksum verification, glob include/exclude filtering, and parallel transfer. This is the only command in the branch that can write, and it enforces a human-in-the-loop consent gate on every write path.

```text
pks storage sync [local-path] [options]
```

| Argument | Required | Description |
|---|---|---|
| `[local-path]` | no | Local directory path. Prompted interactively (default `<cwd>/<shareName>`) when omitted — will hang without a TTY. |

| Flag | Default | Description |
|---|---|---|
| `--provider <text>` | — | Provider key, e.g. `azure-fileshare`. Auto-detected if only one is authenticated. |
| `--account <text>` | — | Storage account name. |
| `--share <text>` | — | File share name. |
| `-d`, `--direction <Download\|Upload\|Bidirectional>` | `Download` | Sync direction. |
| `--dry-run` | `false` | Preview changes without transferring files. |
| `--delete` | `false` | Delete orphaned files at the destination. Requires interactive confirmation. |
| `--verify-checksum` | `false` | Verify file integrity using MD5 checksums. |
| `--parallel <int>` | `4` | Maximum parallel file transfers. |
| `--include <glob>` | `[]` | Glob pattern for files to include, e.g. `'*.json'` or `'users/**'`. Repeatable. |
| `--exclude <glob>` | `[]` | Glob pattern for files to exclude, e.g. `'*.tmp'`. Repeatable. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

```bash
pks storage sync --direction download ./local
```

Pulls the resolved share down to `./local`. Read-only, agent-safe, no confirmation prompt.

```bash
pks storage sync --direction upload ./local
```

Pushes `./local` up to the share. A write operation — this triggers the interactive consent gate and fails outright in a non-interactive context.

```bash
pks storage sync --dry-run
```

Previews what a sync would do, with `local-path`, `--account`, and `--share` resolved interactively if not supplied, without transferring anything.

> **Do not commit.** A write is defined as `Direction` being `Upload` or `Bidirectional`, or `--delete` being set. Whenever that's true and `--dry-run` is not set, `sync` requires an interactive terminal (`Capabilities.Interactive`); in a non-interactive context — CI, an agent-spawned non-tty — it hard-fails with exit **1** and the message "Write operations require interactive confirmation and cannot run non-interactively. Only download (read-only) operations are allowed for automated use." When interactive, the confirmation prompt itself defaults to **No** — a bare Enter cancels. Declining prints "Cancelled." and exits **0**, not an error.

Additional behavior to know before running `sync`:

- `--delete` is gated as a write operation even when `Direction` is `Download` — deleting local files not present remotely still goes through the same confirmation flow.
- `--provider` must match a provider's key exactly. An unrecognized or unauthenticated key exits 1 with `"Provider '<key>' is not authenticated."`
- Account and share resolution follows the same interactive-prompt-if-ambiguous pattern as `ls` — supply `--account` and `--share` explicitly to run non-interactively, and note this is still only safe for `Download` direction given the write gate above.
- The progress bar's maximum grows as the provider discovers files during a large-tree sync — a bar whose total keeps climbing is expected, not stuck.
- On completion, a non-empty error list makes the command exit **1** even if some files transferred successfully. Check the summary table's error count and the exit code, not just that files moved.

## Prerequisites
- At least one file-share provider authenticated via [Fileshare](/tools/pks/fileshare) — otherwise every `pks storage` command reports no authenticated storage providers (`list` exits 0; `ls` and `sync` exit 1) and points at `pks fileshare init`.
- Currently the only implemented provider is Azure File Share (provider key `azure-fileshare`). `pks storage` itself is written against the generic `IFileShareProvider` interface, so this branch works unchanged as more providers are added.

## Troubleshooting

**"No authenticated storage providers found."** — No provider has been authenticated yet. Run `pks fileshare init` first, then retry. `list` treats this as a non-error (exit 0); `ls` and `sync` exit 1.

**`ls` or `sync` hangs with no output.** — You omitted `--account` and/or `--share` (and, for `sync`, `local-path`) while running non-interactively, and the command dropped into an interactive selection prompt with no TTY to answer it. Pass `--account`, `--share`, and, for `sync`, a positional `local-path` explicitly.

**`sync --direction upload` exits 1 immediately with a message about non-interactive confirmation.** — This is the write-consent gate working as designed: uploads, bidirectional syncs, and deletes cannot run without a human present to confirm. Run the command from an interactive terminal, or restrict the operation to `--direction download` without `--delete` for unattended use.

**`sync` exits 0 printing "Cancelled."** — The interactive confirmation prompt defaults to No; a bare Enter (or answering no) cancels the sync without transferring anything. This is not an error.

**`ls --json` shows `"truncated": true`.** — More items existed than `--limit` (default 100) returned. Raise `--limit` or narrow `[path]`. In JSON mode this field is the only truncation signal — the human-readable warning banner does not appear here.

**`sync` finishes but exits 1 despite some files transferring.** — The summary's error count is non-empty. Partial success still yields a non-zero exit; check the printed errors and the summary table rather than assuming a non-zero exit means nothing moved.

## See also
- [pks](/tools/pks) — command families and the full 57-group surface pks belongs to.
