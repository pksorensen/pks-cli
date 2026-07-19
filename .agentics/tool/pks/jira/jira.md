---
title: "pks jira"
description: "Authenticate pks against Jira Cloud or Server/Data Center, browse a project's issue tree interactively, and export selected issues to local markdown and JSON."
tags: [reference, cli, jira]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks jira <command> [options]"
examples:
  - command: "pks jira init"
    description: "Authenticate against Jira Cloud or Server/Data Center"
  - command: "pks jira browse --project PROJ"
    description: "Browse a project's issue tree and export selections"
  - command: "pks jira browse --jql \"assignee = currentUser()\" --save"
    description: "Run a raw JQL query and save it as a named filter"
  - command: "pks jira config --ac-field customfield_10064"
    description: "Pin the acceptance-criteria custom field explicitly"
---

`pks jira` is the Jira half of the pks CLI: it holds your Jira credentials, and it exports a browsable slice of a project's issues to local markdown and JSON files under `.jira/`.

## Overview

`pks jira` is a three-command group. `pks jira init` authenticates once, storing credentials in the shared pks config store. `pks jira browse` is the working command — it opens an interactive terminal tree of a project's epics, stories, tasks, and subtasks, lets you multi-select issues, and exports each selection to `.jira/<PROJECT>/<slug>/<KEY>.md` and `.json`, downloading attachments alongside. `pks jira config` overrides the auto-discovered acceptance-criteria custom field when a Jira instance uses a nonstandard one.

Use `pks jira browse` whenever you want a project's backlog or a saved JQL slice pulled into local files — for offline reading, for feeding into a coding agent's context, or for diffing against a previous export, since re-running `browse` only re-fetches issues whose `Updated` timestamp changed. Reach for `pks jira config` only after `pks jira browse --debug` reveals the real acceptance-criteria field id and auto-discovery picked the wrong one.

### Prerequisites

- **A Jira Cloud, Server, or Data Center account** with API access — an API token (Cloud), a personal access token (Server/Data Center), or a username and password (Server).
- **Admin or standard project access** to the Jira project you want to browse; `browse` reads issues through the same permissions your credential already has in Jira.
- **An interactive terminal.** The `browse` tree UI reads raw keystrokes and needs a real TTY — it will not run correctly piped or in a non-interactive script.

## Authentication model

Credentials live in the shared pks config store at `~/.pks-cli/`, written with `global: true` — one login covers every project on the machine, not one per repository. Three flows work:

- **Jira Cloud** — email plus an API token, sent as Basic auth (base64 `email:token`).
- **Jira Server or Data Center** — a Personal Access Token, sent as a Bearer token.
- **Jira Server** — username and password, sent as Basic auth.

`pks jira init` offers OAuth 2.0 as a menu choice, but it is not implemented: selecting it prints a message and exits with code 1. Use one of the three flows above.

Every credential is validated with a live `GET /myself` call before it is stored; on Cloud, validation also resolves a `cloudId`. Only `browse` calls the stored-credential check first and fails closed — "Not authenticated. Run 'pks jira init' first." (exit 1) — if no base URL is stored. `config` never checks: it only reads and writes the local `ac_field_id` mapping, so it works even if `pks jira init` was never run.

## Synopsis

```text
pks jira <command> [options]
```

```text
init      Authenticate against Jira Cloud or Server/Data Center
browse    Browse a project's issue tree and export selections
config    View or set the acceptance-criteria custom field mapping
```

## init

Interactively authenticates pks against a Jira instance and stores the credentials in `~/.pks-cli/`. Prompts for deployment type — Cloud vs. Server/Data Center — then the matching credential flow, validates it with a live `GET /myself` call, and on success prints a summary table plus a tip to run `pks jira browse`. Run this before any other `jira` subcommand; it is the only place credentials are set.

| Flag | Description |
|---|---|
| `--force` | Force re-authentication even if already authenticated. |
| `-v`, `--verbose` | Currently a no-op — no `jira` subcommand reads this flag. |
| `--debug` | Show HTTP request/response details for troubleshooting. |

```bash
pks jira init
```

First-time auth. If already authenticated, this prints the stored credential summary and exits without prompting.

```bash
pks jira init --force
```

Re-runs the auth wizard even though credentials already exist, overwriting them.

> **Note.** Without `--force`, `init` silently no-ops when already authenticated — it will not let you switch accounts or fix a bad token unless you pass `--force`.

## browse

An interactive terminal tree browser for Jira issues — epics, stories, tasks, and subtasks — with lazy child-loading, multi-select checkboxes, and export to markdown and JSON. `browse` resolves what to show, in order: an explicit `url` argument (JQL is extracted from a `?jql=...` query string, or the whole string is treated as raw JQL if no `jql=` param is found), then `--jql`, then a saved-filter picker if any filters were previously saved with `--save`, then an interactive project picker.

