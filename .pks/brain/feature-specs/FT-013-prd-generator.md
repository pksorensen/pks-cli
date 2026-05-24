---
id: FT-013
title: PRD generator & requirements workflow
domain: knowledge
status: draft
adrs: []
tests: [tests/Commands/Prd/PrdCommandTests.cs, tests/Commands/Prd/PrdGenerateCommandTests.cs, tests/Commands/Prd/PrdLoadCommandTests.cs, tests/Commands/Prd/PrdRequirementsCommandTests.cs, tests/Commands/Prd/PrdValidateCommandTests.cs, tests/Commands/Prd/PrdTemplateCommandTests.cs]
source-files: [src/Commands/Prd/PrdCommand.cs, src/Commands/Prd/PrdGenerateCommand.cs, src/Commands/Prd/PrdLoadCommand.cs, src/Commands/Prd/PrdRequirementsCommand.cs, src/Commands/Prd/PrdStatusCommand.cs, src/Commands/Prd/PrdTemplateCommand.cs, src/Commands/Prd/PrdValidateCommand.cs, src/Commands/Prd/PrdSettings.cs, src/Infrastructure/Services/PrdService.cs]
sessions: [cd04962e-09d1-4d84-a6bb-69df5ede0f04, f0d2b976-8bf1-42c4-9411-9a499dd76e52, a5bba06a-22dc-40e9-9fa3-942b88aa7b41, ee82d185-bed1-47e9-bd14-02aabe5671c8, c0aedb77-775c-40a9-ab86-8e3fa459f719]
---

## Description

The `pks prd` command group turns a free-form idea description into a structured Product Requirements Document and then keeps that document live across the project lifecycle. `PrdGenerateCommand` runs the user through interactive prompts (idea, template type, output path) and hands the request to `PrdService.GeneratePrdAsync`, which renders one of seven templates (`standard`, `technical`, `mobile`, `web`, `api`, `minimal`, `enterprise`). Sibling commands cover the rest of the workflow: `load` reads an existing PRD back into the service, `requirements` lists and filters the extracted requirements, `validate` checks completeness and consistency, `template` scaffolds blank PRDs for new project types, and `status` reports section/word/requirement counts. Together they implement a "PRD as the single source of truth" pattern: the same document feeds project init, requirement tracking, and downstream validation rather than being a one-off Word doc.

## Intent

> we are uploading images. and we have startet making the pks-agent-photographer project. Its time to start making the image processing platform.
>
> I have this description i would like to turn into a PRD:
> # The photographer's mind, mapped for code
> This is a workflow specification, not an essay. The professional photography pipeline is a sequence of **mostly-deterministic gates** punctuated by a few **irreducibly creative decisions**, and the gap between the two is where automation pays off.

From session cd04962e (2026-05-17), prompt.

> Lets make a project for this PRD:
> # B2B Travel Agency Platform
> ## 1. Title and Overview
> ### 1.1 Document Title & Version
> B2B Travel Agency Platform - Version 1.0

From session f0d2b976 (2026-05-20), prompt.

> There are levels of specification — PRD (level 3, narrative requirements), Given/When/Then BDD (level 2, behavioural contracts), product-cli's typed graph (level 1, formal e[xecution])...

From session a5bba06a (2026-05-24), prompt (blog chapter "levels-of-spec" framing the PRD as the top of a three-level spec ladder).

## Key decisions

- **PRD-as-input, not PRD-as-output.** The dominant usage pattern in sessions is "I already have a PRD, ingest it" (cd04962e, f0d2b976). `PrdGenerateCommand` exists, but `PrdLoadCommand` is what gets reached for first — generation is the fallback when the user has only an idea, not a document.
- **Seven canned template types over freeform.** `PrdGenerateCommand.cs:176` hard-codes `standard / technical / mobile / web / api / minimal / enterprise`. Keeps the prompt loop short and the generated structure validator-friendly, at the cost of flexibility for niche project shapes.
- **Interactive prompts default to on.** If `--idea` is not supplied, the command falls through to `AnsiConsole` prompts (template, output file). Non-interactive use requires every flag — chosen so CI runs fail loudly rather than silently generating an empty doc.
- **Service layer owns parsing, command layer owns UX.** `PrdService.GeneratePrdAsync` returns a `PrdGenerationResult` with `OutputFile`, `WordCount`, and `Sections`; the command only renders those into Spectre panels. Same service is reused by `load`, `validate`, and `requirements` so all five commands see the same document model.
- **PRD is level 3 on the spec ladder.** Session a5bba06a frames PRD as the narrative-requirements layer above BDD (level 2) and product-cli's typed graph (level 1). Decision: keep `pks prd` deliberately textual/markdown — formal contracts belong in a different command tree, not retrofitted here.

## Gotchas / known issues

- Sessions touching `pks prd` are sparse — most evidence is users *pasting* a PRD in as conversation input rather than running the command. The interactive generate flow has thin real-world coverage.
- `ee82d185` shows a Windows user running `pks-cli init .` inside `C:\dev\prd-generator` — the workflow assumes the user has already chosen a project directory; there is no `pks prd new` that bootstraps both.
- "Levels of spec" framing (session a5bba06a) implies PRD is upstream of BDD and product-cli graphs, but no command bridges them — exporting requirements as Gherkin or graph nodes is not implemented.
