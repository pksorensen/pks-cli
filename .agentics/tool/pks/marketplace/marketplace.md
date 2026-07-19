---
title: "pks marketplace"
description: "Register Claude Code plugin marketplaces from a URL or GitHub repo, curate which plugins are enabled, and feed the result into a managed-settings.json."
tags: [reference, cli, claude, plugins]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks marketplace <command> [options]"
examples:
  - command: "pks marketplace add https://marketplace.agentics.dk/ctx/ctx-core"
    description: "Register an internal marketplace and pick plugins"
  - command: "pks marketplace list"
    description: "See every registered marketplace and its counts"
  - command: "pks marketplace show my-marketplace"
    description: "Inspect one marketplace plugin by plugin"
  - command: "pks marketplace enable my-marketplace plugin-a"
    description: "Turn on a single plugin"
  - command: "pks marketplace refresh"
    description: "Re-fetch every source for plugin-list changes"
---

`pks marketplace` manages Claude Code plugin marketplaces registered locally by pks. A marketplace is a `marketplace.json` document following Anthropic's plugin-marketplace schema, fetched from an HTTPS URL or a GitHub repository, and remembered on your machine together with the enabled or disabled state of each plugin it lists.

## Overview

The group has one job: keep a local, curated registry of plugin marketplaces so a second command — `pks claude managed-settings` — can render a `managed-settings.json` for Claude Code from it. `pks marketplace` is the curation half; the render half lives under the `claude` branch.

- **Add from two source kinds.** An arbitrary HTTPS URL, or the `github:owner/repo[@ref]` shorthand that resolves to `https://raw.githubusercontent.com/{owner}/{repo}/{ref}/marketplace.json` with `main` as the default ref.
- **Curate per plugin.** Every plugin carries an enabled or disabled flag you control with `enable` and `disable`.
- **Stay local after the fetch.** Only `add` and `refresh` touch the network. `list`, `show`, `enable`, `disable`, and `remove` are pure edits to one JSON file.
- **Feed distribution.** The curated state is the input to `pks claude managed-settings`, which is how the selection reaches a devcontainer or a team machine.

## What you get

- **A single source of truth on disk.** All registered marketplaces and their plugin state live in `~/.pks-cli/claude-marketplace.json`, written as indented camelCase JSON.
- **Interactive plugin selection.** `pks marketplace add` presents a multi-select checkbox of the plugins the source declares, so you choose what to enable at registration time.
- **A non-interactive path for automation.** `--non-interactive` skips every prompt; pair it with `--enable-all` to register everything enabled.
- **Policy support for URL sources.** When the source is a URL, pks also tries `{url}/policy` and force-enables plugins the policy marks `required` or `installed-default`.
- **Drift pickup without losing your choices.** `pks marketplace refresh` re-fetches the source and preserves the enabled state of plugins that still exist by name.

## How it fits together

You register a marketplace once with `add`. pks fetches the `marketplace.json`, reads its top-level `name` field, and stores an entry keyed by that name — this key is the ID that every other subcommand takes. From then on the plugin list you see is a local snapshot: `list` and `show` read it, `enable` and `disable` edit it, `remove` deletes it, and `refresh` replaces the snapshot with a newly fetched one.

Nothing in this group writes any Claude Code configuration file. The curated registry only reaches Claude Code when you run `pks claude managed-settings`, which loads the same `claude-marketplace.json` and renders `managed-settings.json` to stdout or to a path you pass with `--output`.

- **Curation:** `pks marketplace …` — local registry, plugin enable state.
- **Distribution:** `pks claude managed-settings` — renders the file Claude Code reads.

## Commands

`add` · `list` · `show` · `enable` · `disable` · `remove` · `refresh`

| Command | Purpose |
|---|---|
| `add <SOURCE>` | Fetch a marketplace.json from a URL or `github:` source, apply policy, choose plugins, and register it. |
| `list` | Print every registered marketplace with its enabled and total plugin counts. |
| `show <ID>` | Print full detail for one marketplace, including a per-plugin table. |
| `enable <MARKETPLACE_ID> [PLUGIN_NAMES]` | Set plugins to enabled; omit names to enable all. |
| `disable <MARKETPLACE_ID> [PLUGIN_NAMES]` | Set plugins to disabled; omit names to disable all. |
| `remove [ID]` | Delete a marketplace entry; omit the ID to pick interactively. |
| `refresh [ID]` | Re-fetch sources and rebuild plugin lists; omit the ID to refresh all. |

## Defaults

| Setting | Value |
|---|---|
| Registry file | `~/.pks-cli/claude-marketplace.json` |
| Default GitHub ref | `main` |
| Resolved GitHub path | `https://raw.githubusercontent.com/{owner}/{repo}/{ref}/marketplace.json` |
| Fetch authentication | none — unauthenticated HTTP GET |
| Policy document | `{url}/policy`, URL sources only |

No environment variables configure this group. The registry path is fixed and the file is created on first write.

> **Note.** A corrupt or unparseable `claude-marketplace.json` is treated as empty rather than raising an error, so a damaged file reads back as "nothing registered".

## Next steps

- [Add a marketplace](/tools/pks/marketplace/add) — the two source kinds, policy handling, and the interactive versus automated paths
- [List and inspect marketplaces](/tools/pks/marketplace/list) — find IDs and read the per-plugin state
- [Enable and disable plugins](/tools/pks/marketplace/enable) — curate the plugin set and the traps around silent no-ops
- [Refresh marketplace sources](/tools/pks/marketplace/refresh) — pick up upstream plugin changes and what refresh resets
- [Remove a marketplace](/tools/pks/marketplace/remove) — delete an entry safely, interactively or in CI
- [pks marketplace CLI reference](/tools/pks/marketplace/reference) — every argument and flag in one table
