---
title: "pks prd"
description: "Scaffold, inspect, and validate Product Requirements Documents from the command line — six subcommands that write and check docs/PRD.md."
tags: [reference, prd, documents, scaffolding]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks prd <command> [options]"
examples:
  - command: "pks prd template MyProject --type web"
    description: "Write a web-flavored PRD skeleton to docs/PRD.md"
  - command: "pks prd generate \"A mobile app for task management\""
    description: "Scaffold a PRD from a one-line idea"
  - command: "pks prd validate --strict"
    description: "Fail the build when the PRD skeleton is unfilled"
  - command: "pks prd status"
    description: "Show section and requirement counts for the PRD"
  - command: "pks prd load docs/PRD.md --show-metadata"
    description: "Parse a PRD file and print its structure"
---

`pks prd` is the Product Requirements Document branch of the pks CLI: six subcommands that write a conventional PRD skeleton to `docs/PRD.md`, parse it back, and check that its sections are filled in. It is a document scaffolder and a structural linter — the writing itself stays yours.

## Overview

The branch registers under `pks prd` and shares one settings class across every subcommand, so `--verbose`, `--output-format`, `--config`, and `--no-color` parse on all six. Two subcommands write files (`template`, `generate`), three read one (`load`, `status`, `validate`), and one lists requirements (`requirements`).

- **Write a skeleton.** `template` emits a static Markdown archetype with bracketed placeholder prompts. `generate` does the same and seeds a few sections from words it finds in your idea sentence.
- **Read it back.** `load` parses a Markdown or JSON PRD and prints project name, template, and section count.
- **Check it.** `validate` reports missing pieces and exits non-zero when the document fails, which makes it usable as a pre-commit or CI gate.

> **Note.** The branch description in the CLI says "AI-powered generation". No subcommand calls a language model. `generate` matches keywords in your idea string; `template` substitutes three placeholders into a fixed string. Treat this branch as scaffolding and structural checking, not authoring.

## What you get

- **Seven template archetypes.** `standard`, `technical`, `mobile`, `web`, `api`, `minimal`, and `enterprise`, accepted case-insensitively by both `template --type` and `generate --template`.
- **A conventional section layout.** Overview, Business Context, Requirements, User Stories, and the rest of the shape reviewers expect, so a new repository starts from a recognizable document.
- **A CI-usable gate.** `pks prd validate` exits `1` when the document has errors, and `--strict` promotes a missing description and an empty requirements list from warnings to errors.
- **A JSON side door.** `load --export`, `status --export`, `validate --report`, and `requirements --export` each write machine-readable output to a path you choose.
- **A directory sweep.** `status --check-all` walks the current directory for `PRD*.md`, `prd*.md`, `*requirements*.md`, `*spec*.md`, and `*.prd.json` and tables what it finds.

## How it fits together

Everything centers on one file path. `template` and `generate` both default their output to `./docs/PRD.md`, creating `docs/` when it is missing. `load`, `status`, `validate`, and `requirements` all default their input to the same path, so a bare `pks prd status` in a repository root works with no arguments once a PRD exists.

The parser recognizes two shapes: a JSON `PrdDocument`, or Markdown whose first heading matches `# <Name> - Product Requirements Document` and whose sections are `##` headings. The Markdown path extracts titles and sections but never populates a requirements array, which is why the numbers you see downstream are structural rather than semantic.

- **Trustworthy:** section counts, file presence, template type, project name, validation errors and warnings.
- **Not trustworthy:** completion percentages from `status`, the requirement rows from `requirements`, and the clarity, consistency, and feasibility scores from `validate`.

## Commands

`generate` · `template` · `load` · `validate` · `status` · `requirements`

| Subcommand | What it does | Trust level |
|---|---|---|
| [`template`](/tools/pks/prd/template) | Writes a static PRD skeleton for one of seven archetypes. | Deterministic. |
| [`generate`](/tools/pks/prd/generate) | Writes a PRD skeleton seeded from an idea sentence. | Keyword-driven, not AI. |
| [`load`](/tools/pks/prd/load) | Parses a PRD file and prints a summary panel. | Structural facts only. |
| [`validate`](/tools/pks/prd/validate) | Reports completeness errors and exits non-zero on failure. | Structural checks only. |
| [`status`](/tools/pks/prd/status) | Prints counts and a breakdown chart, or sweeps the tree. | Counts real, completion synthetic. |
| [`requirements`](/tools/pks/prd/requirements) | Lists and filters requirements with CSV or JSON export. | Placeholder rows — see its page. |

## Next steps

- [Generate a PRD from an idea](/tools/pks/prd/generate) — the fastest way to get a filled-in starting document
- [PRD templates](/tools/pks/prd/template) — the seven archetypes and how to pick one
- [Validate a PRD](/tools/pks/prd/validate) — wire the completeness gate into CI
- [PRD status and progress](/tools/pks/prd/status) — counts, the directory sweep, and what the percentage really means
- [Load and parse a PRD](/tools/pks/prd/load) — the parser's exact expectations for headings
- [pks prd CLI reference](/tools/pks/prd/reference) — every flag on all six subcommands in one table
