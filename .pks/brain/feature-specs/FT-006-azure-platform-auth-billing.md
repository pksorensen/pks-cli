---
id: FT-006
title: Azure platform (auth, billing, init)
domain: ai-providers
status: draft
adrs: []
tests: [tests/Commands/AzureInitCommandTests.cs]
source-files: [src/Commands/Azure/AzureInitCommand.cs, src/Commands/Azure/AzureUsageCommand.cs, src/Commands/Azure/AzureSettings.cs, src/Commands/Azure/AzureUsageSettings.cs, src/Commands/Azure/CostChart.cs, src/Infrastructure/Services/AzureAuthService.cs, src/Infrastructure/Services/AzureBillingService.cs]
sessions: [13973d8f-8531-4dbe-869d-7a36f7c19e81, a160e3cc-2a06-4df0-acc3-8c686583a4fd, agent-a7d8c25c2f895dfa3, agent-a44f27120fe056658, agent-a807e9b39e3ab013a]
---

## Description
The Azure platform layer provides a single OAuth2-PKCE login and subscription-selection
flow (`pks azure init`) plus a shared billing/Cost-Management client that the rest of
the suite reuses — Foundry (FT-4/5), VM (FT-3), and AppInsights (FT-17) all consume the
same stored Azure credentials and ARM access tokens instead of each shelling out to
`az`. `AzureAuthService` handles PKCE against `login.microsoftonline.com`, tenant
discovery from an email, refresh-token persistence under `~/.pks-cli/`, and on-demand
scoped access-token acquisition. `AzureBillingService` wraps three ARM surfaces:
`Microsoft.Billing` billing-profile/credit-lot enumeration, classic Consumption usage,
and the modern Cost Management `query` endpoint with daily-granularity rollups powering
`pks azure usage`'s bar chart (`CostChart.cs`). `AzureUsageCommand` reuses the same
query path so a future `pks foundry usage` can scope identically to a single
CognitiveServices resource.

## Intent

> From session 13973d8f (2026-05-13), prompt:
> "We have an azure sponsorship subscription but i cant see any billing information on
> credit used. Do you know if billing information is avaible with api in that case or
> its simply is only the sponsorship portal we can see it on."

> From session 13973d8f (2026-05-13), prompt:
> "I would like to extend pks-cli azure command with usage  \"pks azure usage\" which
> does somethign like \"pks claude usage\". azure usage should let me pick a
> subscription and then print information form billing api.  then also a pks foundry
> usage is same code just scoped to a specific foundry instance where i can get
> information on usage"

> From session a160e3cc (2026-05-04), prompt:
> "do not use azure-cli.  Plan out the features.  but we dont want all those
> --arguments, just build it into the interactive prompt."

## Key decisions
- **Native OAuth2 + PKCE, no `az` shellout.** `AzureAuthService` opens a loopback
  HttpListener and drives the auth code flow itself so PKS never depends on Azure CLI
  being installed or logged-in on the host or VM.
- **One credential store, many consumers.** Foundry, VM, and AppInsights services all
  obtain ARM tokens via `IAzureAuthService.GetAccessTokenAsync(scope)` rather than
  holding their own refresh tokens — `~/.pks-cli/` is the single source of truth.
- **Cost Management `query` is the canonical billing surface.** Sponsorship balance via
  `Microsoft.Billing` 403s for most identities, so `AzureBillingService` treats
  403/404 as "no profiles visible" and falls back to the per-subscription Cost
  Management `query` endpoint that always works on a sub the user can read.
- **Scope is a string, not a type.** `QueryCostAsync(accessToken, scope, …)` accepts
  any ARM path so the same code serves `pks azure usage` (subscription scope) and the
  intended `pks foundry usage` (single CognitiveServices account scope).
- **Interactive-first commands, minimal flags.** Per the user's correction, new
  commands (`init`, `usage`) lean on Spectre prompts; only `--force` / `--tenant`
  remain as flags.

## Gotchas / known issues
- **Legacy Sponsorship balance is not API-reachable.** Even when `pks azure usage`
  succeeds for cost, the actual credit balance lives only at
  `microsoftazuresponsorships.com/balance` — the command prints an explicit "skipping
  credit balance" line in that case.
- **First `usage` output was a flat table.** Initial implementation missed the bar
  chart the user expected from `pks claude usage`; `CostChart.cs` was added in a
  follow-up edit to render daily-granularity cost as the bar chart the prompt asked
  for.