Once issues load, the tree responds to arrow keys, space, `a` (select all, recursively loading and expanding the entire visible tree), enter, and escape. Selecting a parent auto-loads and cascades selection to all its children. On enter, every selected issue exports to `{output}/{PROJECT}/{slug}/{KEY}.md` and `{KEY}.json`, with any attachments downloaded into an `attachments/` subfolder and child issues nested inside their parent's directory. Detail fetch — comments, worklogs, attachments, changelog, full description, and acceptance criteria — runs with 10-way concurrent throttling, and only for issues whose `Updated` timestamp differs from a `.json` file already present in the output directory.

### Arguments

| Argument | Required | Description |
|---|---|---|
| `url` | no | A Jira URL with a `?jql=...` query string, or a raw JQL query string. |

### Options

| Flag | Description |
|---|---|
| `--project`, `-p <key>` | Project key to browse directly, skipping the interactive project picker. |
| `--jql <query>` | Custom JQL query to run directly, skipping the project picker and saved-filter picker. |
| `--save` | Save the resolved JQL as a named filter for future `browse` runs. |
| `--name <text>` | Name for the saved filter when using `--save`; defaults to a label generated from the JQL. |
| `--output`, `-o <dir>` | Output directory for exported markdown and JSON files. Default `.jira`. |
| `-v`, `--verbose` | Currently a no-op — no `jira` subcommand reads this flag. |
| `--debug` | Show HTTP request/response details for troubleshooting. |

```bash
pks jira browse
```

No arguments — shows saved filters, if any, then falls through to an interactive project picker.

```bash
pks jira browse --project PROJ
```

Browse a specific project's issues directly.

```bash
pks jira browse "https://mycompany.atlassian.net/issues/?jql=project=PROJ+AND+sprint+in+openSprints()"
```

Paste a Jira filter or search URL directly; the JQL is extracted from the query string.

```bash
pks jira browse --jql "assignee = currentUser() ORDER BY created DESC" --save --name "My issues"
```

Run a raw JQL query and save it as a named filter for quick access next time.

```bash
pks jira browse --project PROJ --output ./exports/proj
```

Export to a custom directory instead of the default `.jira`.

You should see a live tree open in the terminal; pressing enter with issues selected writes files under the output directory and prints a summary count when it finishes.

> **Note.** Only epics, stories, tasks, features, and initiatives are treated as capable of having children — bugs and subtasks never show an expand arrow, even if linked. Attachment download only happens when the local file doesn't already exist; an existing attachment is never re-verified or re-downloaded.

## config

Views or sets the custom-field mapping used to extract acceptance-criteria text during export. Jira instances vary in which custom field — "Acceptance Criteria", "Definition of Done", or another label — holds this text, so `config` exists to override auto-discovery when it fails or picks the wrong field.

| Flag | Description |
|---|---|
| `--ac-field <id>` | Set the acceptance-criteria custom field id, for example `customfield_10064`. |
| `--show` | Show current field mappings. Running `config` with no `--ac-field` already shows the current mapping regardless of this flag. |
| `-v`, `--verbose` | Inherited from the shared `jira` options but a no-op — nothing in `config` reads it. |
| `--debug` | Inherited from the shared `jira` options but a no-op here — `config` never makes HTTP calls, so there is nothing to trace. To find the real field id with `--debug`, use `pks jira browse --debug` instead. |

```bash
pks jira config
```

Shows the current acceptance-criteria field mapping, or "not set (auto-discover)".

```bash
pks jira config --ac-field customfield_10064
```

Pins the acceptance-criteria field explicitly after finding its real id with `pks jira browse --debug`.

> **Note.** Setting a field takes effect immediately, with no confirmation, and applies globally to every future export on the machine — not per project.

## Troubleshooting

> **Note.** "Not authenticated. Run 'pks jira init' first." from `browse` means no base URL is stored yet, or credentials were cleared. Run `pks jira init`. (`config` never performs this check — it only touches the local `ac_field_id` mapping — so it will not surface this message even when unauthenticated.)

> **Note.** The `browse` tree hangs or renders garbled output when run non-interactively or piped — it reads raw keystrokes with `Console.ReadKey` and needs a real TTY. Run it directly in a terminal.

> **Note.** If exported issues aren't picking up refreshed content, check whether the output directory still holds the previous export's `.json` files — `browse` compares `Updated` timestamps against files already present in `--output` to decide what to re-fetch, so deleting or moving that directory forces a full re-fetch on the next run.

## See also

- [pks](/tools/pks) — the command groups pks jira sits alongside
