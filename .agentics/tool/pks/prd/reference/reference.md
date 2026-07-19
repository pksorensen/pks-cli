---
title: "pks prd CLI reference"
description: "Complete command, argument, flag, and exit-code reference for the pks prd branch — generate, template, load, validate, status, and requirements."
tags: [reference, prd, cli]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks prd <command> [options]"
examples:
  - command: "pks prd template MyProject --type web"
    description: "Write a web PRD skeleton to docs/PRD.md"
  - command: "pks prd generate \"A mobile app for task management\" -f"
    description: "Scaffold from an idea, overwriting in place"
  - command: "pks prd load docs/PRD.md --show-metadata"
    description: "Parse a file and print its metadata table"
  - command: "pks prd validate --strict"
    description: "Fail on a missing description or zero requirements"
  - command: "pks prd status --check-all"
    description: "Sweep the tree for PRD-like documents"
  - command: "pks prd requirements --status draft"
    description: "Filter the requirement listing by status"
---

`pks prd` is the Product Requirements Document branch of the pks CLI. Six subcommands write a PRD skeleton to disk, parse one back, count its parts, and check that it has been filled in. All six share a single settings class, so the four global options below parse everywhere in the branch.

The implementation lives in `src/Commands/Prd/` with one service behind it, registered as a singleton at CLI start-up. No subcommand calls a language model, opens a network connection, reads an environment variable, or requires authentication.

## Synopsis

```text
pks prd <command> [options]
```

```text
generate       Generate a PRD skeleton from an idea description
template       Write a static PRD template for a project archetype
load           Load and parse an existing PRD file
validate       Validate a PRD for completeness and consistency
status         Display PRD status, progress, and statistics
requirements   List and filter requirements from a PRD
```

### Global options

Every subcommand accepts these.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | — | Enable verbose output with detailed information. |
| `--output-format <FORMAT>` | `markdown` | Output format: `markdown` or `json`. |
| `--config <CONFIG_FILE>` | — | Declared but never read anywhere in the branch. No effect. |
| `--no-color` | — | Declared but never read anywhere in the branch. No effect. |

`--config` and `--no-color` parse without error and change nothing. They are listed here so a reader who finds them in `--help` knows not to depend on them.

### Shared conventions

The default file path across the branch is `./docs/PRD.md`. `generate` and `template` create `docs/` when it is missing. `load` requires an explicit path; `validate`, `status`, and `requirements` take an optional one.

Template archetypes, accepted case-insensitively by `generate --template` and `template --type`: `standard`, `technical`, `mobile`, `web`, `api`, `minimal`, `enterprise`.

## generate

Writes a PRD skeleton from a required idea sentence. Sections are filled from the flags below; requirements and user stories are seeded by matching literal words in the idea text — `web` or `website`, `user` or `customer`, `data` or `information`, `manage` or `create`. An idea containing none of them yields near-empty Requirements and User Stories sections. With `-v`, a validation pass runs on the newly written file and its completeness score is printed.

Argument: `<IDEA_DESCRIPTION>` — required.

| Flag | Default | Description |
|---|---|---|
| `-n <PROJECT_NAME>`, `--name <PROJECT_NAME>` | current directory name | Name of the project. |
| `-o <OUTPUT_PATH>`, `--output <OUTPUT_PATH>` | `./docs/PRD.md` | Output path for the generated file. |
| `-t <TEMPLATE>`, `--template <TEMPLATE>` | `standard` | Template archetype. |
| `--target-audience <AUDIENCE>` | — | Target audience for the project. |
| `--stakeholders <STAKEHOLDERS>` | — | Comma-separated list of stakeholders. |
| `--business-context <CONTEXT>` | — | Business context or background. |
| `--technical-constraints <CONSTRAINTS>` | — | Comma-separated list of technical constraints. |
| `-f`, `--force` | — | Overwrite an existing output file without prompting. |
| `--interactive` | — | Prompt for the remaining fields. Requires a terminal. |

Exit codes: `1` on a missing or empty idea description, or an invalid template value. `0` with `Operation cancelled` when an overwrite prompt is declined.

Without `-f`, an existing file at the output path opens a confirmation prompt, which has no answer in a non-interactive shell. In `--interactive` mode the template selection is always prompted, even when `-t` was supplied; the other fields are prompted only when empty.

```bash
pks prd generate "An e-commerce web platform with user accounts" -n Shopfront -t web -f
```

## template

Writes a static Markdown skeleton for one archetype, substituting project name, author, and date into a fixed string. Sections arrive as headings with bracketed placeholder prompts. No content is generated.

Argument: `<PROJECT_NAME>` — required.

| Flag | Default | Description |
|---|---|---|
| `-t <TEMPLATE_TYPE>`, `--type <TEMPLATE_TYPE>` | `standard` | Template archetype. |
| `-o <OUTPUT_PATH>`, `--output <OUTPUT_PATH>` | `./docs/PRD.md` | Output path for the template file. |
| `--list` | — | List the seven archetypes with descriptions and exit. |

