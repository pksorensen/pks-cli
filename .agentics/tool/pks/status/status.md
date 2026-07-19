---
title: "pks status"
description: "Render a mocked system-status dashboard with Spectre.Console — a demo of the CLI's grid and live-panel rendering, not a real infrastructure check."
tags: [reference, cli, status]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks status [options]"
examples:
  - command: "pks status"
    description: "Show the mocked dashboard for all three fake environments"
  - command: "pks status --environment prod"
    description: "Restrict the environment grid to the PROD row only"
  - command: "pks status --ai-insights"
    description: "Also print the static AI insights panel"
  - command: "pks status --watch"
    description: "Enter a live-updating panel with fabricated metrics"
  - command: "pks status -e staging --ai-insights"
    description: "Combine a single-environment view with the AI insights panel"
---

`pks status` is a single leaf command registered directly under the root `pks` command — it renders a system-status dashboard in the terminal using Spectre.Console. It has no subcommands.

## Overview

`pks status` prints a colored per-environment health grid (PROD, STAGING, DEV) followed by a fixed five-row services table (API Gateway, Database, Cache Redis, AI Service, Analytics). With `--watch` it switches to a live-refreshing panel of CPU, memory, network, request, error, and latency numbers.

> **Note.** Every value this command prints is hardcoded or randomly generated inside the command class. It does not call any real API, read any config file, or check any actual pks-managed resource — no Coolify deployment, no runner, no registry. Treat it as a rendering demo, not a diagnostic tool.

- **Health grid:** always reports `12/12 running` and healthy for every environment, regardless of what actually exists.
- **Services table:** five fixed rows with fabricated uptime, version, and health-percentage figures.
- **Watch mode:** fabricates CPU/memory/network/request/error/latency numbers with `System.Random` every two seconds.
- **AI insights:** static prose bullets, not derived from any telemetry.

## When to use it

Do not use `pks status` to check the real state of any pks-managed system — it will not tell you anything true. Its only legitimate uses are previewing the Spectre.Console dashboard layout (grid, table, and live-panel rendering) or sanity-checking that the CLI's rendering pipeline works.

For real status of an actual subsystem, use the subsystem-specific command instead:

- [pks github status](/tools/pks/github) — real GitHub authentication and repository status.
- [pks coolify status](/tools/pks/coolify) — real Coolify deployment status.
- [pks registry status](/tools/pks/registry) — real container/NuGet registry status.
- [pks vm status](/tools/pks/vm) — real VM state.
- [pks authenticator status](/tools/pks/authenticator) — real two-factor enrollment status.

## Prerequisites

None. `pks status` makes no network calls and reads no credential or config store — it runs identically with no prior setup.

## Synopsis

```text
pks status [options]
```

### Options

| Flag | Default | Description |
|---|---|---|
| `-w`, `--watch` | `false` | Enter a live-updating panel instead of the static dashboard. |
| `-e`, `--environment <ENV>` | `all` | Environment to show in the health grid: `dev`, `staging`, `prod`, or `all`. |
| `--ai-insights` | `false` | Also print the static "AI-Powered Insights & Recommendations" panel. |

## Examples

```bash
pks status
```

Prints the health grid for all three fake environments plus the fixed services table.

```bash
pks status --environment prod
```

Restricts the health grid to the PROD row. The services table below it is unaffected and still prints all five rows.

```bash
pks status --ai-insights
```

Appends the static AI insights panel to the default dashboard.

```bash
pks status --watch
```

Switches to a live panel that refreshes fabricated CPU, memory, network, request, error, and latency numbers every two seconds. There is no exit or iteration-count flag — stop it with Ctrl+C.

## Troubleshooting

### `--environment PROD` renders an empty grid

`--environment` is matched with exact lowercase equality against `dev`, `staging`, `prod`, and `all`. The header line uppercases whatever string you passed for display, but the grid-row lookup does not — passing `PROD` (or any other casing) prints a `PROD` header over an empty grid, with no error message. Pass the value in lowercase.

### `--watch` never stops on its own

Watch mode runs an unconditional loop with a two-second sleep between ticks and no built-in exit condition. There is no `--count` or `--duration` option. Stop it with Ctrl+C.

### The numbers don't match reality

They are not supposed to. Every figure on this dashboard — health percentages, uptimes, versions, and the watch-mode metrics — is hardcoded or randomly generated in `StatusCommand`. Use one of the subsystem-specific `status` commands listed in [When to use it](#when-to-use-it) for a real check.

## See also

- [pks](/tools/pks) — command families and the full 57-command surface.
- [pks github runner](/tools/pks/github/runner) — real GitHub Actions runner registration and status.
- [pks agentics runner](/tools/pks/agentics/runner) — real Agentics runner status.
