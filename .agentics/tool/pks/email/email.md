---
title: "pks email"
description: "Export an Outlook or Exchange mailbox via Microsoft Graph into a date-organized tree of Markdown files with frontmatter and saved attachments."
tags: [reference, cli, microsoft-graph, email]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks email export [options]"
examples:
  - command: "pks ms-graph register"
    description: "Required one-time sign-in before the first export"
  - command: "pks email export"
    description: "Export the inbox into ./.emails"
  - command: "pks email export --after 2026-01-01 --folder inbox"
    description: "Export inbox messages received on or after a date"
  - command: "pks email export -o ./my-emails --max 100"
    description: "Export the 100 most recent messages into a custom folder"
---

`pks email` exports messages from a Microsoft Graph mail folder into local Markdown files, one file per message, with attachments saved alongside them.

## Overview

`pks email` has a single subcommand, `export`. It pages through a Microsoft Graph mail folder — `inbox` by default — applying any date, sender, or subject filters you give it, and writes each message as a Markdown file with YAML frontmatter under a date-based folder tree. HTML bodies are converted to Markdown; attachments land in a sibling `attachments/` folder next to each message.

- **Build a local archive.** Turn a mailbox, or a filtered slice of one, into a greppable, git-able set of files.
- **Feed an agent.** Export a project inbox so a coding agent can read correspondence as plain Markdown context.
- **Re-run incrementally.** A second run skips messages that already have an output file, so it picks up new mail without re-fetching what you already have.

`pks email` is not a sync tool. It never diffs or updates content for a message it has already exported, and it does not detect edits or deletes made on the server — only `--overwrite` forces a message to be re-fetched and rewritten.

## What you get

- **One Markdown file per message**, with frontmatter (subject, from, to/cc, date, message ID, conversation ID, importance, read state, has-attachments flag, categories, web link, export timestamp) and a Markdown-converted body.
- **A predictable, date-based path** for every message, so a second export run can tell what it has already fetched.
- **Attachments saved alongside the message**, decoded from Graph's base64 payload, unless you pass `--no-attachments`.
- **Server-side filtering** by date range, sender, and subject substring, so you can scope an export before it starts.
- **A partial-failure-tolerant batch**: one message that fails to export does not abort the rest of the run.

## Prerequisites

- **Microsoft Graph sign-in.** `pks email export` does not authenticate itself — it checks whether you are already signed in and, if not, prints `Not authenticated. Run pks ms-graph register first.` and exits `1` without ever calling the mail-export Graph endpoints. (If a stored token is more than an hour old, this check itself makes one Graph `/me` validation call before deciding.) Run [pks ms-graph register](/tools/pks/ms-graph) first.
- **A Graph mail-folder id or well-known name** for `--folder`, if you are not exporting the default `inbox` — for example `sentitems` or `archive`. An invalid folder name surfaces as an HTTP error from Graph.

> **Note.** A Graph-layer guard elsewhere in the export path throws a different message — `"Not authenticated. Please sign in first using 'pks graph auth'"` — if a token becomes invalid mid-export. That command name is stale; the real command is `pks ms-graph register`.

## export

Fetches messages from one mail folder, converts each to Markdown, and writes it under `{output}/raw/{yyyy}/{MM}/{dd}/{HHmmss}-{subject-slug}/{subject-slug}.md`. Subject slugs are lowercased, non-alphanumeric characters collapsed to `-`, trimmed, and capped at 60 characters; an empty subject becomes `untitled`. When a message has attachments and `--no-attachments` was not passed, they are decoded and written into a sibling `attachments/` folder next to the message file.

Messages are fetched newest first (`receivedDateTime desc`) in pages of 50, following Graph's `@odata.nextLink` automatically. `--max` does not change the page size — it truncates the accumulated list client-side once the running total reaches your requested count, so even `--max 1` can trigger a full 50-message page fetch before truncating. `--after`, `--before`, `--from`, and `--subject` are translated into a Graph `$filter` clause and applied server-side.

A progress spinner reports `Exporting emails... {current}/{total} - Fetching` and `Exporting emails... {current}/{total} - Exporting`. When the run finishes, a table reports Exported, Skipped, and Error counts, followed by the absolute output directory path.

```text
pks email export [options]
```

| Flag | Default | Description |
|---|---|---|
| `-o, --output <PATH>` | `.emails` | Output directory for the exported tree. |
| `--after <DATE>` | — | Only messages received on or after this date (`yyyy-MM-dd`). |
| `--before <DATE>` | — | Only messages received on or before this date (`yyyy-MM-dd`). |
| `--from <EMAIL>` | — | Filter by sender email address. |
| `--subject <TEXT>` | — | Filter to subjects containing this text (Graph `contains(...)`). |
| `--folder <NAME>` | `inbox` | Mail folder id or well-known name to export from. |
| `--max <COUNT>` | — | Maximum number of messages to export, applied after fetching. |
| `--no-attachments` | `false` | Skip downloading attachments. |
| `--overwrite` | `false` | Re-export and overwrite messages whose output file already exists. |
| `-v, --verbose` | `false` | Show detailed output. |

```bash
pks email export
```

Exports the default `inbox`, all pages, into `./.emails`, skipping any message whose output file already exists.

```bash
pks email export --after 2026-01-01 --folder inbox
```

Exports only inbox messages received on or after 2026-01-01.

```bash
pks email export -o ./my-emails --max 100
```

Exports into a custom directory, capped at the 100 most recent messages.

## Troubleshooting

> **Availability.** `--after` and `--before` accept only `yyyy-MM-dd`. Any other format, including a full ISO datetime, is rejected with an `Invalid --after/--before date format. Use yyyy-MM-dd.` error and exit code `1` before any Graph call is made.

- **Nothing new gets exported on a re-run.** Without `--overwrite`, a message whose deterministic output path already exists is skipped, not re-fetched or diffed. Pass `--overwrite` to force a message to be rewritten — note this is not scoped to what changed, so combined with a broad filter it re-downloads every matching message and its attachments again.
- **Exit code is `1` but most messages exported fine.** The command exits `1` whenever any message failed (`ErrorCount > 0`), even if hundreds of others succeeded. Check the Errors count in the summary table rather than relying on exit code alone to mean "total failure".
- **`--subject` behaves unexpectedly with quotes in it.** The value is inserted directly into a Graph `contains(subject, '...')` filter with no escaping of embedded single quotes, which can break or alter the query.
- **An attachment did not save.** Attachments with empty content (reference or cloud attachments) are silently skipped. Attachment filenames from Graph are also written to disk with no sanitization.
- **`--folder` fails with an HTTP error.** The value must be a real Graph mail-folder id or well-known name (`inbox`, `sentitems`, `archive`, and so on) — an unknown name surfaces as a Graph HTTP error, not a local validation message.

## Next steps

- [pks ms-graph register](/tools/pks/ms-graph) — sign in to Microsoft Graph before your first export
- [pks](/tools/pks) — the full pks command surface this group belongs to
