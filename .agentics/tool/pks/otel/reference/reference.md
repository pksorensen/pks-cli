---
title: "pks otel command reference"
description: "Complete command, argument, and flag reference for the pks otel telemetry query group — errors, traces, logs, spans, prerequisites, and behavioral gaps."
tags: [reference, cli, observability, application-insights]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks otel <command> [options]"
examples:
  - command: "pks otel errors my-app --since 6h --limit 50"
    description: "Six hours of exceptions for one app"
  - command: "pks otel traces --has-error --since 24h"
    description: "Failed requests over the last day"
  - command: "pks otel logs --severity Error --since 7d"
    description: "Errors and above from application logs"
  - command: "pks otel spans --operation-id abc123"
    description: "Dependency waterfall for one trace"
---

`pks otel` is a read-only query group in the pks CLI. It builds KQL (Kusto Query Language), obtains an Azure AD token for the Application Insights data plane, and POSTs the query to `https://api.applicationinsights.io/v1/apps/{appId}/query`. Four commands cover exceptions, requests, application logs, and dependency spans.

The group is registered as a branch in the pks command tree with no aliases on the branch or on any leaf, and there are no hidden subcommands. It has no `init` and no `login` of its own: the App ID comes from `pks appinsights init` and the identity from `pks foundry init`.

## Synopsis

```text
pks otel <command> [options]
```

```text
errors [APP]     List recent exceptions from the exceptions table
traces [APP]     List recent requests from the requests table
logs   [APP]     List structured log entries from the traces table
spans            List dependency spans for one operation ID
```

### Prerequisites

Every command checks configuration before doing anything else.

| Requirement | Established by | Failure behavior |
|---|---|---|
| Application Insights App ID | `pks appinsights init` | Prints `Application Insights is not configured.` plus `Run pks appinsights init to configure.` and returns exit code 1. |
| Azure AD access token for `https://api.applicationinsights.io/.default` | `pks foundry init` | Throws `Not authenticated. Run 'pks foundry init' to sign in.` from the query service. |

The App ID, resource name, and subscription ID are stored in the pks global configuration, not per project. The `otel` group reads no environment variables.

### Shared options

`errors`, `traces`, and `logs` share one settings type; `spans` does not.

| Flag | Default | Purpose |
|---|---|---|
| `--since <DURATION>` | `1h` | Time window. Accepts a number with a trailing `d`, `h`, or `m` suffix. Unparseable values fall back to one hour with no warning. |
| `--limit <COUNT>` | `20` | Maximum rows, applied as the KQL `take` clause. Inert on `logs`. |
| `--format <FORMAT>` | `Table` | `Table` renders a rounded-border Spectre.Console table. `Json` writes compact JSON to standard output with no markup. |
| `--verbose`, `-v` | — | Print the App ID, resolved window, and generated KQL. Implemented on `errors` only. |

The optional positional `APP` argument on `errors`, `traces`, and `logs` filters on the application name, matched against the `cloud_RoleName` dimension.

## errors [APP]

Queries the `exceptions` table and returns the most recent exceptions first, bounded by `--since` and `--limit`. Table output truncates the exception message to 60 characters and the operation ID to 16; `--format Json` carries the untruncated values. This is the only command in the group that acts on `--verbose`.

| Flag | Default | Description |
|---|---|---|
| `--since <DURATION>` | `1h` | Time window for the query. |
| `--limit <COUNT>` | `20` | Maximum rows returned. |
| `--format <FORMAT>` | `Table` | Output format: `Table` or `Json`. |
| `--verbose`, `-v` | — | Print the App ID, resolved window, and KQL before the results. |
| `--operation-id <ID>` | — | Exact match on `operation_Id`. |

**Endpoint:** `POST /v1/apps/{appId}/query`.

```bash
pks otel errors my-app --since 6h --limit 50 --verbose
```

## traces [APP]

