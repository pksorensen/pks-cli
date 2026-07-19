---
title: "Remove a marketplace"
description: "Delete a registered marketplace and its plugin state from the local registry, by ID with a confirmation prompt or interactively from a picker."
tags: [how-to, cli, claude, plugins]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks marketplace remove [ID] [options]"
examples:
  - command: "pks marketplace remove my-marketplace"
    description: "Remove by ID with a confirmation prompt"
  - command: "pks marketplace remove --yes"
    description: "Pick interactively, then remove without confirming"
---

`pks marketplace remove` deletes a marketplace entry, along with its plugin list and every enabled or disabled choice you made on it, from the local registry. The deletion is permanent — there is no undo and no backup copy.

## 1. Prerequisites

- **The marketplace ID**, unless you want the interactive picker. Run `pks marketplace list` to find it.
- **An interactive terminal**, unless you pass both an ID and `--yes`.

## 2. Remove by ID

```bash
pks marketplace remove my-marketplace
```

A confirmation prompt appears. It defaults to No, so pressing Enter cancels rather than confirms.

## 3. Remove interactively

Omit the ID and pks shows a selection list of every registered marketplace:

```bash
pks marketplace remove
```

Choose the entry, then confirm.

## 4. Remove without confirming

`--yes` skips the confirmation prompt:

```bash
pks marketplace remove my-marketplace --yes
```

`--yes` skips only the confirmation. Combined with no ID, the interactive picker still runs:

```bash
pks marketplace remove --yes
```

## 5. Verify

```bash
pks marketplace list
```

The entry is gone from the table.

## 6. Re-render Claude Code settings

Removing an entry changes what `pks claude managed-settings` produces, but does not itself update any Claude Code file. Re-render and redistribute:

```bash
pks claude managed-settings
```

## Options

| Flag | Description |
|---|---|
| `--yes` | Skip the confirmation prompt. |

| Argument | Required | Description |
|---|---|---|
| `ID` | no | Marketplace ID to remove. Omit to pick interactively. |

## Troubleshooting

**The command hangs in CI.** Running without an ID falls through to an interactive selection prompt, which has no terminal to read from. In automation always pass both the ID and `--yes`.

**Enter did nothing but the marketplace is still there.** The confirmation prompt defaults to No. Answer yes explicitly, or pass `--yes`.

**The plugin curation is gone.** Removal deletes the entry's full plugin and enabled-state record. Re-adding the source registers a fresh entry with default state; the previous selection is not recoverable.

**Removing the wrong entry.** IDs are the marketplace `name` from the fetched document, or the `--label` override. Confirm against `pks marketplace show <ID>` before removing.

## Next steps

- [List and inspect marketplaces](/tools/pks/marketplace/list) — confirm the ID before deleting
- [Add a marketplace](/tools/pks/marketplace/add) — re-register a source you removed
- [pks marketplace CLI reference](/tools/pks/marketplace/reference) — the full argument and flag surface
