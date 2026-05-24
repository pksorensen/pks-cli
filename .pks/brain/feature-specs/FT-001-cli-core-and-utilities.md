---
id: FT-001
title: CLI core, project identity & utilities
domain: cli-core
status: draft
adrs: []
tests: []
source-files: [src/Program.cs, src/Commands/InitCommand.cs, src/Commands/DeployCommand.cs, src/Commands/StatusCommand.cs, src/Commands/AsciiCommand.cs, src/Commands/Tools/ToolsPublishCommand.cs, src/Commands/Tools/ToolsSettings.cs, src/Commands/Image/ImageCommand.cs, src/Commands/Tts/TtsCommand.cs, src/Commands/Promptwall/PromptwallCommand.cs, src/Commands/Promptwall/PromptwallTranscript.cs, src/Infrastructure/Services/ProjectIdentityService.cs]
sessions: [db1daceb-3ef0-4dab-8f2e-1523508106b2, 13973d8f-8531-4dbe-869d-7a36f7c19e81, 64fd343a-ff75-446b-b5e9-349427867e0c, ee82d185-bed1-47e9-bd14-02aabe5671c8, af8f146f-a46e-4f3e-8d0e-71e318904b67]
---

## Description
This is the first surface every user touches: `pks init` scaffolds a project (template selection,
devcontainer prompt, project identity), `pks deploy` / `pks status` cover the lightweight ops
loop, `pks ascii` and the boot banner give the tool its character, and `pks tools publish` ships
the binary itself. Around them sit small but visible utilities — `image`, `tts`, `promptwall` —
that ride the same Spectre.Console.Cli + DI plumbing in `Program.cs`. `ProjectIdentityService`
stamps a stable project id/slug used by everything downstream (brain ingest, telemetry, foundry
env injection). The design intent is that a user can go from "vibe-start" to a fully wired
agentic project without paying upfront engineering overhead.

## Intent

> "Jeg tror rigtig meget på den her 'start vibe' => 'go professionel' - og jeg har mange af mine
> projekter jeg starter uden noget som helst. Men jeg vil også gerne have alt det nince
> engineering ned af vejen men jeg gider ikke betale overhead i starten hvis jeg ikke ved om
> projektet bliver til noget. … men at jeg bare kan have en background agent der står og cruncher
> det for at så på bagsiden faktisk bygge ADR, feature besrkivelser og whatnot op automatisk
> løbende uden jeg behøver at tænkte over det, jeg viber bare der ud af."
> — From session db1daceb (2026-05-14), prompt introducing `pks brain init` as the next surface
> after `pks init`.

> "PS C:\dev\prd-generator> dotnet dnx pks-cli init . --force … Would you like to spawn the
> devcontainer now? (Opens in Docker volume with VS Code) [y/n] (y): y"
> — From session ee82d185 (2026-05-11), prompt showing the expected end-to-end flow: `init`
> immediately offers to hand off to `devcontainer spawn`.

> "I would like to extend pks-cli azure command with usage 'pks azure usage' which does
> somethign like 'pks claude usage'. … please use plan mode and ask me all questions there"
> — From session 13973d8f (2026-05-12), prompt confirming the pattern that every new top-level
> verb is added as a sibling command under the same root, mirroring an existing one.

## Key decisions
- **Spectre.Console.Cli + Microsoft DI bridge.** `Program.cs` wires everything through
  `TypeRegistrar`/`TypeResolver` so commands get constructor-injected services. New verbs are
  added as siblings in `Program.cs`, never as a separate startup.
- **One project identity, many consumers.** `ProjectIdentityService` produces the slug/id that
  ingest, foundry env injection, and telemetry all key off — written once in `init`, read
  everywhere else. Avoids each command rolling its own "what project is this" logic.
- **`init` is a pipeline, not a monolith.** Conditional `IInitializer` implementations
  auto-discovered by reflection, ordered, each gated by `ShouldRunAsync`. Lets templates layer
  cleanly (template files → devcontainer → identity → optional spawn handoff).
- **Banner + ASCII is part of the product, not decoration.** The boot banner ("Poul's Killer
  Swarms") and `pks ascii` are intentional brand surface; `--no-logo` exists for scripted use
  (seen in `pks azure usage` transcripts).
- **Small utilities follow the same shape.** `image`, `tts`, `promptwall`, `tools publish` are
  each one-file commands using the same DI/Settings pattern — deliberately not abstracted into
  a plugin framework, so adding a verb stays a 50-line change.

## Gotchas / known issues
- `init . --force` on an existing directory followed by an offered "spawn devcontainer now?"
  prompt can fail downstream against an unreachable remote (`Cannot connect to azureuser@…:22`),
  with the failure surfacing after init already wrote files — observed in session ee82d185. The
  init itself succeeded; the chained spawn is what broke.
- The boot banner is emitted before argument parsing, so anything that shells out to `pks`
  programmatically must use `--no-logo` to keep stdout parseable (pattern visible in the
  `pks azure usage` transcript from session 13973d8f).
