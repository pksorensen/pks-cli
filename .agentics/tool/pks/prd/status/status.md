---
title: "PRD status and progress"
description: "Print section and requirement counts for a PRD file, sweep a repository for every PRD-like document, or watch one file and re-render on change."
tags: [how-to, prd, reporting]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks prd status [FILE_PATH] [options]"
examples:
  - command: "pks prd status"
    description: "Status panel for ./docs/PRD.md"
  - command: "pks prd status --check-all"
    description: "Table every PRD-like file under this directory"
  - command: "pks prd status --export status.json"
    description: "Write the status report to JSON"
---

`pks prd status` prints a status panel and a breakdown chart for a PRD file: requirement counts, section counts, and a completion percentage. With `--check-all` it stops looking at one file and sweeps the current directory tree for every document that looks like a PRD.

## 1. Prerequisites

- **A PRD file, or a repository containing some.** Single-file mode defaults to `./docs/PRD.md`.
- **An interactive terminal, if you plan to use `--watch`.** That mode never returns on its own.

## 2. Check one file

```bash
pks prd status
```

The panel names the file, its counts, and a completion percentage.

> **Note.** The completion figure is not progress. The service sets the completed-requirement count to `Math.Min(1, TotalRequirements)`, carrying an explicit "for test compatibility" comment in the source. The percentage reads as zero or as one requirement's worth, whatever the document actually says. Use the counts; ignore the percentage.

## 3. Sweep the repository

```bash
pks prd status --check-all
```

The sweep searches recursively from the current directory for files matching `PRD*.md`, `prd*.md`, `*requirements*.md`, `*spec*.md`, and `*.prd.json`, then tables what each one contains. It takes precedence over both a positional file path and `--watch`.

This is the practical way to find PRDs that drifted out of `docs/` in a repository nobody has tidied.

## 4. Export the report

```bash
pks prd status docs/PRD.md --export status.json
```

The status object is written as JSON to the path you give. Export works in single-file mode only — combining it with `--check-all` produces no file.

## 5. Watch a file while you edit

```bash
pks prd status --watch
```

The file is polled every two seconds and the panel re-renders when it changes. The loop is unbounded and ends only on Ctrl+C. Keep it out of scripts, pipelines, and any non-interactive context.

## 6. Verify

```bash
pks prd status docs/PRD.md --export status.json
```

The console shows the panel and `status.json` now holds the same figures in machine-readable form.

## Options

| Flag | Default | Description |
|---|---|---|
| `--check-all` | — | Sweep the current directory tree for PRD-like files and table them. |
| `--watch` | — | Poll the file every two seconds and re-render. Ends only on Ctrl+C. |
| `--export <EXPORT_PATH>` | — | Write the status object as JSON. Single-file mode only. |
| `--include-history` | — | Print up to five recent-change entries. No effect today. |
| `-v`, `--verbose` | — | Detailed output. |
| `--output-format <FORMAT>` | `markdown` | Output format: `markdown` or `json`. |
| `--config <CONFIG_FILE>` | — | Declared but never read. No effect. |
| `--no-color` | — | Declared but never read. No effect. |

## Troubleshooting

**The file does not exist and the command still succeeds.** Single-file mode prints an error, suggests `pks prd generate`, and returns `0`. Do not use `pks prd status` as a presence check in CI — use [`pks prd validate`](/tools/pks/prd/validate), whose exit code follows the result.

**`--include-history` prints nothing.** The recent-changes list is never populated, so the flag has no observable effect.

**A pipeline step never finishes.** `--watch` was passed. Remove it.

**Completion sits at a number that never moves.** Expected — see the note in step 2.

## See also

- [Validate a PRD](/tools/pks/prd/validate) — the subcommand whose exit code you can gate on
- [Load and parse a PRD](/tools/pks/prd/load) — structural detail for a single file
- [pks prd CLI reference](/tools/pks/prd/reference) — every flag across the branch
- [pks prd](/tools/pks/prd) — the branch overview
