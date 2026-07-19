---
title: "Scheduling VM start and stop"
description: "Use the top-level pks schedule wizard to set daily auto-start, daily auto-shutdown, and idle shutdown on an Azure VM in one multi-select pass."
tags: [how-to, vm, azure, cost, schedule]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks schedule"
examples:
  - command: "pks schedule"
    description: "Interactive wizard for start, stop, and idle timing"
---

`pks schedule` is the interactive superset of [pks vm autoshutdown](/tools/pks/vm/autoshutdown): the same idle and daily shutdown controls, plus daily auto-start, applied to one Azure VM in a single confirmed batch. It belongs to the VM family conceptually but is registered as a top-level command.

> **Note.** The command is `pks schedule`, not `pks vm schedule`. Running it under the `vm` branch will not resolve.

## Prerequisites

- **An Azure VM.** The wizard calls Azure Resource Manager directly with no provider-capability check. Pointing it at a Scaleway machine fails inside the Azure calls rather than declining gracefully.
- **Azure authentication.** Run `pks azure init` first.
- **A reachable machine, for idle changes.** The idle option is applied over SSH against the on-box systemd service.

## 1. Run the wizard

```bash
pks schedule
```

There are no arguments and no flags. Everything is prompted, so the command cannot be used in a script or a non-interactive session.

## 2. Pick the VM

The first prompt selects one machine from the tracked list.

## 3. Multi-select what to change

The wizard offers any combination of:

- Auto-start at a fixed daily time
- Auto-shutdown at a fixed daily time
- Idle shutdown after a number of minutes
- The matching "Disable" option for each of the three

## 4. Fill in the values and confirm

Each selected option prompts for its value. A summary panel then shows everything you chose before anything is applied.

## 5. Apply

Confirming applies all selected changes in one batch — Azure schedule writes for the start and stop times, an SSH-based systemd change for the idle threshold — and reports any per-item errors at the end.

Partial application is possible. A failure in one selected item does not roll back the items that already succeeded; the failures surface as warnings and the command exits with a non-zero status even when others applied cleanly.

## 6. Verify

```bash
pks vm status
```

The panel shows the idle and scheduled shutdown configuration for the machine.

## 7. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Azure errors on every item | The selected machine is not an Azure VM, or the Azure context is wrong. Confirm the provider with `pks vm list`. |
| A warning about VM identifier resolution | Resolution failed and the schedule calls proceeded with an empty identifier, which may silently do nothing. Rerun after confirming the machine and resource group in `pks vm status`. |
| Some items applied, some did not | Per-item failures do not roll back successes. Rerun the wizard selecting only the failed items. |
| No second-factor prompt appeared | `pks schedule` is not covered by the action guard, unlike the other billing-affecting VM commands. |

## See also

- [Auto-shutdown for Azure VMs](/tools/pks/vm/autoshutdown) — the flag-driven, scriptable subset
- [Start, stop, and destroy VMs](/tools/pks/vm/power) — manual power control
- [Inspect VMs with list and status](/tools/pks/vm/inspect) — confirming applied settings
- [pks vm](/tools/pks/vm) — the full command group
