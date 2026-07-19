---
title: "Generate a PRD from an idea"
description: "Turn a one-line project idea into a docs/PRD.md skeleton with seeded sections, sample requirements, and canned user stories you then edit by hand."
tags: [how-to, prd, scaffolding]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks prd generate \"<IDEA_DESCRIPTION>\" [options]"
examples:
  - command: "pks prd generate \"A mobile app for task management\""
    description: "Scaffold docs/PRD.md from an idea sentence"
  - command: "pks prd generate \"An e-commerce web platform with user accounts\" -n Shopfront -t web -f"
    description: "Named project, web template, overwrite in place"
  - command: "pks prd generate \"Internal data pipeline\" --interactive"
    description: "Collect the remaining fields through prompts"
---

`pks prd generate` writes a new `docs/PRD.md` from a single sentence describing your project. It fills the standard headings, adds a few sample requirements, and drops in one or two user stories, giving you a document to edit rather than a blank page.

## 1. Prerequisites

- **A working directory you want the PRD in.** The default output is `./docs/PRD.md`, and `docs/` is created when it does not exist.
- **A terminal, if you plan to use `--interactive` or overwrite an existing file.** Both paths open console prompts.
- **Nothing else.** This subcommand needs no login, no API key, and no environment variables.

## 2. Write the idea sentence

The idea description is a required positional argument. Quote it.

```bash
pks prd generate "A mobile app for task management"
```

The file lands at `docs/PRD.md` and the console confirms the path.

Word choice in that sentence matters more than it looks. Requirement and user-story seeding is keyword matching over the idea text, checking for the literal words `web` or `website`, `user` or `customer`, `data` or `information`, and `manage` or `create`. An idea sentence containing none of them produces near-empty Requirements and User Stories sections.

> **Note.** No language model runs here. The branch description says "AI-powered generation"; the implementation is string matching plus templating.

## 3. Add the surrounding context

Each of these flags fills a section of the generated document.

```bash
pks prd generate "An internal approval workflow for finance" \
  --name ApprovalFlow \
  --target-audience "Finance operations team" \
  --business-context "Manual approvals take four days on average" \
  --stakeholders "CFO,Head of Ops,Platform team" \
  --technical-constraints "Must run on-premises,No new database"
```

`--stakeholders` and `--technical-constraints` are comma-separated lists. `--name` defaults to the current directory's folder name when omitted.

## 4. Choose a template and an output path

```bash
pks prd generate "A partner-facing REST service" -t api -o docs/partner-api-prd.md
```

Accepted `--template` values are `standard`, `technical`, `mobile`, `web`, `api`, `minimal`, and `enterprise`, matched case-insensitively. An unrecognized value exits `1`. Here the template selects the section shape and labels; it does not change how content is seeded.

## 5. Overwrite safely

Without `-f`, an existing file at the output path triggers an interactive confirmation. Declining prints `Operation cancelled` and exits `0`.

```bash
pks prd generate "A partner-facing REST service" -t api -f
```

In scripts and CI, always pass `-f`. A non-interactive shell has nothing to answer the prompt with.

## 6. Verify

```bash
pks prd validate docs/PRD.md
```

A freshly generated file passes the non-strict checks and prints a completeness score. Adding `-v` to the generate call runs the same validation pass automatically and prints the score table right after writing the file.

## Options

| Flag | Default | Description |
|---|---|---|
| `-n <PROJECT_NAME>`, `--name <PROJECT_NAME>` | current directory name | Name of the project, when it differs from the folder. |
| `-o <OUTPUT_PATH>`, `--output <OUTPUT_PATH>` | `./docs/PRD.md` | Output path for the generated file. |
| `-t <TEMPLATE>`, `--template <TEMPLATE>` | `standard` | Archetype: `standard`, `technical`, `mobile`, `web`, `api`, `minimal`, `enterprise`. |
| `--target-audience <AUDIENCE>` | — | Target audience for the project. |
| `--stakeholders <STAKEHOLDERS>` | — | Comma-separated list of stakeholders. |
| `--business-context <CONTEXT>` | — | Business context or background. |
| `--technical-constraints <CONSTRAINTS>` | — | Comma-separated list of technical constraints. |
| `-f`, `--force` | — | Overwrite an existing file without prompting. |
| `--interactive` | — | Prompt for idea, name, audience, template, context, and stakeholders. |
| `-v`, `--verbose` | — | Detailed output, plus a validation pass on the new file. |
| `--output-format <FORMAT>` | `markdown` | Output format: `markdown` or `json`. |
| `--config <CONFIG_FILE>` | — | Declared but never read. No effect. |
| `--no-color` | — | Declared but never read. No effect. |

## Troubleshooting

**The command exits `1` immediately.** The idea description is missing or empty, or `--template` carries a value outside the seven archetypes. Both exit `1` before anything is written.

**The Requirements section is nearly empty.** The idea sentence contains none of the trigger words. Rewrite it to name what the system manages or creates, and for whom.

**The run hangs or errors in CI.** A file already exists at the output path and the confirmation prompt has no terminal. Add `-f`.

**`--interactive` asks for the template even though `-t` was passed.** The template selection prompt always runs in interactive mode; the other fields are prompted only when still empty. Pick the archetype again at the prompt, or drop `--interactive`.

## See also

- [PRD templates](/tools/pks/prd/template) — deterministic skeletons with no keyword seeding
- [Validate a PRD](/tools/pks/prd/validate) — check what the generated file is still missing
- [pks prd CLI reference](/tools/pks/prd/reference) — the full flag surface for all six subcommands
- [pks prd](/tools/pks/prd) — how the six subcommands fit together
