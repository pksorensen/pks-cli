---
title: "PRD templates"
description: "Write a static Markdown PRD skeleton for one of seven project archetypes, with the project name, author, and date substituted into the headings."
tags: [how-to, prd, templates]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks prd template <PROJECT_NAME> [options]"
examples:
  - command: "pks prd template MyProject --type web"
    description: "Write a web-app skeleton to docs/PRD.md"
  - command: "pks prd template MyProject --list"
    description: "Show the seven archetypes and their descriptions"
  - command: "pks prd template Acme-API -t api -o docs/api-prd.md"
    description: "API skeleton written to a custom path"
---

`pks prd template` writes a fixed Markdown PRD skeleton for one of seven project archetypes. Every section arrives as a heading with a bracketed placeholder prompt underneath, such as `[Provide a brief overview...]`, and three values are substituted in: project name, author, and date. It is the most predictable subcommand in the branch — pure string templating with no content generation.

## 1. Prerequisites

- **A project name.** It is a required positional argument and appears in the document title.
- **A writable output directory.** The default is `./docs/PRD.md`.

## 2. See the archetypes

```bash
pks prd template MyProject --list
```

A table prints the seven template types with their descriptions and the command exits without writing anything. The accepted values are `standard`, `technical`, `mobile`, `web`, `api`, `minimal`, and `enterprise`, matched case-insensitively.

> **Note.** The project-name argument is declared as required, so `pks prd template --list` on its own can be rejected at parse time before the listing branch runs. Pass any placeholder name alongside `--list` if that happens.

## 3. Write the skeleton

```bash
pks prd template Shopfront --type web
```

`docs/PRD.md` now contains the web archetype's headings with `Shopfront` in the title. Pick an archetype by what the project is, not by how detailed you want the document:

| Type | Fits |
|---|---|
| `standard` | A general product with no strong platform bias. |
| `technical` | An internal component where implementation detail dominates. |
| `mobile` | An iOS or Android application. |
| `web` | A browser-delivered application. |
| `api` | A service consumed by other software. |
| `minimal` | A short document for a small piece of work. |
| `enterprise` | A document with the wider governance sections. |

## 4. Choose an output path

```bash
pks prd template Acme-API -t api -o docs/api-prd.md
```

Use a distinct path when a repository holds more than one PRD. The default path is shared with `pks prd generate`, so the two overwrite each other otherwise.

**Do not commit.** The file at the output path is replaced without warning. This subcommand has no `--force` flag and no confirmation prompt — writing over a PRD you have already filled in is silent and unrecoverable. Check the path before running it in a repository that already contains a document.

## 5. Verify

```bash
pks prd load docs/PRD.md --show-metadata
```

The metadata table prints the project name, template, and section count for the file that was written.

## Options

| Flag | Default | Description |
|---|---|---|
| `-t <TEMPLATE_TYPE>`, `--type <TEMPLATE_TYPE>` | `standard` | Archetype: `standard`, `technical`, `mobile`, `web`, `api`, `minimal`, `enterprise`. |
| `-o <OUTPUT_PATH>`, `--output <OUTPUT_PATH>` | `./docs/PRD.md` | Output path for the template file. |
| `--list` | — | List the seven template types with descriptions and exit. |
| `-v`, `--verbose` | — | Detailed output. |
| `--output-format <FORMAT>` | `markdown` | Output format: `markdown` or `json`. |
| `--config <CONFIG_FILE>` | — | Declared but never read. No effect. |
| `--no-color` | — | Declared but never read. No effect. |

## Troubleshooting

**An existing PRD was replaced.** There is no overwrite guard on this subcommand. Recover the file from version control and rerun with `-o` pointing somewhere else.

**`--list` is rejected with a missing-argument error.** The positional project name is mandatory in the command grammar. Run `pks prd template placeholder --list`.

**The document is all brackets.** That is the design. Templates carry placeholder prompts only. For a skeleton with some seeded content, use [Generate a PRD from an idea](/tools/pks/prd/generate).

## See also

- [Generate a PRD from an idea](/tools/pks/prd/generate) — a skeleton seeded from an idea sentence
- [Load and parse a PRD](/tools/pks/prd/load) — confirm what was written
- [pks prd CLI reference](/tools/pks/prd/reference) — every flag across the branch
- [pks prd](/tools/pks/prd) — the branch overview
