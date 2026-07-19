---
title: "pks schedule"
description: "Interactive wizard that sets daily auto-start, daily auto-shutdown, and idle-shutdown timing on a tracked Azure VM in a single confirmed batch."
tags: [reference, vm, azure, schedule]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks schedule"
examples:
  - command: "pks schedule"
    description: "Launch the VM picker, then the start/stop/idle wizard"
---

`pks schedule` is a fully interactive, menu-driven command that configures how a tracked Azure VM starts, stops, and shuts itself down when idle. It walks you through picking a VM, choosing any combination of auto-start, auto-shutdown, and idle-shutdown actions, filling in the values, and applying everything in one confirmed batch. It is registered as a top-level command, not a subcommand of [pks vm](/tools/pks/vm), even though it operates on the same tracked VM records.

## Overview

Use `pks schedule` to turn a provisioned VM into a cost-controlled box: it boots itself at a fixed time each morning, stops at a fixed time each night, and optionally self-terminates after a period of inactivity — without hand-editing Azure DevTestLab schedule resources or the on-box idle-monitor service. It covers the same ground as [pks vm autoshutdown](/tools/pks/vm/autoshutdown) plus daily auto-start, trading that command's scriptable flags for a single interactive pass over all three settings at once.

- **Auto-start and auto-shutdown** are implemented as `Microsoft.DevTestLab/schedules` Azure resources — the same mechanism behind the Azure Portal's Auto-shutdown blade.
- **Idle shutdown** is not an Azure resource. It patches a systemd unit (`pks-idle-monitor`) over SSH on the VM itself.
- Each of the three settings can also be disabled independently from the same menu.

## Prerequisites

- **A VM already tracked by `pks vm init`.** With zero tracked VMs the command prints `No VMs tracked. Run 'pks vm init' first.` and exits before showing any prompt.
- **An Azure management token.** The command calls `IAzureAuthService.GetAccessTokenAsync` for the `https://management.azure.com/.default` scope. If no token can be minted it prints `Failed to get Azure token. Run 'pks azure init' first.` and exits — run [pks azure init](/tools/pks/azure) first even though the command never prompts for it.
- **A public IP and SSH key on the record, for idle-shutdown only.** The idle actions SSH into the VM as `azureuser` using the key path stored for that VM. A stopped VM with no current public IP, or a VM never given a key via `pks vm add-ssh-key`, silently skips the idle step.

## Synopsis

```text
pks schedule
```

The command takes no arguments and no flags — `VmSettings` is an empty settings class. Every input is gathered through Spectre.Console prompts at runtime, so it cannot be scripted or piped.

## How it works

1. **Pick the VM.** A selection prompt lists every tracked VM.
2. **Multi-select actions.** Choose any combination of six options: auto-start, auto-shutdown, idle shutdown, and a matching disable option for each. Selecting nothing prints `Nothing selected.` and exits with no changes.
3. **Fill in values.** Each selected action prompts for its value — a `HH:MM` time for start/stop, a minute count for idle.
4. **Review the summary.** A panel lists every pending change.
5. **Confirm and apply.** A `Confirm` prompt gates the apply step. Declining exits with nothing changed. Confirming applies every selected action in one pass and prints a per-action warning for anything that failed.

> **Note.** The command is `pks schedule`, not `pks vm schedule` — it is not nested under the `vm` branch.

## What each action does

### Auto-start and auto-shutdown times

Validated client-side only against the pattern `^\d{2}:\d{2}$` — a format check, not a range check — and always interpreted as a **UTC** daily recurrence. There is no timezone option.

Both are written as `Microsoft.DevTestLab/schedules` ARM resources (`autostart-computevm-<vmName>` / `shutdown-computevm-<vmName>`, api-version `2018-09-15`). Resolving the VM's ARM resource ID first is best-effort: if that lookup fails, the command prints a warning and continues with an empty resource ID, which then tends to make the schedule write fail server-side — surfaced only as a later warning, not a hard stop.