**Do not commit.** There is no `--force` flag and no confirmation here — an existing file at the output path is replaced silently.

Because the project-name argument is declared as required, `pks prd template --list` on its own can be rejected at parse time. Pass a placeholder name alongside `--list` if that happens.

```bash
pks prd template Acme-API -t api -o docs/api-prd.md
```

## load

Parses a JSON `PrdDocument` or a Markdown PRD and prints a summary panel with the project name, template, and section count.

Argument: `<FILE_PATH>` — required.

| Flag | Default | Description |
|---|---|---|
| `--validate` | — | Run the validation checks after loading and print the report. |
| `--show-metadata` | — | Print an additional metadata table. |
| `--export <EXPORT_PATH>` | — | Write the load summary as JSON to this path. |

The Markdown title must match `# <Name> - Product Requirements Document`; otherwise the project name is derived from the filename with a leading `PRD` prefix stripped. The description is read from a `## Overview` section only. The parse result carries section titles but never the document's requirements or user stories.

Exit code `1` when the file is missing or parses as neither shape.

## validate

Loads a PRD and runs structural completeness checks: project name and description present, requirements, user stories, and sections non-empty. Prints errors, warnings, suggestions, and a completeness-score bar.

Argument: `[FILE_PATH]` — optional, defaults to `./docs/PRD.md`.

| Flag | Default | Description |
|---|---|---|
| `--strict` | — | Treat a missing description and zero requirements as errors, not warnings. |
| `--report <REPORT_PATH>` | — | Write a JSON validation report to this path. |
| `--fix` | — | Declared but never read. No automatic fixing occurs. |

Exit code is `0` when the document is valid and `1` when errors are present, which makes this the one branch subcommand suitable as a CI gate.

Only the completeness score is computed from the document. The clarity, consistency, and feasibility scores are the constants `85`, `90`, and `80` in source, marked as simulated. Checks are structural, not semantic — placeholder text in every section still scores as complete.

```bash
pks prd validate docs/PRD.md --strict --report validation-report.json
```

## status

Prints a status panel and breakdown chart for one file, or sweeps the current directory tree.

Argument: `[FILE_PATH]` — optional, defaults to `./docs/PRD.md`.

| Flag | Default | Description |
|---|---|---|
| `--check-all` | — | Recursively find PRD-like files and table their status. Takes precedence over the path and `--watch`. |
| `--watch` | — | Poll the file every two seconds and re-render on change. Ends only on Ctrl+C. |
| `--export <EXPORT_PATH>` | — | Write the status object as JSON. Single-file mode only. |
| `--include-history` | — | Print up to five recent-change entries. Never populated, so no effect. |

The sweep matches `PRD*.md`, `prd*.md`, `*requirements*.md`, `*spec*.md`, and `*.prd.json`.

The completed-requirement count is hardcoded to `Math.Min(1, TotalRequirements)` with a "for test compatibility" comment in source, so the completion percentage does not track progress. A missing file prints an error and still returns `0`; use `validate` when the exit code matters.

## requirements

Lists and filters requirements as a table or as detail panels, with CSV or JSON export.

Argument: `[FILE_PATH]` — optional, defaults to `./docs/PRD.md`.

| Flag | Default | Description |
|---|---|---|
| `--status <STATUS>` | — | `draft`, `approved`, `inprogress`, `completed`, `blocked`, `cancelled`, `onhold`. Invalid values exit `1`. |
| `--priority <PRIORITY>` | — | `critical`, `high`, `medium`, `low`, `nice`. Invalid values exit `1`. |
| `--type <TYPE>` | — | `functional`, `nonfunctional`, `business`, `technical`, `security`, `performance`, `usability`, `accessibility`, `compliance`. Invalid values are ignored. |
| `--assignee <ASSIGNEE>` | — | Case-insensitive substring match on the assignee. |
| `--export <EXPORT_PATH>` | — | Export the list. The extension must be `.json` or `.csv`. |
| `--show-details` | — | One detail panel per requirement instead of the table. |

Because the Markdown load path never populates a requirements array, this subcommand falls back to two hardcoded placeholder rows regardless of file content. Exit code `1` on a load failure or an invalid status or priority; `0` with a no-results message when the filters exclude both rows.

The example registered on the command, `pks prd requirements --status pending`, uses a value outside the status set and exits `1`.

## See also

- [pks prd](/tools/pks/prd) — branch overview and mental model
- [Generate a PRD from an idea](/tools/pks/prd/generate) — step-by-step scaffolding
- [PRD templates](/tools/pks/prd/template) — the seven archetypes
- [Validate a PRD](/tools/pks/prd/validate) — the CI gate
- [PRD status and progress](/tools/pks/prd/status) — counts and the directory sweep
- [List PRD requirements](/tools/pks/prd/requirements) — filters, export, and the known defect
