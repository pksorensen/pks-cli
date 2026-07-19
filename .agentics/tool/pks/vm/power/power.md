---
title: "Start, stop, and destroy VMs"
description: "Run the power lifecycle of a tracked VM — resume it and wait for SSH, deallocate it while keeping the disk, or permanently delete it and its cloud resources."
tags: [how-to, vm, lifecycle, billing]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks vm start|stop [VM_NAME] · pks vm destroy"
examples:
  - command: "pks vm start my-vm"
    description: "Start a VM and wait until SSH answers"
  - command: "pks vm stop my-vm"
    description: "Deallocate a VM, keeping its disk"
  - command: "pks vm start"
    description: "Pick the VM to start interactively"
  - command: "pks vm destroy"
    description: "Permanently delete a VM after confirmation"
---

Three commands cover the money-relevant part of a VM's life: `pks vm start` resumes a machine and blocks until it is reachable, `pks vm stop` deallocates it while preserving the disk, and `pks vm destroy` deletes it and everything it owns in the cloud. Each one resolves the VM to its provider and passes through the second-factor guard before touching billing.

## Prerequisites

- **An authenticated provider.** Each command checks the VM's owning cloud and fails with a pointer to `pks azure init` or `pks scaleway init` if it is not signed in.
- **A tracked VM.** Provision one with [pks vm init](/tools/pks/vm/init), or adopt an existing machine's key with [pks vm add-ssh-key](/tools/pks/vm/ssh-keys).
- **A second factor, if enrolled.** `vm.start` and `vm.destroy` require one by default; `vm.stop` does not. Adjust with `pks actions`.

## start

```bash
pks vm start my-vm
```

Starting a stopped machine resumes compute and GPU billing, which is why `vm.start` requires a second factor by default. The command then waits for a public IP and for port 22 to answer, refreshes the SSH target registration, and prints a connection panel containing the `ssh` command and the matching `pks claude --ssh-target <name>` invocation.

| Argument | Required | Description |
|---|---|---|
| `VM_NAME` | no | The VM to start. An interactive picker appears when omitted, and is skipped when only one VM is tracked. |

The wait for a public IP times out after 5 minutes. On timeout the command exits with a non-zero status and suggests checking `pks vm list` — the machine may still be coming up.

> **Note.** The same reachability routine backs `pks vm tailscale`, so it can trigger the `vm.start` second-factor prompt too. `pks devcontainer spawn --ssh-target`'s auto-start is a separate, Azure-only code path — it does not go through this routine or the provider registry, so it will not auto-start a Scaleway-provisioned VM. It waits up to 3 minutes for SSH (instead of this routine's 5-minute IP wait) and shows its own `Start the VM?` confirmation before the `vm.start` gate.

## stop

```bash
pks vm stop my-vm
```

The machine is deallocated. Its disk and attached storage are preserved, so `pks vm start` brings it back with its state intact. The success message names both ways to resume it: `pks vm start <name>` and `pks vm tailscale <name>`.

| Argument | Required | Description |
|---|---|---|
| `VM_NAME` | no | The VM to stop. An interactive picker appears when omitted. |

`vm.stop` does not require a second factor by default, unlike start, create, and destroy.

## destroy

```bash
pks vm destroy
```

This permanently deletes the VM and the cloud resources it owns — the Azure resources created for it, or the Scaleway server. On success it also removes the local SSH target and the tracked metadata record. There is no undo.

`pks vm destroy` takes no VM name argument. It auto-picks when only one VM is tracked, otherwise it shows a selection prompt. Two gates stand in front of the deletion: a plain-text confirmation defaulting to no, and the `vm.destroy` second-factor action, which is required by default.

Removal of the SSH target is best-effort — a failure there is swallowed and the command still reports the destroy as successful. Removal of the metadata record is not: an exception there is not caught and would surface as a command failure even though the cloud resources are already gone.

## Where the prompts run

Power operations execute outside any progress spinner on purpose. The second-factor guard may need to read a one-time password from the terminal, and an interactive prompt cannot render inside a live display.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Red error naming a provider `init` command | The VM's cloud is not authenticated. Run the named command, then retry. |
| Start exits after five minutes | The public IP has not appeared yet. Check `pks vm list`; if the machine is running, retry the start. |
| A second-factor prompt appears on a command you expected to be silent | `vm.start` is gated by default and is reused by `vm tailscale`. Devcontainer auto-start (Azure only) gates the same action through its own code path. Review the policy with `pks actions`. |
| A VM disappears from `pks vm list` | Its cloud resource no longer exists, so pks pruned the record and its SSH target. Connection info for that name stops working. |

## Next steps

- [Inspect VMs with list and status](/tools/pks/vm/inspect) — check state before and after a power change
- [Auto-shutdown for Azure VMs](/tools/pks/vm/autoshutdown) — stop paying for idle machines automatically
- [Create a VM with pks vm init](/tools/pks/vm/init) — provision a replacement after a destroy
- [pks vm](/tools/pks/vm) — the full command group