- **Disabling auto-start** issues an ARM `DELETE` on the autostart schedule. A `404` is treated as success.
- **Disabling auto-shutdown** instead does a `GET` then a `PUT` with `status=Disabled`. If the `GET` itself fails, the `PUT` defaults to `location=eastus` and `dailyTime=2200` — disabling a shutdown schedule whose `GET` errors out can silently recreate it with the wrong location and time.

### Idle shutdown

Prompts for a threshold in minutes, defaulting to `60`, with no upper bound and only `n > 0` enforced.

Setting or disabling idle shutdown is not an ARM call. The command opens `ssh -i <SshKeyPath> -o StrictHostKeyChecking=no azureuser@<PublicIpAddress>` and either edits `IDLE_THRESHOLD_MINUTES=` in `/usr/local/bin/pks-idle-monitor` and restarts the `pks-idle-monitor` systemd unit, or stops and disables that unit. That script and unit only exist on VMs provisioned by `pks vm init`'s cloud-init — running the idle option against an older or differently provisioned VM connects over SSH successfully but the remote `sed`/`systemctl` calls fail with nothing surfaced to you.

## Behavior notes

- **Partial application is normal.** A failure in one selected action does not roll back actions that already succeeded. Failures print as warnings; the command exits non-zero only when the failure came from an ARM call.
- **Idle-shutdown failures never surface.** The SSH call is wrapped in a bare try/catch with a 30-second wait. A bad key, an unreachable host, a non-zero remote exit code, or a timeout are all swallowed — the command still prints `Schedule updated successfully.` because idle errors never populate the list that gates that message.
- **Local metadata is written unconditionally.** `pks vm status` and `pks vm list` update and persist the VM's `ScheduledShutdownUtc` and `IdleShutdownMinutes` based on which options were selected, not on whether the underlying Azure or SSH call actually succeeded. Local state can read as configured while nothing changed on the machine.
- **The `Microsoft.DevTestLab` resource provider is not checked.** If it is unregistered on the subscription, the schedule write fails and is reported the same as any other ARM error.

## Verify

```bash
pks vm status
```

The status panel reports the idle and scheduled shutdown values currently recorded for the VM. Because local state is written regardless of remote success, treat a value shown here as unconfirmed until you separately check the VM boots, stops, or idles out on schedule.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `No VMs tracked. Run 'pks vm init' first.` | No VM has been provisioned or imported yet. Run [pks vm init](/tools/pks/vm) first. |
| `Failed to get Azure token. Run 'pks azure init' first.` | No usable Azure credential. Run [pks azure init](/tools/pks/azure). |
| `Nothing selected.` and no changes applied | The multi-select was submitted with no options chosen. Rerun and pick at least one action. |
| A yellow warning about resolving the VM ID, then the schedule write fails | The ARM resource-ID lookup failed and the schedule call proceeded with an empty ID. Confirm the VM name and resource group with `pks vm status`, then rerun. |
| Command reports success but the VM never idles out | The tracked record is missing a public IP or SSH key path, or the SSH call itself failed silently. Confirm the VM is reachable and has an SSH key via `pks vm status`, then rerun the idle option. |
| Disabling auto-shutdown re-creates it at 22:00 in `eastus` | The `GET` before the disable `PUT` failed, so the disable call fell back to those defaults instead of disabling. Rerun once the VM's schedule resource is reachable. |
| No second-factor prompt appears before applying | `pks schedule` is not currently covered by the action guard that gates other billing-affecting VM commands. |

## See also

- [pks vm](/tools/pks/vm) — the command group that owns the tracked VM records this wizard operates on
- [Auto-shutdown for Azure VMs](/tools/pks/vm/autoshutdown) — the flag-driven, scriptable subset of idle and scheduled shutdown
- [Start, stop, and destroy VMs](/tools/pks/vm/power) — manual power control outside a schedule
- [Inspect VMs with list and status](/tools/pks/vm/inspect) — where applied schedule values are shown
- [pks azure](/tools/pks/azure) — the authentication this command depends on
