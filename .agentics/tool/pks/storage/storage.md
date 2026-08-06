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
  - command: "pks storage rm tmp/ --recursive --dry-run"
    description: "Show exactly which files a delete would remove, without deleting"
  - command: "pks storage rm reports/old.csv"
    description: "Delete a file (permanent; requires an approved consent grant)"
---

`pks storage` is the provider-agnostic operational surface for browsing and moving files through whichever file-share provider you've authenticated with `pks fileshare init`. It never authenticates anything itself, and it deliberately splits its commands by risk: reads run freely, writes stop for a human.

## Overview
`pks storage` layers four commands — `list`, `ls`, `sync`, and `rm` — over the `IFileShareProvider` interface, so the same commands work against any registered provider without provider-specific tooling like Azure Storage Explorer or the `az` CLI. Today exactly one provider is implemented: Azure File Share (`azure-fileshare`).

- **Discover** what you have access to with `list`.
- **Browse** a specific share's contents with `ls`, including in `--json` mode for agent consumption.
- **Move files** in bulk with `sync` — download, upload, or bidirectional, with dry-run preview and checksum verification.
- **Delete files** with `rm` — permanent, target-by-target, and gated behind an approval a caller cannot grant itself.

## What you get
- **Provider-agnostic browsing.** `list` and `ls` work against any authenticated `IFileShareProvider` without touching provider-specific tools.
- **Agent-safe reads.** `list` and `ls` are read-only and need no path or account argument to get started; `ls --json` is built specifically for machine consumption.
- **A hard write gate.** `sync` refuses to upload or mirror bidirectionally without an interactive human confirmation — and hard-fails outright in a non-interactive context rather than silently skipping the prompt.
- **Consent-gated deletes.** `rm` resolves the exact file list first, then puts it through the `storage.delete` action guard. A caller without a second factor or a terminal gets a request id instead of a deletion, and a human approves it out of band with [`pks consent approve`](/tools/pks/consent).
- **Bulk transfer controls.** `sync` supports dry-run preview, MD5 checksum verification, glob include/exclude filtering, parallel transfer, and resume — a re-run skips files already downloaded at the same size instead of starting over.

## How it fits together
`pks storage` sits on top of `pks fileshare`, which owns authentication. Every `pks storage` command starts by calling `FileShareProviderRegistry.GetAuthenticatedProvidersAsync()`; if nothing is authenticated, `list` prints a warning and exits 0, while `ls` and `sync` exit 1, both pointing at `pks fileshare init`. If more than one provider is authenticated, `ls` and `sync` prompt interactively to pick one (or accept `--account`/`--share`/`--provider` up front), while `list` aggregates across all of them.

The load-bearing design decision is the risk split baked into the branch description itself: "download is agent-safe, upload requires consent." `list` and `ls` never write. `sync` checks whether the operation is a write (`Direction` is `Upload` or `Bidirectional`) before doing anything — if so, and `--dry-run` isn't set, it requires an interactive terminal and an explicit confirmation that defaults to **No**.

`rm` sits a level above that. An interactive confirmation is not a boundary against an agent, because an agent can drive a terminal; so deletion routes through `IActionGuard` as a *resource-scoped* request, and approval binds to a fingerprint of the resolved file list. Approving a delete of three files therefore cannot be spent on a fourth that appeared afterwards, and the grant is single-use and time-boxed by default.

- **Read path:** `list` → `ls` → done. No confirmation, no interactivity requirement.
- **Write path:** `sync --direction upload` (or `--direction bidirectional`) → interactive confirmation required → hard failure if run non-interactively.
- **Delete path:** `rm --dry-run` (free, read-only) → `rm` → action guard → either a second factor on a TTY, or a consent request id a human approves elsewhere.

## Commands
`list` · `ls` · `sync` · `rm`. Full flags, arguments, and examples for each are below.

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
| `--delete` | `false` | **Not implemented.** Passing it exits 1 pointing at `pks storage rm`. |
| `--verify-checksum` | `false` | Verify file integrity using MD5 checksums. |
| `--force` | `false` | Re-download every matching file, including ones already present locally at the same size. |
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

