---
title: "Validate a PRD"
description: "Run structural completeness checks over a PRD file, print errors, warnings and suggestions, and exit non-zero so CI can block an unfilled skeleton."
tags: [how-to, prd, validation, ci]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks prd validate [FILE_PATH] [options]"
examples:
  - command: "pks prd validate"
    description: "Check ./docs/PRD.md with the default rules"
  - command: "pks prd validate --strict"
    description: "Promote missing description and requirements to errors"
  - command: "pks prd validate docs/PRD.md --report validation-report.json"
    description: "Validate and write a machine-readable report"
---

`pks prd validate` loads a PRD file, checks that the pieces a reader expects are present, and prints errors, warnings, and suggestions with a completeness-score bar. Its exit code follows the result, which makes it the one subcommand in the branch suited to automation.

## 1. Prerequisites

- **An existing PRD file.** The default input is `./docs/PRD.md`; pass a path to check another.
- **Nothing else.** No credentials, no network, no configuration.

## 2. Run the default check

```bash
pks prd validate
```

The report lists what is missing and prints a completeness score. Checks are structural: is the project name set, is a description present, are there requirements, user stories, and sections at all.

> **Note.** The checks count content, they do not read it. A document whose every section holds placeholder text scores as complete. Validation answers "did anyone fill this in", not "is this any good".

## 3. Tighten the rules

```bash
pks prd validate --strict
```

Strict mode changes severity, not the checks themselves. A missing project description and a requirements count of zero become hard errors instead of warnings, so a skeleton nobody has touched fails.

## 4. Wire it into CI

The exit code is `0` when the document is valid and `1` when errors are present.

```bash
pks prd validate docs/PRD.md --strict
```

Put that in a pre-commit hook or a pipeline step to keep an empty PRD out of the default branch. Choose the severity mode deliberately: without `--strict` an untouched skeleton passes, because the missing pieces are only warnings.

## 5. Keep a report

```bash
pks prd validate docs/PRD.md --report validation-report.json
```

The JSON report carries a timestamp, the validity flag, the score, the errors, warnings, suggestions, and a summary. Publish it as a build artifact when a human needs to read the failure after the fact.

## 6. Verify

```bash
pks prd validate docs/PRD.md --strict
```

Then read the shell's exit status. A valid document returns `0`; a document with errors returns `1` and the failing conditions appear in the printed error list.

## Scores and what they mean

Only the completeness score is derived from the document. The clarity score of `85`, the consistency score of `90`, and the feasibility score of `80` are constants in the source, marked as simulated. Do not chart them, do not gate on them, and do not report them to anyone as a measurement.

## Options

| Flag | Default | Description |
|---|---|---|
| `--strict` | — | Treat a missing description and zero requirements as errors, not warnings. |
| `--report <REPORT_PATH>` | — | Write the JSON validation report to this path. |
| `--fix` | — | Declared but never read. No automatic fixing happens. |
| `-v`, `--verbose` | — | Detailed output. |
| `--output-format <FORMAT>` | `markdown` | Output format: `markdown` or `json`. |
| `--config <CONFIG_FILE>` | — | Declared but never read. No effect. |
| `--no-color` | — | Declared but never read. No effect. |

## Troubleshooting

**`--fix` changed nothing.** The flag parses and is then ignored — the automatic-fix path does not exist. Edit the document by hand.

**A document full of placeholders passes.** Add `--strict`, which turns the two most common empty-skeleton conditions into errors. Beyond that, the checks cannot distinguish real prose from bracketed prompts.

**The command exits `1` with a load failure rather than a validation report.** The file is missing, or it parses as neither JSON nor the expected Markdown shape. See the parser's expectations on [Load and parse a PRD](/tools/pks/prd/load).

## See also

- [Load and parse a PRD](/tools/pks/prd/load) — the heading conventions the parser requires
- [PRD status and progress](/tools/pks/prd/status) — counts and the directory sweep
- [Generate a PRD from an idea](/tools/pks/prd/generate) — produce a document that passes the default checks
- [pks prd CLI reference](/tools/pks/prd/reference) — the full flag surface
