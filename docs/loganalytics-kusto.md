# Log Analytics and raw KQL (`pks loganalytics`, `pks kusto`)

Sibling to the Application Insights vertical (`pks appinsights init` + `pks otel …`).
Where `otel` ships fixed, typed queries against App Insights resources, this pair points
at **Log Analytics workspaces** and lets an agent fire arbitrary KQL at them.

The `otel errors|traces|logs|spans` commands fan out the same way: every enabled App
Insights resource by default, `--resource <name-or-appid>` (repeatable) to narrow, each
row stamped with a `Resource` field (first column in tables, `"Resource"` in Json), rows
merged newest-first (spans oldest-first), per-resource failures on stderr, exit 1 only
when every resource failed. Tokens come from the tenant store per resource; an expired
tenant says `pks appinsights init --reauth <tenant>`.

## Configure once

```bash
pks loganalytics init                                   # checkbox list of workspaces across all signed-in tenants
pks loganalytics init --enable law-prod --disable law-dev   # non-interactive toggles
pks loganalytics init --reauth <tenant-id>              # the only way to get a browser for a known tenant
pks loganalytics init --workspace <workspace-guid>      # already know the GUID: no ARM lookup at all
pks loganalytics init --list                            # what is registered and enabled
pks loganalytics status                                 # entries + per-workspace connection test + tenant sign-in state
```

The shared model (tenant store, registry, `--force` never clearing credentials, migration
from the single-workspace keys) is in `azure-resources.md`.

`init` can be run more than once: every workspace it registers lands in the Azure
resource registry (`~/.pks-cli/azure-resources.json`) as an **enabled** entry with the
tenant it was found in, and `pks kusto` queries all of them by default.

Auth is per tenant. Each query takes its bearer token from the tenant credential store
for the workspace's own tenant, so two workspaces in two tenants each get the right
token. When that tenant's sign-in has expired the query fails with
`Run 'pks loganalytics init --reauth <tenant>'` — no browser is opened mid-query. The
old shared `pks foundry` credential is used only when the tenant store knows no tenant
at all (a pre-registry install that has not run `init` since).

What is stored is the workspace **GUID** (`properties.customerId`), not the ARM resource
id. That GUID is what the query API addresses workspaces by; the resource id is kept only
for display.

## Query

```bash
pks kusto "AppTraces | where SeverityLevel >= 3 | take 20"
pks kusto "AppExceptions | summarize count() by ProblemId" --since 24h
pks kusto --file query.kql --format Json
echo "Heartbeat | take 5" | pks kusto
pks kusto "Heartbeat | take 5" --workspace law-prod                 # one registered workspace, by name
pks kusto "Heartbeat | take 5" --workspace <other-workspace-guid>   # any GUID, registered or not
```

- With no `--workspace`, the query runs against **every enabled workspace in parallel**.
  `--workspace <name>` narrows to one enabled entry (unknown or disabled names exit 1 and
  list the enabled ones); `--workspace <GUID>` queries that GUID directly, registered or
  not, so it works with no configuration at all.
- `--since 30m|6h|7d` maps to the API's `timespan` property, so it applies **without
  rewriting the query**. Omit it and the query's own `TimeGenerated` filters decide.
- `--format Table` (default, cells truncated to 80 chars) prints one section per workspace
  with a `Workspace <name>` rule when there is more than one. `Json` is still **one flat
  array** of row objects (full fidelity — this is the one for agents), every row leading
  with a `"workspace": "<name>"` property, single target included. `Csv` gets a leading
  `workspace` column the same way. Json/Csv emit the first result table of each workspace.
- One workspace failing never stops the others: its error goes to **stderr** (so
  `--format Json | jq` still parses), the rest are printed, and the exit code is 0 as long
  as at least one workspace answered — 1 only when all of them failed. A rejected query
  prints the Kusto diagnostic, including the syntax position.
- `--verbose` lists the target workspaces (name, GUID, tenant) before the query.
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
