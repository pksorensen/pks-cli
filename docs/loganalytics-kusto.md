# Log Analytics and raw KQL (`pks loganalytics`, `pks kusto`)

Sibling to the Application Insights vertical (`pks appinsights init` + `pks otel …`).
Where `otel` ships fixed, typed queries against an App Insights app, this pair points
at a **Log Analytics workspace** and lets an agent fire arbitrary KQL at it.

## Configure once

```bash
pks loganalytics init                                   # pick subscription + workspace interactively
pks loganalytics init --subscription <sub-id> --workspace law-prod   # non-interactive
pks loganalytics init --workspace <workspace-guid>      # already know the GUID: no ARM lookup at all
pks loganalytics status                                 # config + live connection test
```

Auth is the shared `pks foundry` credential (Azure CLI public client, refresh token in
`~/.pks-cli/settings.json`). `init` signs in only when there is no stored credential;
unlike `appinsights init --force`, `loganalytics init --force` does **not** clear those
credentials — it only re-picks the workspace, so it can't knock out the other verticals.

What is stored is the workspace **GUID** (`properties.customerId`), not the ARM resource
id. That GUID is what the query API addresses workspaces by; the resource id is kept only
for display.

## Query

```bash
pks kusto "AppTraces | where SeverityLevel >= 3 | take 20"
pks kusto "AppExceptions | summarize count() by ProblemId" --since 24h
pks kusto --file query.kql --format Json
echo "Heartbeat | take 5" | pks kusto
pks kusto "Heartbeat | take 5" --workspace <other-workspace-guid>   # bypasses the configured one
```

- `--since 30m|6h|7d` maps to the API's `timespan` property, so it applies **without
  rewriting the query**. Omit it and the query's own `TimeGenerated` filters decide.
- `--format Table` (default, cells truncated to 80 chars), `Json` (array of row objects,
  full fidelity — this is the one for agents), `Csv`. Json/Csv emit the first result table.
- `--workspace <GUID>` works with no configuration at all.
- A rejected query exits 1 and prints the Kusto diagnostic, including the syntax position.
- `kusto` is on the banner-suppression list in `Program.cs` (like `claude limits`), so stdout is
  the result and nothing else in every format — `pks kusto … --format Json | jq` works as-is.

## Verified API facts (2026-08-12, live)

- Token scope is **`https://api.loganalytics.io/.default`**. `https://api.loganalytics.azure.com/.default`
  fails with AADSTS500011 in our tenant — the resource principal isn't there.
- ARM listing: `Microsoft.OperationalInsights/workspaces?api-version=2022-10-01`.
- Request body: `{"query": "...", "timespan": "PT6H"}` — ISO 8601 duration, optional.
- Errors come back as HTTP 400 with the real diagnostic **nested** inside
  `error.innererror.innererror`; the outer message is only "The request had some invalid
  properties". `LogAnalyticsQueryService.FormatApiError` walks that chain, which is why
  the adapter must not call `EnsureSuccessStatusCode()` — that throws the body away.

## Not built (deliberate)

`/v1/workspaces/{id}/metadata` would give table/schema discovery (`kusto tables`,
`kusto schema <table>`). Worth adding when an agent needs to explore an unfamiliar
workspace; not needed to run queries. Neither command is exposed over MCP yet.
