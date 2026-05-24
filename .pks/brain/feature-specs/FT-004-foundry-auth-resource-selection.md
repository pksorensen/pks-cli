---
id: FT-004
title: Azure AI Foundry auth & resource selection
domain: ai-providers
status: draft
adrs: []
tests: []
source-files: [src/Commands/Foundry/FoundryInitCommand.cs, src/Commands/Foundry/FoundryUsageCommand.cs, src/Commands/Foundry/FoundrySelectCommand.cs, src/Infrastructure/Services/AzureFoundryAuthService.cs]
sessions: [48ecbcc9-3201-45d2-b315-24cdc6975f43, 9d3ba702-b45c-4ed4-9947-b13000a21ddc, de4200b6-f68c-4c7b-910f-7880441ecd58, 37f77efc-c3cf-426d-95ce-abcb63e4ad75, 64fd343a-ff75-446b-b5e9-349427867e0c]
---

## Description
`pks foundry` is the user-facing flow for plugging a personal Azure AI Foundry resource into pks-cli: OAuth device-code login against the user's tenant, pick one Foundry/Cognitive Services resource, multi-select which model deployments to enable (chat, embeddings, tts, speech, a chosen "voice classifier"), and persist the refresh token + selection to `~/.pks-cli/foundry-credentials.json`. `pks foundry usage` then surfaces sponsorship/credit consumption scoped to that resource — modelled after `pks claude usage` (bar chart, daily breakdown). The persisted creds (TenantId, RefreshToken, SelectedResourceName, EnabledModels, ApiKey, VoiceClassifierModel) are the substrate for everything downstream: `pks claude`, `pks voice`, and `BuildFoundryEnvArgsAsync` injecting tokens into devcontainers.

## Intent

> From session 64fd343a (2026-05-13), prompt: "I leaed that people use services, so i am looking at foundry since i have acces sthere."

> From session 64fd343a (2026-05-13), prompt: "we need to be able to enable by ticknig off them odels we can provide tokens for - and what model is needed to do this transcription ?"

> From session 024c4bd0 (2026-05-05), prompt: "I would like to extend pks-cli azure command with usage  \"pks azure usage\" which does somethign like \"pks claude usage\". azure usage should let me pick a subscription and then print information form billing api.  then also a pks foundry usage is same code just scoped to a specific foundry instance where i can get information on usage"

## Key decisions
- **OAuth device-code over service principal** — the target user is a single developer with a sponsorship subscription, not a CI identity. Refresh token + TenantId stored in `~/.pks-cli/foundry-credentials.json` via `IConfigurationService`.
- **`MultiSelectionPrompt` for enabling models, separate single-select for "default"** — was originally a single-select that only let you pick one model; rewritten after the user pointed out they couldn't enable both `tts-hd` and a chat model together. Pre-ticks previously enabled deployments so re-running `foundry select` is non-destructive.
- **`VoiceClassifierModel` is a distinct slot** — not just "default model". `pks voice` needs a fast GPT deployment to fuzzy-match spoken commands; the user must pick which enabled deployment plays that role (with a "(none — use simple text matching)" escape hatch).
- **Both Bearer token AND `ApiKey` are persisted** — Azure AD bearer (scope `cognitiveservices.azure.com/.default`) works for `/openai/deployments/...`, but classic Speech REST (`/speech/recognition/...`) rejects it and needs `Ocp-Apim-Subscription-Key`. Storing both avoids a second login flow.
- **Usage UI mirrors `pks claude usage`** — bar chart + daily rollup, not a table. Explicit user feedback: "i think the idea was more like claude usage, taht we got a bar chart and daily usage ect?"

## Gotchas / known issues
- **No billing API for sponsorship subscriptions in some tenants** — `pks azure usage` prints `No billing profiles visible to this identity — skipping credit balance`; only cost-summary is reachable. `foundry usage` inherits this.
- **Speech API 404 with Bearer token** — see "ApiKey is persisted" above; if `creds.ApiKey` is missing, Speech calls 404 silently with a misleading "Resource not found" message rather than 401.
- **`foundry select` was single-select for several releases** — any creds file written before the multi-select rewrite has at most one entry in `EnabledModels`; re-running `foundry select` is the only migration path.