Queries the `requests` table — one row per inbound request, with duration, result code, and success flag. Table output renders the status as a green check or a red cross derived from `success`. JSON output exposes both `Success` and a separate `HasError` field (PascalCase, matching the underlying C# model), which is `Success` inverted.

| Flag | Default | Description |
|---|---|---|
| `--since <DURATION>` | `1h` | Time window for the query. |
| `--limit <COUNT>` | `20` | Maximum rows returned. |
| `--format <FORMAT>` | `Table` | Output format: `Table` or `Json`. |
| `--has-error` | — | Restrict results to requests where `success` is false. |
| `--verbose`, `-v` | — | Accepted but never read by this command. No effect. |

**Endpoint:** `POST /v1/apps/{appId}/query`.

```bash
pks otel traces --has-error --since 24h --format Json
```

## logs [APP]

Queries the `traces` table, which holds application log entries rather than distributed-tracing spans. Filters are a minimum severity, a trace identifier, or both. Severity names map to the numeric `severityLevel` and are compared with greater-than-or-equal, so `Warning` also returns `Error` and `Critical`. Unrecognized severity names apply no filter at all. Table output truncates the message to 70 characters.

| Flag | Default | Description |
|---|---|---|
| `--severity <LEVEL>` | — | Minimum severity: `Trace`, `Info` (or `Information`), `Warning`, `Error`, or `Critical`. Case-insensitive. |
| `--trace-id <ID>` | — | Exact match on `operation_Id`. |
| `--since <DURATION>` | `1h` | Time window for the query. |
| `--format <FORMAT>` | `Table` | Output format: `Table` or `Json`. |
| `--limit <COUNT>` | `20` | Accepted but never applied. The generated KQL carries no `take` clause. |
| `--verbose`, `-v` | — | Accepted but never read by this command. No effect. |

**Endpoint:** `POST /v1/apps/{appId}/query`.

> **Note.** Because `--limit` is inert, a wide `--since` with no severity or trace filter can return a very large result set. The Application Insights query API's own result limits are the only ceiling.

> **Known issue.** The query always projects the numeric `severityLevel` column, and the row mapper reads it with a string-only accessor. Any query that returns at least one log line currently throws an unhandled `InvalidOperationException` instead of rendering output, regardless of whether `--severity` is set.

```bash
pks otel logs --trace-id abc123 --format Json
```

## spans

Queries the `dependencies` table for one operation ID and returns outbound calls ordered oldest first. This command's settings type is separate from the other three: there is no `--since`, no `--limit`, no `--verbose`, and no positional application argument. The time window is fixed at `ago(24h)` inside the query and is not configurable.

| Flag | Required | Description |
|---|---|---|
| `--operation-id <ID>` | yes | Operation or trace identifier. Validation fails with `--operation-id is required for spans` when missing or whitespace. |
| `--format <FORMAT>` | no | Output format: `Table` or `Json`. Defaults to `Table`. |

**Endpoint:** `POST /v1/apps/{appId}/query`.

The target column is nullable: spans with no network target render blank in table output and `null` in JSON.

```bash
pks otel spans --operation-id abc123 --format Json
```

## Behavior worth knowing

| Behavior | Commands affected |
|---|---|
| `--limit` accepted but never applied | `logs` |
| `--verbose` accepted but never read | `traces`, `logs` |
| Fixed 24-hour window, no `--since` | `spans` |
| Unparseable `--since` silently degrades to `1h` | `errors`, `traces`, `logs` |
| Unrecognized `--severity` silently applies no filter | `logs` |
| Any non-empty result throws `InvalidOperationException` (numeric `severityLevel` read as a string) | `logs` |
| JSON written to standard output, bypassing the console renderer | all four |

## See also

- [pks otel](/tools/pks/otel) — the group landing page and mental model
- [pks otel errors](/tools/pks/otel/errors) — exception triage walkthrough
- [pks otel traces](/tools/pks/otel/traces) — request and latency walkthrough
- [pks otel logs](/tools/pks/otel/logs) — severity and trace-scoped log reading
- [pks otel spans](/tools/pks/otel/spans) — dependency waterfall for one request