> **Do not commit.** A write is defined as `Direction` being `Upload` or `Bidirectional`. Whenever that's true and `--dry-run` is not set, `sync` requires an interactive terminal (`Capabilities.Interactive`); in a non-interactive context — CI, an agent-spawned non-tty — it hard-fails with exit **1** and the message "Write operations require interactive confirmation and cannot run non-interactively. Only download (read-only) operations are allowed for automated use." When interactive, the confirmation prompt itself defaults to **No** — a bare Enter cancels. Declining prints "Cancelled." and exits **0**, not an error.

Additional behavior to know before running `sync`:

- `--delete` does nothing and now says so: it exits **1** before contacting any provider. It never had an implementation — the provider has no orphan detection and the summary's "Files deleted" row was always 0 — so it used to make a mirror look like it ran. Use `pks storage rm` to delete remote files, and delete local files yourself.
- **Download resumes; it is still not a mirror.** A re-run skips any file whose local copy already matches the remote one — same size, and not superseded by a newer remote timestamp when the listing carries one — and reports them as `Already up to date`. An interrupted transfer of 216 files therefore picks up near where it stopped rather than starting over. `--force` re-fetches everything. `Files skipped` still counts glob exclusions only; the two rows are separate. Upload remains a blind overwrite, and neither direction removes anything: deleting a local file and re-running `sync` re-downloads it rather than deleting it remotely.
- **Interrupted downloads cannot corrupt the resume.** Each file lands in `<name>.pks-part` and is renamed into place only once the transfer completes, so a killed run leaves either the whole file or a `.pks-part` leftover — never a short file that the size check would mistake for a finished one. Stray `.pks-part` files are safe to delete.
- **Long syncs refresh their own token.** The share client mints access tokens on demand and renews them five minutes before the STS-reported expiry, so a multi-hour transfer no longer dies partway with a 403. If the stored refresh token itself has lapsed, the sync fails up front telling you to run `pks fileshare init` again.
- `--provider` must match a provider's key exactly. An unrecognized or unauthenticated key exits 1 with `"Provider '<key>' is not authenticated."`
- Account and share resolution follows the same interactive-prompt-if-ambiguous pattern as `ls` — supply `--account` and `--share` explicitly to run non-interactively, and note this is still only safe for `Download` direction given the write gate above.
- The progress bar's maximum grows as the provider discovers files during a large-tree sync — a bar whose total keeps climbing is expected, not stuck.
- On completion, a non-empty error list makes the command exit **1** even if some files transferred successfully. Check the summary table's error count and the exit code, not just that files moved.

### pks storage rm

Permanently deletes files from a share. Azure Files has no recycle bin, so this command is built around one rule: **the exact target list is resolved and shown before anything is authorised**, and approval binds to that list rather than to the path you typed.

```text
pks storage rm <path> [options]
```

| Argument | Required | Description |
|---|---|---|
| `<path>` | yes | File or directory path within the share. There is no way to target a whole share implicitly. |

| Flag | Default | Description |
|---|---|---|
| `--account <text>` | — | Storage account name. |
| `--share <text>` | — | File share name. |
| `-r`, `--recursive` | `false` | Include files in subdirectories. Without it, a directory path yields only its immediate files. |
| `--dry-run` | `false` | Resolve and print the targets, then stop. Never asks for approval, never deletes. |
| `--yes` | `false` | Skip the final confirmation. Does **not** skip the approval gate. |
| `--json` | `false` | Print the resolved target list as JSON. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

```bash
pks storage rm tmp/ --recursive --dry-run
```

Prints every file the delete would remove, with sizes and a total. Read-only and free of any gate — this is the safe way for an agent to establish blast radius before asking a human for anything.

```bash
pks storage rm reports/old.csv --account acct --share data
```

Resolves the single file, then requires approval for the `storage.delete` action scoped to `azure-fileshare:acct/data`.

> **Approval binds to targets, not to patterns.** The guard receives the resolved paths and hashes them. A grant approved for `a.csv` and `b.csv` is rejected the moment the resolved set differs — including when a third file has appeared in the directory since. That is deliberate: it closes the gap where a caller gets approval for a small set and then widens it before executing.

The gate resolves in one of three ways:

- **A matching grant exists** (approved earlier with `pks consent approve`) → it is spent and the delete proceeds.
- **A second factor is enrolled and there is a terminal** → a TOTP challenge, as with any other guarded action.
- **Neither** → a consent request is filed and the command exits **1**, printing the request id and the exact command a human should run. Nothing is deleted. Re-running the identical command after approval succeeds.

