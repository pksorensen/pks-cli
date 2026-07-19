---
title: "Load and parse a PRD"
description: "Parse a Markdown or JSON PRD file, print its project name, template and section count, and learn the exact heading conventions the parser expects."
tags: [how-to, prd, parsing]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks prd load <FILE_PATH> [options]"
examples:
  - command: "pks prd load docs/PRD.md"
    description: "Summarize the default PRD file"
  - command: "pks prd load docs/PRD.md --validate --show-metadata"
    description: "Summary, metadata table, and validation in one call"
  - command: "pks prd load docs/PRD.md --export prd-summary.json"
    description: "Write the load summary to JSON"
---

`pks prd load` reads a PRD file and prints what it found: project name, template, and section count. It is the subcommand to reach for when a document written elsewhere is not being recognized, because it shows exactly what the parser extracted.

## 1. Prerequisites

- **A file path.** Unlike the other read subcommands, the path is a required argument here.
- **A file in one of two shapes.** Either a JSON `PrdDocument`, or Markdown following the conventions below.

## 2. Summarize a file

```bash
pks prd load docs/PRD.md
```

A panel prints the project name, the template, and how many sections were found.

## 3. Understand what the parser expects

The Markdown path is strict in two places, and both explain most surprises.

- **The title.** The first heading must match `# <Name> - Product Requirements Document`. A plain `# MyProject` heading does not match, and the project name is instead derived from the filename, with a leading `PRD` prefix stripped.
- **The description.** It is taken from a section headed exactly `## Overview`. Other headings still parse as generic sections but contribute no description, which is what a later `pks prd validate` run reports as a missing description.

Files produced by [`pks prd template`](/tools/pks/prd/template) and [`pks prd generate`](/tools/pks/prd/generate) already follow both conventions.

## 4. Inspect the metadata

```bash
pks prd load docs/PRD.md --show-metadata
```

An additional table prints the product name, template, section count, status, and message. Use it when the panel alone leaves the question open.

## 5. Validate in the same call

```bash
pks prd load docs/PRD.md --validate
```

The same validation panel that [`pks prd validate`](/tools/pks/prd/validate) prints is appended to the load output. Note that `load` exits `1` only on a load failure; when you need an exit code that follows validity, call `validate` directly.

## 6. Export the summary

```bash
pks prd load docs/PRD.md --export prd-summary.json
```

The JSON carries the product name, template, sections, and message — the summary, not the full parsed document.

## 7. Verify

```bash
pks prd load docs/PRD.md --show-metadata
```

The project name in the metadata table should match the name in your document's title. If it matches the filename instead, the title heading does not follow the pattern in step 3.

## What load does not give you

The parse result never carries the actual requirements or user stories from a Markdown file — only section titles. That gap is why [`pks prd requirements`](/tools/pks/prd/requirements) cannot show your real requirement list. Treat `load` as a structural inspector.

## Options

| Flag | Default | Description |
|---|---|---|
| `--validate` | — | Run the validation checks after loading and print the report. |
| `--show-metadata` | — | Print an additional metadata table. |
| `--export <EXPORT_PATH>` | — | Write the load summary to a JSON file. |
| `-v`, `--verbose` | — | Detailed output. |
| `--output-format <FORMAT>` | `markdown` | Output format: `markdown` or `json`. |
| `--config <CONFIG_FILE>` | — | Declared but never read. No effect. |
| `--no-color` | — | Declared but never read. No effect. |

## Troubleshooting

**Exit code `1` with a parse error.** The file is missing, or it is neither valid JSON nor Markdown in the expected shape. Compare its first heading against step 3.

**The project name is the filename.** The title heading does not match the required pattern. Rename the heading to `# <Name> - Product Requirements Document`.

**Validation reports a missing description on a document that clearly has one.** The description section is not headed `## Overview`. Rename the heading.

## See also

- [Validate a PRD](/tools/pks/prd/validate) — the checks run by `--validate`, with a usable exit code
- [PRD status and progress](/tools/pks/prd/status) — counts and the repository sweep
- [pks prd CLI reference](/tools/pks/prd/reference) — the full flag surface
- [pks prd](/tools/pks/prd) — the branch overview
