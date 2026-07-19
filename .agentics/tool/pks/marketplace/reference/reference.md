---
title: "pks marketplace CLI reference"
description: "Complete command, argument, and flag reference for pks marketplace — add, list, show, enable, disable, remove, refresh, plus source forms and local storage."
tags: [reference, cli, claude, plugins]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks marketplace <command> [options]"
examples:
  - command: "pks marketplace add github:owner/repo --enable-all --non-interactive"
    description: "Register a GitHub source with all plugins enabled"
  - command: "pks marketplace list"
    description: "Print the registry table"
  - command: "pks marketplace show my-marketplace"
    description: "Print one marketplace plugin by plugin"
  - command: "pks marketplace disable my-marketplace plugin-a"
    description: "Disable one plugin"
  - command: "pks marketplace refresh"
    description: "Re-fetch every registered source"
  - command: "pks marketplace remove my-marketplace"
    description: "Delete an entry after confirming"
---

`pks marketplace` is the branch of the pks CLI that registers Claude Code plugin marketplaces, records each plugin's enabled state, and keeps that state in one local JSON file. A marketplace is a `marketplace.json` document following Anthropic's plugin-marketplace schema, fetched from an HTTPS URL or a GitHub repository.

All state lives in `~/.pks-cli/claude-marketplace.json`, written as indented camelCase JSON and created on first write. Only `add` and `refresh` make network calls; every other subcommand edits that file directly. The consumer of the file is `pks claude managed-settings`, which renders `managed-settings.json` for Claude Code from the same registry.

## Synopsis

```text
pks marketplace <command> [options]
```

```text
add        Fetch and register a marketplace from a URL or github: source
list       List every registered marketplace with plugin counts
show       Show one marketplace in full, with a per-plugin table
enable     Set plugins to enabled (omit names to enable all)
disable    Set plugins to disabled (omit names to disable all)
remove     Delete a marketplace entry from local storage
refresh    Re-fetch sources and rebuild plugin lists
```

### Source forms

| Form | Resolves to |
|---|---|
| `https://…` | The URL, fetched verbatim. |
| `github:owner/repo` | `https://raw.githubusercontent.com/owner/repo/main/marketplace.json` |
| `github:owner/repo@ref` | `https://raw.githubusercontent.com/owner/repo/{ref}/marketplace.json` |

Fetches are unauthenticated HTTP GETs. Private repositories and URLs requiring credentials fail with a fetch error.

### Storage

| Setting | Value |
|---|---|
| Registry file | `~/.pks-cli/claude-marketplace.json` |
| Format | indented camelCase JSON |
| Entry key | the marketplace `name` field, compared case-insensitively |
| Corrupt-file behavior | treated as an empty registry, no error |

No environment variables configure this group.

## add

Fetches `marketplace.json` from the source and requires a non-empty top-level `name`; without it the command fails and writes nothing. For URL sources it then attempts to fetch a policy document at `{url}/policy`, force-enabling plugins marked `required` or `installed-default` and flagging `required` ones as non-disableable. It then either prompts with a multi-select checkbox for the remaining plugins, or — under `--non-interactive` — enables all with `--enable-all` or none without it. The resulting entry is written into the registry, overwriting any existing entry with the same name.

| Argument | Required | Description |
|---|---|---|
| `SOURCE` | yes | Marketplace source: URL, `github:owner/repo`, or `github:owner/repo@ref`. |

| Flag | Description |
|---|---|
| `--label <LABEL>` | Optional display label for the marketplace. |
| `--non-interactive` | Skip interactive prompts. |
| `--enable-all` | Enable all plugins when adding. Read only when `--non-interactive` is also passed. |

Policy fetching never runs for `github:` sources, and a failed policy fetch — 404, timeout beyond ten seconds, or invalid JSON — is swallowed silently.

```bash
pks marketplace add https://marketplace.agentics.dk/ctx/ctx-core
pks marketplace add github:owner/repo --enable-all --non-interactive
pks marketplace add github:owner/repo@v2 --non-interactive
```

## list

Prints a table of every registered marketplace: ID, label, source, enabled-of-total plugin count, and added date. Read-only, no network call. With an empty registry it prints a hint that names `pks claude marketplace add` — a stale string in the source; the real path is `pks marketplace add`.

Takes no arguments and no flags.

```bash
pks marketplace list
```

## show

Prints full detail for one marketplace: label, source type with URL or repository and ref, added and last-fetched timestamps, and a per-plugin table of name, version, enabled state, and description. Read-only, and reflects only what was captured at the last `add` or `refresh`.

| Argument | Required | Description |
|---|---|---|
| `ID` | yes | Marketplace ID. Exits 1 with a not-found message when unmatched. |

```bash
pks marketplace show my-marketplace
```

## enable

Sets `Enabled` to true on some or all plugins in a registered marketplace and saves the registry. No network call. Plugin names that are not in the marketplace's plugin list are ignored without a warning.

| Argument | Required | Description |
|---|---|---|
| `MARKETPLACE_ID` | yes | Marketplace ID. |
| `PLUGIN_NAMES` | no | Plugin names to enable. Omit to enable all. |

Takes no flags.

```bash
pks marketplace enable my-marketplace plugin-a
pks marketplace enable my-marketplace plugin-a plugin-b
pks marketplace enable my-marketplace
```

## disable

Mirror of `enable`: sets `Enabled` to false on some or all plugins and saves the registry. It does not consult a plugin's `required` flag, so a plugin marked required by a policy during `add` can be disabled here with no warning. Unknown plugin names are ignored, same as `enable`.

| Argument | Required | Description |
|---|---|---|
| `MARKETPLACE_ID` | yes | Marketplace ID. |
| `PLUGIN_NAMES` | no | Plugin names to disable. Omit to disable all. |

Takes no flags.

```bash
pks marketplace disable my-marketplace plugin-a
pks marketplace disable my-marketplace
```

## remove

Deletes a marketplace entry, its plugin list, and its enabled state from the registry. Prompts for confirmation unless `--yes`; the prompt defaults to No, so a bare Enter cancels. With no ID it first shows an interactive selection list, which makes an ID-less invocation unusable in CI.

| Argument | Required | Description |
|---|---|---|
| `ID` | no | Marketplace ID to remove. Omit to pick interactively. |

| Flag | Description |
|---|---|
| `--yes` | Skip the confirmation prompt. |

```bash
pks marketplace remove my-marketplace
pks marketplace remove my-marketplace --yes
```

## refresh

Re-fetches `marketplace.json` from each registered marketplace's original source, or from one marketplace by ID, and rebuilds its plugin list. Plugins that still exist under the same name keep their enabled state; new plugins are added disabled; plugins removed upstream are dropped. The policy document that `add` applies is not re-fetched, and the `required` flag is not carried over, so policy protection set at add time is reset. In a bulk run, per-marketplace failures are reported and successful refreshes are still saved, but the command exits 1 if any failed.

| Argument | Required | Description |
|---|---|---|
| `ID` | no | Marketplace ID to refresh. Omit to refresh all. |

Takes no flags.

```bash
pks marketplace refresh my-marketplace
pks marketplace refresh
```

## See also

- [pks marketplace](/tools/pks/marketplace) — the group landing page and mental model
- [Add a marketplace](/tools/pks/marketplace/add) — source forms, policy handling, and the interactive flow
- [Enable and disable plugins](/tools/pks/marketplace/enable) — curating the plugin set
- [Refresh marketplace sources](/tools/pks/marketplace/refresh) — what a re-fetch preserves and resets
- [Remove a marketplace](/tools/pks/marketplace/remove) — deleting an entry safely
