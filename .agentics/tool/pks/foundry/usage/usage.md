---
title: "Report Foundry resource cost"
description: "Show an Azure cost summary, a cost-over-time chart, and a top-meter breakdown scoped to the selected Azure AI Foundry resource."
tags: [how-to, azure, cost, reporting]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks foundry usage"
examples:
  - command: "pks foundry usage"
    description: "Pick a resource and window, then show cost tables"
---

See what one Azure AI Foundry resource is costing you. `pks foundry usage` queries Azure Cost Management scoped to the resource's Azure Resource Manager ID, then prints a summary table, a cost-over-time chart, and a top-15 breakdown by meter.

It shares its billing plumbing with `pks azure usage`; the difference is the scope, a single resource rather than a whole subscription.

## 1. Prerequisites

- **A completed `pks foundry init`.** The command exits when no credentials are stored.
- **Cost Management Reader rights, or equivalent, on the resource's subscription.** A working Foundry data-plane token does not imply billing access.
- **An interactive terminal.** Both the resource confirmation and the time window are prompts, with no flag equivalents.

## 2. Run the report

```bash
pks foundry usage
```

If a resource is already saved from `init` or `select`, the command asks whether to use it, defaulting to yes. Decline to pick a different subscription and resource for this run.

## 3. Pick a time window

A prompt offers the reporting window. There is no CLI flag for it, so this step cannot be scripted.

## 4. Read the output

Three sections print in order: a summary table of total cost for the window, a cost-over-time chart, and the top 15 meters by cost. The chart sizes itself to the terminal, honoring `COLUMNS` and `LINES` when they are set.

## 5. Verify

Cross-check the total against the Azure portal's Cost analysis view for the same resource and window. Cost Management data lags actual usage by hours, so a resource used minutes ago may report nothing yet.

## Options

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |

## Troubleshooting

**The command hangs in a script or CI job.** The resource confirmation and window selection are interactive prompts. Run it from a terminal.

**"Failed to query cost" with an authorization message.** The signed-in identity lacks Cost Management rights on the subscription. Grant Cost Management Reader and retry.

**Zero cost reported for a resource you are using.** Cost Management data is delayed, and a newly created resource may not be billable yet. Try a wider window.

**The report errors instead of showing partial data.** Any query failure aborts the command; it never renders half a table. Fix the underlying query failure and rerun.

**Wrong resource in the report.** You accepted the saved resource. Answer no at the confirmation prompt, or change the saved one with [pks foundry select](/tools/pks/foundry/select).

## Next steps

- [Inspect stored Foundry credentials](/tools/pks/foundry/status) — confirm which resource is saved
- [Change resource and model selection](/tools/pks/foundry/select) — report on a different resource by default
- [pks foundry reference](/tools/pks/foundry/reference) — the full command surface
