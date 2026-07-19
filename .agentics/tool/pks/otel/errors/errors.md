---
title: "pks otel errors"
description: "Find the exception behind a production failure: scan recent errors from Application Insights, narrow by app and time window, then pivot on an operation ID."
tags: [how-to, observability, telemetry, application-insights]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks otel errors [APP] [options]"
examples:
  - command: "pks otel errors"
    description: "Last hour of exceptions across all apps"
  - command: "pks otel errors my-app --since 6h --limit 50"
    description: "Six hours of exceptions for one app"
  - command: "pks otel errors --operation-id abc123"
    description: "Exceptions tied to one request"
  - command: "pks otel errors --format Json"
    description: "Machine-readable output for jq"
---

`pks otel errors` queries the `exceptions` table in Azure Application Insights and prints the most recent exceptions first. It is the first stop when a deployed app misbehaves and you want the stack-level detail without opening the Azure Portal.

## 1. Prerequisites

- **An Application Insights resource registered with pks.** Run `pks appinsights init` once. It stores the App ID in the global pks config; `pks otel errors` refuses to run without it.
- **An Azure AD sign-in.** Run `pks foundry init`. The query service exchanges that identity for a token scoped to `https://api.applicationinsights.io/.default`.
- **Telemetry already flowing.** The command reads what the app has emitted; it does not instrument anything.

## 2. Scan the last hour

Start broad, with no filters at all.

```bash
pks otel errors
```

You get a rounded-border table of up to 20 exceptions from the last hour, newest first, with the exception message and the operation ID of the request that raised it.

Two columns are shortened for the terminal: the message is truncated to 60 characters and the operation ID to 16. That is fine for scanning and wrong for copying — see step 5.

## 3. Narrow by app and window

The optional positional argument is an application name, matched against the `cloud_RoleName` dimension. Use it when several apps report into the same Application Insights resource.

```bash
pks otel errors my-app --since 6h --limit 50
```

`--since` accepts a number with a trailing `d`, `h`, or `m` suffix. An unparseable value falls back to one hour without an error message, so check the value you typed if the window looks wrong.

## 4. Confirm what was actually queried

`pks otel errors` is the one command in the group that honors `--verbose`. It prints the resolved App ID, the resolved time window, and the exact KQL sent to Application Insights before the results.

```bash
pks otel errors my-app --since 6h --verbose
```

Use it whenever the result set surprises you — it separates "no matching telemetry" from "my filter did not mean what I thought".

## 5. Pivot on an operation ID

Once `pks otel traces` or a table row gives you an operation ID, pass it back in to isolate the exception raised by that one request.

```bash
pks otel errors --operation-id abc123
```

The filter is an exact match on `operation_Id`, so a truncated ID from the table will return nothing. Take the full value from JSON output instead.

```bash
pks otel errors --format Json
```

JSON is written compact, straight to standard output, with no ANSI escapes or Spectre.Console markup, so it pipes into `jq` cleanly.

## 6. Verify

Run the command with verbose output against a window you know contains an error.

```bash
pks otel errors --since 24h --verbose
```

You should see the App ID and the generated KQL printed first, then a table of exceptions. If you instead see `Application Insights is not configured.` the prerequisites in step 1 are not met.

## Options

| Flag | Default | Description |
|---|---|---|
| `--since <DURATION>` | `1h` | Time window, such as `1h`, `6h`, `24h`, or `7d`. Unparseable values fall back to one hour. |
| `--limit <COUNT>` | `20` | Maximum rows returned. Applied as the KQL `take` clause. |
| `--format <FORMAT>` | `Table` | Output format: `Table` or `Json`. |
| `--verbose`, `-v` | — | Print the App ID, the resolved window, and the KQL before the results. |
| `--operation-id <ID>` | — | Return only exceptions whose `operation_Id` matches exactly. |

The positional `APP` argument is optional and filters on the application name.

## Troubleshooting

**`Application Insights is not configured.`** — the command exits with code 1 and points at `pks appinsights init`. Run that first.

**`Not authenticated. Run 'pks foundry init' to sign in.`** — the App ID is stored but no Azure AD token is available. Sign in with `pks foundry init`.

**Empty table with no error.** — check the window with `--verbose`. A typo in `--since` silently degrades to one hour, and the app-name argument matches `cloud_RoleName` exactly.

**An operation ID from the table returns nothing.** — the table truncates operation IDs to 16 characters. Re-run with `--format Json` and copy the full value.

## Next steps

- [pks otel traces](/tools/pks/otel/traces) — find the failing request that produced the exception
- [pks otel spans](/tools/pks/otel/spans) — see which outbound call failed inside that request
- [pks otel logs](/tools/pks/otel/logs) — read the application log lines around the same trace
- [pks otel command reference](/tools/pks/otel/reference) — the full flag surface for all four commands
