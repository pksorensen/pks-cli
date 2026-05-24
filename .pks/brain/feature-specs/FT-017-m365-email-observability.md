---
id: FT-017
title: Microsoft 365 / email / observability (MS Graph, Email, AppInsights, OTEL)
domain: integrations-identity-data + observability
status: draft
adrs: []
tests: [tests/Commands/AppInsights/AppInsightsCommandTests.cs, tests/Commands/Otel/OtelCommandTests.cs]
source-files: [src/Commands/MsGraph/MsGraphRegisterCommand.cs, src/Commands/MsGraph/MsGraphSettings.cs, src/Commands/Email/EmailExportCommand.cs, src/Commands/Email/EmailSettings.cs, src/Commands/AppInsights/AppInsightsInitCommand.cs, src/Commands/AppInsights/AppInsightsStatusCommand.cs, src/Commands/AppInsights/AppInsightsSettings.cs, src/Commands/Otel/OtelTracesCommand.cs, src/Commands/Otel/OtelLogsCommand.cs, src/Commands/Otel/OtelErrorsCommand.cs, src/Commands/Otel/OtelSpansCommand.cs, src/Commands/Otel/OtelQuerySettings.cs, src/Commands/Otel/OtelSettings.cs, src/Infrastructure/Services/MsGraphAuthenticationService.cs, src/Infrastructure/Services/MsGraphEmailService.cs, src/Infrastructure/Services/MsGraphEmailExportService.cs, src/Infrastructure/Services/AppInsightsConfigService.cs, src/Infrastructure/Services/AppInsightsQueryService.cs, src/Infrastructure/Services/Logging/LoggingOrchestrator.cs, src/Infrastructure/Services/Logging/CommandTelemetryService.cs, src/Infrastructure/Services/Logging/CommandLoggingWrapper.cs]
sessions: [de4200b6-f68c-4c7b-910f-7880441ecd58, c2814099-d510-41e4-b7a4-edb84adbd05b, db1daceb-3ef0-4dab-8f2e-1523508106b2, 13973d8f-8531-4dbe-869d-7a36f7c19e81, af53e229-8629-4bff-869e-b78e8e75accf]
---

## Description

This bundle wires `pks` into the Microsoft 365 and Azure observability surfaces.
`pks msgraph register` runs the PKCE/device-code flow against MS Graph and
persists the resulting tokens via `MsGraphAuthenticationService`; `pks email
export` then drives `MsGraphEmailService` / `MsGraphEmailExportService` to pull
mailbox archives (folders, messages, attachments) into a local export tree.
The observability half — `pks appinsights init|status` plus `pks otel
traces|logs|errors|spans` — uses Azure subscription discovery via PKCE
(`AppInsightsConfigService`) to pick an Application Insights resource, then
queries traces, structured logs, errors and spans through
`AppInsightsQueryService` over arbitrary `Nh/Nd/Nm` time windows. The shared
`Infrastructure/Services/Logging/` orchestrator (LoggingOrchestrator,
CommandTelemetryService, CommandLoggingWrapper) is what every other command
emits into, so this FT both *produces* OTEL/AppInsights data from inside pks
and *queries* it back out from the same CLI.

## Intent

> I would like to extend pks-cli azure command with usage  "pks azure usage"
> which does somethign like "pks claude usage". azure usage should let me pick
> a subscription and then print information form billing api.  then also a pks
> foundry usage is same code just scoped to a specific foundry instance where
> i can get information on usage

— session 13973d8f (2026-05-09), framing the AppInsights/usage query surface
that the `appinsights` + `otel` query commands grew out of.

> We have an azure sponsorship subscription but i cant see any billing
> information on credit used.
> Do you know if billing information is avaible with api in that case or its
> simply is only the sponsorship portal we can see it on.

— session 13973d8f (2026-05-09), the original "can we even read this from an
API" question that pushed init/status toward PKCE-driven subscription
discovery instead of a stored API key.

> i think the idea was more like claude usage, taht we got a bar chart and
> daily usage ect?

— session 13973d8f (2026-05-09), pinning the `pks otel`/`appinsights status`
output shape to the same daily bar-chart pattern used by `pks claude usage`.

## Key decisions

- **MS Graph auth is its own service, not a wrapper around foundry auth** —
  `MsGraphAuthenticationService` stores its own token cache and PKCE config so
  `pks email export` can run without first calling `pks foundry init`, matching
  the way `pks appinsights init` was redesigned to "trigger PKCE auth itself
  instead of requiring pks foundry init" (commit `86dfe0a`).
- **AppInsights resource selection goes through Azure subscription discovery,
  not a flat config file** — `AppInsightsConfigService` enumerates
  subscriptions → resource groups → AI resources via PKCE-acquired ARM tokens
  (commit `bdcdd5c`), so the user picks an AI instance interactively instead
  of pasting an instrumentation key.
- **OTEL query commands accept arbitrary `Nh/Nd/Nm` durations and a
  `--verbose` debug flag** (commit `301ec39`) — windows are not fixed buckets,
  so the same command set serves both "last 15m" triage and "last 30d"
  reviews.
- **Email export is a streaming pull, not a one-shot dump** —
  `MsGraphEmailExportService` is split from `MsGraphEmailService` so the
  paginated Graph traversal (folders → messages → attachments) is testable
  independently of the auth + raw API surface.
- **Every other pks command emits into `Logging/` so AppInsights/OTEL
  queries see real data** — `CommandLoggingWrapper` + `CommandTelemetryService`
  give a single sink that the AppInsights configured here actually receives,
  closing the produce/query loop inside one CLI.

## Gotchas / known issues

- Legacy Azure Sponsorship subscriptions do not expose credit balance via the
  billing API — only via `microsoftazuresponsorships.com/balance` (surfaced as
  the "No billing profiles visible to this identity — skipping credit balance"
  message in `pks azure usage` output, session 13973d8f).
- `pks appinsights init` originally required `pks foundry init` to run first;
  fixed in `86dfe0a` so the command bootstraps its own PKCE flow — older
  scripts that chained the two no longer need to.
- AppInsights resource-group display + status spinner were a UX trap (wrong
  resource picked, spinner masking errors) — fixed in `c8ce25a` to show RG in
  the prompt and drop the spinner.
- OTEL duration parsing used to assume fixed units; arbitrary `Nh/Nd/Nm`
  parsing landed in `301ec39` along with `--verbose` because silent empty
  results were hard to distinguish from "window too narrow".

