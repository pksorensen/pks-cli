---
title: "Auto-shutdown for Azure VMs"
description: "Configure idle-based and daily fixed-time shutdown for an Azure VM, or disable both, so a forgotten remote box stops charging you overnight."
tags: [how-to, vm, azure, cost]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks vm autoshutdown [VM_NAME] [--idle <MINUTES>] [--scheduled <TIME>] [--disable]"
examples:
  - command: "pks vm autoshutdown my-vm --idle 30"
    description: "Shut down after 30 minutes with no activity"
  - command: "pks vm autoshutdown my-vm --scheduled 22:00"
    description: "Hard shutdown daily at 22:00 UTC"
  - command: "pks vm autoshutdown my-vm --disable"
    description: "Turn off both idle and scheduled shutdown"
---

A remote VM you forget about is a bill you did not plan. `pks vm autoshutdown` sets two independent safety nets on an Azure machine: an on-box idle monitor that powers it off after a period of inactivity, and a daily fixed-time shutdown enforced through Azure Resource Manager. It prints the updated settings table when it finishes.

## Prerequisites

- **An Azure VM.** Scaleway instances do not support scheduled shutdown. The command prints a yellow notice and exits successfully without changing anything, suggesting you stop the machine manually from `pks vm status`.
- **Azure authentication.** The command fetches an Azure management token and fails with a pointer to `pks azure init` if it cannot.
- **A reachable machine, for `--idle`.** Changing the idle threshold requires SSH — the command patches the on-box `pks-idle-monitor` systemd service and restarts it.

## 1. Pick the machine

```bash
pks vm autoshutdown my-vm --idle 30
```

`VM_NAME` must match the tracked VM's name (case-insensitive). Omit it and an interactive selection prompt appears.

## 2. Choose the flags

| Flag | Description |
|---|---|
| `--idle <MINUTES>` | Idle shutdown threshold in minutes. `0` disables the idle monitor. |
| `--scheduled <TIME>` | Daily shutdown time in UTC, formatted `HH:MM`, validated against a two-digit pattern. |
| `--disable` | Disables all auto-shutdown — both the idle monitor and the Azure schedule. |

The write is gated by the `vm.autoshutdown.write` action, which does not require a second factor by default.

## 3. How each flag behaves

**`--idle`** reconfigures `/usr/local/bin/pks-idle-monitor` over SSH and restarts the service, or stops and disables it when set to `0`. It only takes effect when the tracked record already has both a public IP address and an SSH key path. When either is missing the remote change is skipped while the recorded value is still saved, so the table can show a threshold the machine is not enforcing.

**`--scheduled`** sets a daily Azure shutdown schedule. If the VM identifier lookup fails, that is reported as a warning and the schedule call is attempted anyway.

**`--disable`** clears both. If the Azure disable call fails it logs a warning and still stops the local idle monitor and zeroes the record, so local state can read as disabled while the cloud-side schedule survives.

> **Note.** `--disable` short-circuits the command: it clears both the idle monitor and the Azure schedule and returns immediately, before any co-supplied `--scheduled` or `--idle` value is even looked at. `--disable --idle 30` still ends with auto-shutdown fully disabled — the `--idle 30` is silently ignored, not applied afterward. Use one intent per invocation.

## 4. Verify

```bash
pks vm status
```

The panel reports the idle and scheduled shutdown configuration for the machine. For the idle monitor specifically, confirm the machine was reachable when you made the change — a skipped SSH patch is the one case where the recorded value and the enforced value diverge.

## 5. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Yellow "not supported yet" notice | The VM is on Scaleway. Stop it manually from `pks vm status`. |
| "Run pks azure init first" | No Azure management token is available. The token is fetched unconditionally after the provider-support check. |
| The idle threshold shows in the table but the machine never stops | The record was missing a public IP or SSH key path, so the remote patch was skipped. Start the machine, confirm SSH works, then rerun the same `--idle` command. |
| A daily shutdown still fires after `--disable` | The Azure disable call failed while the local state was cleared. Rerun `--disable` on a reachable, authenticated session. |

## See also

- [Scheduling VM start and stop](/tools/pks/vm/schedule) — the interactive superset that also schedules auto-start
- [Start, stop, and destroy VMs](/tools/pks/vm/power) — stopping a machine by hand
- [Inspect VMs with list and status](/tools/pks/vm/inspect) — where the effective settings are shown
- [pks vm](/tools/pks/vm) — the full command group
