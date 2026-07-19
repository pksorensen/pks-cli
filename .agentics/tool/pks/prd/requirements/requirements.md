---
title: "List PRD requirements"
description: "Filter and export requirements from a PRD file — and the one known defect that makes its output placeholder rows rather than your document's content."
tags: [reference, prd, requirements]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks prd requirements [FILE_PATH] [options]"
examples:
  - command: "pks prd requirements --status draft --priority high"
    description: "Filter to draft, high-priority requirements"
  - command: "pks prd requirements docs/PRD.md --show-details"
    description: "One detail panel per requirement"
  - command: "pks prd requirements docs/PRD.md --export reqs.csv"
    description: "Export the filtered list as CSV"
---

`pks prd requirements` lists requirements from a PRD file as a table or as one panel each, with status, priority, type, and assignee filters and CSV or JSON export. The command surface is complete; the data behind it is not.

> **Note.** The Markdown load path never populates a requirements array, so this subcommand falls back to two hardcoded placeholder rows — `REQ-001 High Priority Draft Requirement` and `REQ-002 Medium Priority Feature` — whatever the file contains. The filters, the table, and the export all work; they work over that placeholder pair. Do not use this output as a requirements inventory.

## What it is useful for today

- **Exercising the filter and export paths** while the data plumbing is fixed.
- **Confirming that a file loads at all** — a load failure still exits `1`.

For real structural information about a document, use [`pks prd load`](/tools/pks/prd/load) or [`pks prd status`](/tools/pks/prd/status).

## Synopsis

```text
pks prd requirements [FILE_PATH] [options]
```

`FILE_PATH` is optional and defaults to `./docs/PRD.md`.

## Filters

Filter values are matched case-insensitively against enum names.

| Flag | Accepted values |
|---|---|
| `--status <STATUS>` | `draft`, `approved`, `inprogress`, `completed`, `blocked`, `cancelled`, `onhold` |
| `--priority <PRIORITY>` | `critical`, `high`, `medium`, `low`, `nice` |
| `--type <TYPE>` | `functional`, `nonfunctional`, `business`, `technical`, `security`, `performance`, `usability`, `accessibility`, `compliance` |
| `--assignee <ASSIGNEE>` | Free text, matched as a case-insensitive substring. |

The three enum filters behave differently on a bad value. An unrecognized `--status` or `--priority` exits `1`. An unrecognized `--type` is silently dropped and the listing runs unfiltered.

The example registered on the command itself, `pks prd requirements --status pending`, uses a value that is not in the status set and exits `1`. Use `draft` instead.

## Options

| Flag | Default | Description |
|---|---|---|
| `--status <STATUS>` | — | Filter by requirement status. Invalid values exit `1`. |
| `--priority <PRIORITY>` | — | Filter by priority. Invalid values exit `1`. |
| `--type <TYPE>` | — | Filter by requirement type. Invalid values are ignored. |
| `--assignee <ASSIGNEE>` | — | Filter by assignee, case-insensitive substring match. |
| `--export <EXPORT_PATH>` | — | Export the list. The extension must be `.json` or `.csv`. |
| `--show-details` | — | Print one detail panel per requirement instead of the table. |
| `-v`, `--verbose` | — | Detailed output. |
| `--output-format <FORMAT>` | `markdown` | Output format: `markdown` or `json`. |
| `--config <CONFIG_FILE>` | — | Declared but never read. No effect. |
| `--no-color` | — | Declared but never read. No effect. |

## Examples

```bash
pks prd requirements --status draft --priority high
pks prd requirements docs/PRD.md --show-details --export reqs.csv
```

## Troubleshooting

**The output does not match the document.** Expected. See the note above.

**Exit code `1` on an apparently reasonable filter.** The value is outside the enum. Check it against the filter table; `pending`, `open`, and `todo` are not status values.

**Export writes nothing and errors.** The export path's extension is neither `.json` nor `.csv`.

**A filter appears to be ignored.** `--type` fails open. A typo there silently disables that filter rather than reporting it.

## See also

- [Load and parse a PRD](/tools/pks/prd/load) — where the missing requirements data would come from
- [PRD status and progress](/tools/pks/prd/status) — counts that do reflect the file
- [Validate a PRD](/tools/pks/prd/validate) — the checks worth gating on
- [pks prd CLI reference](/tools/pks/prd/reference) — the full flag surface
