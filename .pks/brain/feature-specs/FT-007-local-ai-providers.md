---
id: FT-007
title: Local AI providers (model registry, Google AI, Claude usage, TTS, image)
domain: ai-providers
status: draft
adrs: []
tests: []
source-files: [src/Commands/Model/ModelInitCommand.cs, src/Commands/Model/ModelListCommand.cs, src/Commands/Model/ModelRemoveCommand.cs, src/Commands/Model/ModelSettings.cs, src/Commands/Model/ModelStatusCommand.cs, src/Commands/Model/ModelUpdateCommand.cs, src/Commands/Google/GoogleInitCommand.cs, src/Commands/Google/GoogleStatusCommand.cs, src/Commands/Claude/ClaudeBackupCommand.cs, src/Commands/Claude/ClaudeSettings.cs, src/Commands/Claude/ClaudeSpawnCommand.cs, src/Commands/Claude/ClaudeStatsCommand.cs, src/Commands/Claude/ClaudeUsageCommand.cs, src/Commands/Tts/TtsCommand.cs, src/Commands/Image/ImageCommand.cs, src/Infrastructure/Services/ModelRegistryService.cs, src/Infrastructure/Services/ModelDownloadService.cs, src/Infrastructure/Services/GoogleAiService.cs]
sessions: [db1daceb-3ef0-4dab-8f2e-1523508106b2, 5d817688-c7ef-4497-988e-53d257d792db, 13973d8f-8531-4dbe-869d-7a36f7c19e81, 64fd343a-ff75-446b-b5e9-349427867e0c, f7a4850a-b97b-43f9-867d-a1b4849ef95b]
---

## Description
A grab-bag of local-side AI provider integrations that sit beside the Foundry/Azure stack: a model registry that downloads, extracts and tracks local ONNX bundles (Parakeet v3 etc.) for offline speech-to-text; a Google AI (Gemini) provider used primarily for image generation; analytics over Claude Code's own JSONL transcripts (`pks claude stats` / `pks claude usage`) to surface per-day cost bar charts; a `pks tts` text-to-speech primitive; and a `pks image` command that calls `IGoogleAiService.GenerateImageAsync` to produce on-brand social-media images. The shared design idea is that each provider is registered as a small Spectre.Console.Cli branch with its own settings and a thin service in `Infrastructure/Services/`, so users can mix Foundry-issued tokens, Google API keys and locally downloaded models without one provider knowing about another.

## Intent
> From session 13973d8f (2026-05-13), prompt: "I would like to extend pks-cli azure command with usage  \"pks azure usage\" which does somethign like \"pks claude usage\". azure usage should let me pick a subscription and then print information form billing api.  then also a pks foundry usage is same code just scoped to a specific foundry instance where i can get information on usage"

> From session 64fd343a (2026-05-13), prompt: "I want to use a voice to dictate my computer. i am looking at those realtime trascription or fast trascripions. I want to build a copy of whisper flow ect - and we tryed with hviske-v2 but found out it was to slow. I leaed that people use services, so i am looking at foundry since i have acces sthere."

> From session 64fd343a (2026-05-13), prompt: "we need to be able to enable by ticknig off them odels we can provide tokens for - and what model is needed to do this transcription ?" — surfaced while `pks foundry select` was showing `tts-hd` alongside the Claude deployments, which drove multi-select enablement for TTS/image/transcription models.

## Key decisions
- **Branch-per-model-name + command-per-verb** for `pks model <name> <verb>` (init/status/update/remove). The model name flows through Spectre's per-branch `context.Data`, so adding a new local model is one registration, not five command classes.
- **Services own the primitives, commands own the UX**: `ModelDownloadService` only knows how to fetch + extract tar.bz2 + hash; `ModelRegistryService` only persists registry JSON. The Parakeet URL lives in `CatalogEntry` data, not in the service.
- **Reuse Gemini for image rendering** instead of pulling in Skia/ImageSharp. The promptwall/image pipeline accepts Gemini's text-rendering quality so the only image dependency is `IGoogleAiService.GenerateImageAsync`.
- **`pks claude usage` is modelled on `ClaudeStatsCommand`** — same JSONL discovery under `~/.claude/projects/<encoded-cwd>/`, same per-project filtering — so transcript-reading conventions stay in one place and are shared by downstream features (promptwall, brain ingest).
- **Foundry models are opt-in per capability**: `pks foundry select` is a multi-select where TTS / image / transcription models are ticked independently from chat models, so the same Foundry credential can power TTS without forcing every consumer to enumerate every deployment.

## Gotchas / known issues
- Azure Speech REST API needs `Ocp-Apim-Subscription-Key`, not an Azure AD Bearer token — the first heypoul/TTS attempts returned `speech API 404` until the auth header was swapped. Any new Foundry-fronted speech/TTS call must honour the same split.
- `pks foundry select` initially listed only Claude deployments; surfacing `tts-hd` exposed that the selector had no notion of model *capability*, which is the reason for the multi-select + enabled-models persistence in `FoundryStoredCredentials`.
