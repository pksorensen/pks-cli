---
title: "pks otel traces"
description: "List recent requests from Application Insights with duration, result code, and success flag, then take an operation ID into the exception and span views."
tags: [how-to, observability, telemetry, application-insights]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks otel traces [APP] [options]"
examples:
  - command: "pks otel traces"
    description: "Last hour of requests"
  - command: "pks otel traces --has-error --since 24h"
    description: "Failed requests over the last day"
  - command: "pks otel traces my-app --limit 50"
    description: "Fifty most recent requests for one app"
  - command: "pks otel traces --format Json"
    description: "Machine-readable output for jq"
---

`pks otel traces` queries the `requests` table in Azure Application Insights — one row per inbound request, with duration, result code, and a success flag. It answers "which endpoint is slow or failing", and it is where you pick up the operation ID that the other three commands consume.

## 1. Prerequisites

- **`pks appinsights init`** — stores the App ID this command queries. Without it, the command warns and exits with code 1.
- **`pks foundry init`** — establishes the Azure AD identity used to obtain the query token.

## 2. Look at recent requests

```bash
pks otel traces
```

You get up to 20 requests from the last hour. The status column renders a green check or a red cross derived from the `success` field, alongside the result code and duration.

## 3. Filter to failures only

```bash
pks otel traces --has-error --since 24h
```

`--has-error` adds `success == false` to the query. Without the flag, successful and failed requests are both returned, which is what you want when you are hunting latency rather than errors.

## 4. Narrow to one app and widen the window

```bash
pks otel traces my-app --since 7d --limit 50
```

The positional argument filters on the application name. `--since` accepts a number with a `d`, `h`, or `m` suffix; anything else silently falls back to one hour.

## 5. Take the operation ID onward

Copy the operation ID from a failing row and use it as the pivot into the other views.

```bash
pks otel errors --operation-id abc123
pks otel spans --operation-id abc123
pks otel logs --trace-id abc123
```

Because table output shortens long values, take the ID from JSON when the exact-match filters return nothing.

```bash
pks otel traces --has-error --format Json
```

JSON mode exposes both `Success` and a separate `HasError` field, which is `Success` inverted. Field names are PascalCase, matching the underlying C# model, not the lowercase KQL column names. It is written compact to standard output with no terminal markup, so it pipes into `jq` directly.

## 6. Verify

```bash
pks otel traces --since 24h
```

You should see a table of requests with a status column. If you see `Application Insights is not configured.` instead, complete step 1.

## Options

| Flag | Default | Description |
|---|---|---|
| `--since <DURATION>` | `1h` | Time window, such as `1h`, `6h`, `24h`, or `7d`. |
| `--limit <COUNT>` | `20` | Maximum rows returned. |
| `--format <FORMAT>` | `Table` | Output format: `Table` or `Json`. |
| `--has-error` | — | Return only requests where `success` is false. |
| `--verbose`, `-v` | — | Accepted for consistency with the group, but this command never reads it. No effect. |

The positional `APP` argument is optional and filters on the application name.

## Troubleshooting

**`-v` prints nothing extra.** — expected. Only `pks otel errors` implements the verbose query dump. Use that command to inspect the generated KQL.

**Everything looks healthy but users report errors.** — a request can succeed at the HTTP layer while an outbound call fails. Check `pks otel spans` for the operation ID.

**No rows at all.** — the app-name argument matches the `cloud_RoleName` dimension exactly. Drop the argument to confirm telemetry is arriving at all.

## Next steps

- [pks otel errors](/tools/pks/otel/errors) — the exception behind a failed request
- [pks otel spans](/tools/pks/otel/spans) — the outbound-call waterfall for one operation ID
- [pks otel logs](/tools/pks/otel/logs) — application log lines for the same trace
- [pks otel command reference](/tools/pks/otel/reference) — every flag and default in the group
