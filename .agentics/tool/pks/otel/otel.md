---
title: "pks otel"
description: "Query exceptions, requests, structured logs, and dependency spans from Azure Application Insights in your terminal, without opening the Azure Portal."
tags: [cli, observability, telemetry, application-insights]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks otel <command> [options]"
examples:
  - command: "pks otel errors --since 6h"
    description: "Recent exceptions across every instrumented app"
  - command: "pks otel traces --has-error --since 24h"
    description: "Failed requests over the last day"
  - command: "pks otel logs --severity Error --since 7d"
    description: "Errors and above from application logs"
  - command: "pks otel spans --operation-id abc123"
    description: "Dependency-call waterfall for one trace"
---

`pks otel` is the read-only telemetry query group in the pks CLI. It runs canned KQL (Kusto Query Language) against an Azure Application Insights resource and prints the result as a table or as JSON.

## Overview

`pks otel` wraps the Application Insights REST Query API (`https://api.applicationinsights.io/v1/apps/{appId}/query`) behind four commands. Instead of opening the Azure Portal and writing a query by hand, you ask for the four things you actually want during an incident.

- **Exceptions first.** `pks otel errors` reads the `exceptions` table, newest first, so you see what broke.
- **Requests next.** `pks otel traces` reads the `requests` table — duration, success flag, result code — so you see which endpoints are slow or failing.
- **Application logs.** `pks otel logs` reads the `traces` table, filtered by minimum severity or by a single trace ID.
- **Outbound calls.** `pks otel spans` reads the `dependencies` table for one operation ID, oldest first — the waterfall of what that request called.

## What you get

- **Terminal-native incident triage.** Four bounded queries covering exceptions, requests, logs, and dependency spans, each returning in one round trip.
- **A correlation pivot.** Every command surfaces the operation ID, and three of the four accept one, so you can walk from a failed request to its exception, its log lines, and its outbound calls.
- **Clean JSON output.** `--format Json` writes compact JSON through plain standard output, bypassing the Spectre.Console renderer, so `| jq` sees no ANSI escapes or markup.
- **No extra credentials.** The group reuses the App ID stored by `pks appinsights init` and the Azure AD identity established by `pks foundry init`.

## How it fits together

Each command builds a KQL string, asks `IAzureFoundryAuthService` for an access token scoped to `https://api.applicationinsights.io/.default`, and POSTs the query to the App Insights query endpoint for the configured App ID. Nothing is cached, nothing is streamed, and nothing is written back — every invocation is a single point-in-time query over a bounded time window.

That means two prerequisites, both owned by other command groups. `pks appinsights init` stores the App ID (plus resource name and subscription ID) in the global pks config. `pks foundry init` signs you in to Azure AD. `pks otel` itself has no `init` and no `login` subcommand.

- **At a glance:** `otel errors` / `otel traces` / `otel logs` are app-wide scans over a window you choose.
- **At a glance:** `otel spans` is a single-trace drill-down with a fixed 24-hour window.

## Commands

`errors` · `traces` · `logs` · `spans`

Four commands, no aliases and no hidden subcommands. Full flag detail lives on the [pks otel command reference](/tools/pks/otel/reference).

## Subcommand summary

| Command | Application Insights table | Scope |
|---|---|---|
| [`pks otel errors`](/tools/pks/otel/errors) | `exceptions` | App-wide or one operation ID, newest first. |
| [`pks otel traces`](/tools/pks/otel/traces) | `requests` | App-wide, optionally errors only. |
| [`pks otel logs`](/tools/pks/otel/logs) | `traces` | App-wide, filtered by minimum severity or trace ID. |
| [`pks otel spans`](/tools/pks/otel/spans) | `dependencies` | One operation ID, fixed 24-hour window. |

## Defaults

| Setting | Value |
|---|---|
| Time window (`errors`, `traces`, `logs`) | `1h` |
| Time window (`spans`) | `24h`, not configurable |
| Result cap | `20` rows |
| Output format | `Table` |

Nothing in this group reads an environment variable. The App ID and the Azure AD sign-in come from `pks appinsights init` and `pks foundry init`.

## Next steps

- [pks otel errors](/tools/pks/otel/errors) — find the exception that broke a deployed app
- [pks otel traces](/tools/pks/otel/traces) — spot slow or failing requests and grab an operation ID
- [pks otel logs](/tools/pks/otel/logs) — read application log lines around a known trace
- [pks otel spans](/tools/pks/otel/spans) — see the outbound-call waterfall for one request
- [pks otel command reference](/tools/pks/otel/reference) — every flag, default, and behavioral gap in one table
