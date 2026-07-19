---
title: "pks otel spans"
description: "Show the outbound dependency calls a single request made — HTTP, database, and more — ordered oldest first, for one Application Insights operation ID."
tags: [how-to, observability, telemetry, distributed-tracing]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks otel spans --operation-id <ID> [options]"
examples:
  - command: "pks otel spans --operation-id abc123"
    description: "Dependency-call waterfall for one trace"
  - command: "pks otel spans --operation-id abc123 --format Json"
    description: "Same waterfall, machine-readable"
---

`pks otel spans` queries the `dependencies` table in Azure Application Insights for one operation ID and prints the outbound calls that request made — HTTP calls, database calls, and anything else the SDK recorded — ordered oldest first. It is the last step of a drill-down, not a browsing command.

> **Note.** The time window is fixed at 24 hours and cannot be changed. A trace older than that returns no spans.

## 1. Prerequisites

- **`pks appinsights init`** — stores the App ID. Without it the command warns and exits with code 1.
- **`pks foundry init`** — provides the Azure AD identity used to obtain the query token.
- **An operation ID.** This command has no browse mode. Get the ID from [pks otel traces](/tools/pks/otel/traces) or [pks otel errors](/tools/pks/otel/errors) first.

## 2. Fetch the waterfall

```bash
pks otel spans --operation-id abc123
```

You get a table of dependency calls in chronological order, with the call target, duration, and success flag. The target column can be blank for spans with no network target.

`--operation-id` is required. Omit it and validation fails before the query runs, with `--operation-id is required for spans`.

## 3. Read it as a sequence

The rows are ordered oldest first on purpose. Read down the list and look for the two shapes that explain most incidents: one call that consumed most of the request's duration, or the first call that failed and everything that stopped after it.

Cross-check the total against the request duration reported by `pks otel traces` for the same operation ID. A large gap means the time went somewhere other than an outbound call.

## 4. Watch the 24-hour boundary

The window is baked into the query as `ago(24h)`. There is no `--since` on this command. An exception you can still find with `pks otel errors --since 7d` may have no retrievable spans, because the exception and the dependency records age out of this command's reach at different points.

When you are investigating something older than a day, use `pks otel logs --trace-id` instead, where the window is yours to set.

## 5. Verify

```bash
pks otel spans --operation-id abc123 --format Json
```

You should see compact JSON on standard output, one object per dependency call, with `null` in place of a missing target. JSON is written with no terminal markup, so it pipes into `jq` cleanly.

## Options

| Flag | Required | Description |
|---|---|---|
| `--operation-id <ID>` | yes | Operation or trace ID to fetch dependency spans for. Validation fails when it is missing or whitespace. |
| `--format <FORMAT>` | no | Output format: `Table` or `Json`. Defaults to `Table`. |

This command does not accept `--since`, `--limit`, `--verbose`, or a positional application-name argument. Flag parity across the `otel` group is not guaranteed.

## Troubleshooting

**`--operation-id is required for spans`** — the flag is mandatory; the command aborts before querying.

**Empty result for a real trace.** — the fixed 24-hour window is the usual cause. Confirm the request age with `pks otel traces --since 7d`.

**Blank target column.** — some spans record no network target. JSON output shows `null` for the same rows.

**`Application Insights is not configured.`** — run `pks appinsights init`, then `pks foundry init`.

## Next steps

- [pks otel traces](/tools/pks/otel/traces) — find the operation ID this command needs
- [pks otel errors](/tools/pks/otel/errors) — the exception raised inside the same request
- [pks otel logs](/tools/pks/otel/logs) — log lines for the same trace, over a window you choose
- [pks otel command reference](/tools/pks/otel/reference) — every flag and default in the group