Unlike most guarded actions, `storage.delete` is **fail-closed**: an unenrolled authenticator does not wave it through the way it does for `vm.start`. Deletion always needs an explicit yes from somewhere.

Additional behavior to know:

- The match table prints at most 25 paths, then `… and N more`. The count and total size always reflect the full set.
- `--dry-run` exits **1** when nothing matches, which makes it usable as a "does this exist" probe.
- On partial failure — some files deleted, some errored — the command prints each error and exits **1**. Files already deleted stay deleted; there is no rollback.

## Prerequisites
- At least one file-share provider authenticated via [Fileshare](/tools/pks/fileshare) — otherwise every `pks storage` command reports no authenticated storage providers (`list` exits 0; `ls` and `sync` exit 1) and points at `pks fileshare init`.
- Currently the only implemented provider is Azure File Share (provider key `azure-fileshare`). `pks storage` itself is written against the generic `IFileShareProvider` interface, so this branch works unchanged as more providers are added.

## Troubleshooting

**"No authenticated storage providers found."** — No provider has been authenticated yet. Run `pks fileshare init` first, then retry. `list` treats this as a non-error (exit 0); `ls` and `sync` exit 1.

**`ls` or `sync` hangs with no output.** — You omitted `--account` and/or `--share` (and, for `sync`, `local-path`) while running non-interactively, and the command dropped into an interactive selection prompt with no TTY to answer it. Pass `--account`, `--share`, and, for `sync`, a positional `local-path` explicitly.

**`sync --direction upload` exits 1 immediately with a message about non-interactive confirmation.** — This is the write-consent gate working as designed: uploads and bidirectional syncs cannot run without a human present to confirm. Run the command from an interactive terminal, or restrict the operation to `--direction download` for unattended use.

**`sync --delete` exits 1 with "--delete is not implemented".** — Deliberate. The flag never removed anything; failing is better than reporting a mirror that didn't happen. Delete explicitly with `pks storage rm <path> --recursive`.

**`rm` exits 1 with "Approval required for 'storage.delete'" and a request id.** — The guard could not be satisfied in-band, either because no authenticator is enrolled or because there is no terminal to type a code into. Nothing was deleted. Have a human run `pks consent show <id>` to review the target list and `pks consent approve <id>` to grant, then re-run the identical `rm` command. The grant is single-use and expires in 10 minutes by default.

**`rm` reports "Nothing matches".** — The path resolved to no files. A directory path only yields its immediate files unless you pass `--recursive`; a path that doesn't exist yields nothing at all. Confirm it with `pks storage ls <path>` first.

**`sync` exits 0 printing "Cancelled."** — The interactive confirmation prompt defaults to No; a bare Enter (or answering no) cancels the sync without transferring anything. This is not an error.

**`ls --json` shows `"truncated": true`.** — More items existed than `--limit` (default 100) returned. Raise `--limit` or narrow `[path]`. In JSON mode this field is the only truncation signal — the human-readable warning banner does not appear here.

**A long `sync` died partway with a 403.** — Fixed in the client: tokens are now renewed five minutes before they expire instead of being minted once at the start. Re-run the same command — it resumes over what already landed rather than starting from file 1. If the 403 comes back immediately on the retry, the stored refresh token has lapsed rather than the access token: run `pks fileshare init`.

**A re-run reports `Already up to date` for files you wanted refreshed.** — The local copy matches the remote one on size, and no newer remote timestamp contradicted it. Pass `--force` to re-download regardless, or delete the local files first.

**Leftover `.pks-part` files in the local directory.** — A download was interrupted before its file was renamed into place. They are inert scratch files; delete them. The corresponding real file will be re-fetched on the next run because it either doesn't exist or has the wrong size.

**`sync` finishes but exits 1 despite some files transferring.** — The summary's error count is non-empty. Partial success still yields a non-zero exit; check the printed errors and the summary table rather than assuming a non-zero exit means nothing moved.

## See also
- [Consent](/tools/pks/consent) — approving the scoped grants `rm` requires.
- [Actions](/tools/pks/actions) — the action catalog `storage.delete` belongs to.
- [pks](/tools/pks) — command families and the full 57-group surface pks belongs to.
