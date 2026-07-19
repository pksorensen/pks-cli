---
title: "List and inspect marketplaces"
description: "Read the local marketplace registry: list every entry with its plugin counts, then open one by ID for a per-plugin table of names, versions, and state."
tags: [how-to, cli, claude, plugins]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks marketplace list && pks marketplace show <ID>"
examples:
  - command: "pks marketplace list"
    description: "See every registered marketplace and its counts"
  - command: "pks marketplace show my-marketplace"
    description: "Read one marketplace plugin by plugin"
---

`pks marketplace list` and `pks marketplace show` are the read side of the registry. Both are local: they print what was captured at the last `add` or `refresh` and make no network call, so what you see is a snapshot, not the live state of the source.

## 1. Prerequisites

- **At least one registered marketplace.** Register one first with [pks marketplace add](/tools/pks/marketplace/add). With an empty registry, `list` prints a hint instead of a table.

## 2. List everything

```bash
pks marketplace list
```

The output is a table with one row per marketplace: ID, label, source, an enabled-of-total plugin count, and the date the entry was added. Use it to find the exact ID that the other subcommands require.

## 3. Inspect one marketplace

Pass the ID from the list to `show`:

```bash
pks marketplace show my-marketplace
```

You get the label, the source type together with its URL or its repository and ref, the added and last-fetched timestamps, and a per-plugin table of name, version, enabled state, and description. This is the view to read before running `enable` or `disable`, because plugin names must match exactly.

## Options

Neither command takes flags.

| Command | Argument | Required | Description |
|---|---|---|---|
| `pks marketplace list` | — | — | No arguments. |
| `pks marketplace show` | `ID` | yes | Marketplace ID. |

## Troubleshooting

**`show` exits with "not found".** The ID is the marketplace `name` from the fetched document, or the `--label` override supplied at add time — not the source URL or the `owner/repo` string. Run `pks marketplace list` and copy the ID from the first column.

**An empty list after adding something.** A registry file that cannot be parsed is treated as empty rather than raising an error, so a damaged `~/.pks-cli/claude-marketplace.json` reads back as nothing registered. Re-add the marketplace to rebuild the file.

**The empty-registry hint names the wrong command.** When nothing is registered, `list` prints a hint referring to `pks claude marketplace add`. That path is stale in the source message. The real command is `pks marketplace add`.

**The plugin list looks out of date.** It is a snapshot. Run [pks marketplace refresh](/tools/pks/marketplace/refresh) to re-fetch the source before trusting the table.

## Next steps

- [Enable and disable plugins](/tools/pks/marketplace/enable) — act on what the table shows
- [Refresh marketplace sources](/tools/pks/marketplace/refresh) — bring the snapshot up to date
- [Remove a marketplace](/tools/pks/marketplace/remove) — drop an entry you no longer track
- [pks marketplace CLI reference](/tools/pks/marketplace/reference) — the full argument and flag surface
