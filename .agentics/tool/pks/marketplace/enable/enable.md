---
title: "Enable and disable plugins"
description: "Turn plugins on or off inside a registered marketplace, one at a time or all at once, and understand why the change does not reach Claude Code by itself."
tags: [how-to, cli, claude, plugins]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks marketplace enable <MARKETPLACE_ID> [PLUGIN_NAMES]"
examples:
  - command: "pks marketplace enable my-marketplace plugin-a"
    description: "Enable one specific plugin"
  - command: "pks marketplace enable my-marketplace"
    description: "Enable every plugin in the marketplace"
  - command: "pks marketplace disable my-marketplace plugin-a"
    description: "Disable one specific plugin"
  - command: "pks marketplace disable my-marketplace"
    description: "Disable every plugin in the marketplace"
---

`pks marketplace enable` and `pks marketplace disable` set the enabled flag on plugins inside a marketplace you already registered. Both are local edits to the last-fetched plugin list, with no network call and no change to any Claude Code file.

## 1. Prerequisites

- **A registered marketplace.** Add one with [pks marketplace add](/tools/pks/marketplace/add).
- **The marketplace ID.** Run `pks marketplace list` to find it.
- **The exact plugin names.** Run `pks marketplace show <ID>` for the per-plugin table. Names are matched literally.

## 2. Enable specific plugins

Name one or more plugins after the marketplace ID:

```bash
pks marketplace enable my-marketplace plugin-a
pks marketplace enable my-marketplace plugin-a plugin-b
```

Each named plugin is set to enabled and the registry is saved.

## 3. Enable everything

Omit the plugin names to enable every plugin the marketplace lists:

```bash
pks marketplace enable my-marketplace
```

## 4. Disable plugins

`disable` is the exact mirror — same arguments, opposite result:

```bash
pks marketplace disable my-marketplace plugin-a
pks marketplace disable my-marketplace
```

## 5. Verify

Read the per-plugin table back and check the enabled column:

```bash
pks marketplace show my-marketplace
```

`pks marketplace list` also reflects the change in its enabled-of-total count.

## 6. Apply to Claude Code

Neither command touches Claude Code configuration. The curated state only reaches Claude Code when you render it:

```bash
pks claude managed-settings
```

That command reads the same registry and writes `managed-settings.json` to stdout, or to a path you pass with `--output`.

## Options

Neither command takes flags.

| Argument | Required | Description |
|---|---|---|
| `MARKETPLACE_ID` | yes | Marketplace ID. |
| `PLUGIN_NAMES` | no | Plugin names to enable or disable. Omit to affect all plugins. |

## Troubleshooting

**Nothing changed after naming a plugin.** Plugin names that do not appear in the marketplace's plugin list are ignored without a warning or an error, so a typo fails quietly. Copy the name from `pks marketplace show <ID>` rather than typing it.

**Claude Code still loads the old plugin set.** These commands edit only the pks registry. Re-run `pks claude managed-settings` and redistribute the rendered file.

**A required plugin was disabled anyway.** `disable` does not check the `required` flag that a policy document applied during `add`. A plugin marked required can be disabled here with no warning, which silently defeats the policy. Re-run `pks marketplace add` against the URL source to reapply the policy.

**Enabled state disappeared for a plugin.** A plugin that vanishes from the upstream source is dropped on the next `pks marketplace refresh`, and a newly appearing plugin is added disabled. Check the source document before assuming the registry lost data.

## Next steps

- [List and inspect marketplaces](/tools/pks/marketplace/list) — confirm the resulting plugin state
- [Refresh marketplace sources](/tools/pks/marketplace/refresh) — how a re-fetch treats your enable state
- [Add a marketplace](/tools/pks/marketplace/add) — where policy-driven required plugins come from
- [pks marketplace CLI reference](/tools/pks/marketplace/reference) — the full argument and flag surface
