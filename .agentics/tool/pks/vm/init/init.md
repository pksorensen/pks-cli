---
title: "Create a VM with pks vm init"
description: "Provision an Azure VM, a Scaleway GPU instance, or a per-hour Hetzner Cloud server interactively — with the price shown first — generate its SSH key, and register it as a named pks SSH target ready for devcontainers."
tags: [how-to, vm, azure, scaleway, hetzner, provisioning]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks vm init [--idle-shutdown <MINUTES>] [--scheduled-shutdown <TIME>]"
examples:
  - command: "pks vm init"
    description: "Full interactive wizard on any of the three clouds"
  - command: "pks vm init --idle-shutdown 30"
    description: "Shut the new Azure VM down after 30 idle minutes"
  - command: "pks vm init --scheduled-shutdown 22:00"
    description: "Shut the new Azure VM down daily at 22:00 UTC"
---

Get a remote Linux box with Docker preinstalled and SSH access wired into pks in one wizard: pick a cloud, pick a size, confirm, wait. `pks vm init` provisions the machine, generates an ed25519 keypair, waits for SSH, and registers the VM as a named SSH target so `pks devcontainer spawn --ssh-target <name>` works right after.

The wizard is fully interactive. There is no non-interactive or scriptable path — every choice is a prompt.

## 1. Prerequisites

- **A cloud account.** Azure, Scaleway, or Hetzner Cloud. The wizard chains into `pks azure init`, `pks scaleway init`, or `pks hetzner init` automatically if that provider is not authenticated yet.
- **`ssh-keygen` on PATH.** The wizard uses it to generate the keypair for the new machine.
- **A second factor, if enrolled.** Creating a VM is gated by the `vm.create` action, which requires a time-based one-time password by default. Manage that policy with `pks actions`.

## 2. Start the wizard

```bash
pks vm init
```

The first prompt asks which cloud to provision on. If that provider is not authenticated, the wizard runs its `init` command inline and returns to the flow.

## 3. Answer the machine prompts

The prompts differ by cloud:

**Azure**

1. VM name.
2. Resource group.
3. Size — the wizard lists live SKUs with prices and lets you filter by RAM and vCPU.
4. OS disk size, between 30 and 4096 GB.

Azure always uses the pks cloud-init image, which installs Docker during first boot.

**Scaleway**

1. Instance name.
2. Zone.
3. Instance type — GPU types are listed first.
4. OS image.
5. Optional Tailscale join at boot, offered only if `pks tailscale init` has already been run.

**Hetzner Cloud**

1. Server name — a hostname: lowercase letters, digits, hyphens.
2. Location, from the live list, your default first.
3. Server type — only types orderable in that location, cheapest first, each labelled with its shape and `€/hr (€/mo cap)` net of VAT. The primary IPv4 is priced separately and added in the panel.
4. OS image — `ubuntu-24.04` is first, so Enter takes it.
5. Optional Tailscale join at boot, as on Scaleway.

Hetzner installs Docker through cloud-init, like Azure, so the box satisfies the devcontainer-host contract.

The confirmation panel on Hetzner carries a price block: server and IPv4 per hour, the monthly cap, what **one hour a day** costs if the server is deleted between runs, what it costs **powered off** (the same as running — Hetzner bills until delete), and what parking the disk costs as a snapshot or a volume instead.

## 4. Confirm and provision

The wizard prints a confirmation panel with every choice. Approving it triggers the `vm.create` second-factor gate, then provisioning starts.

If Azure rejects the request with a capacity or `SkuNotAvailable` error, the wizard offers to retry with a different size or location instead of discarding the whole session. Any other creation error is fatal.

## 5. Wait for the machine

Provisioning waits up to 5 minutes for SSH to answer. On Azure and Hetzner it then polls `cloud-init status` for up to 10 minutes while Docker installs. A timeout or an error status at this step is reported as a warning, not a failure — the VM still exists and is still registered.

On Hetzner every phase is timed and printed as a **boot timeline** — API accepted the create, status `running`, SSH port open, cloud-init done — followed by what that boot cost at the chosen hourly rate. That is the number to compare across server types when deciding what a per-hour runner should be.

## 6. Options

| Flag | Default | Description |
|---|---|---|
| `--idle-shutdown <MINUTES>` | `60` | Auto-shutdown after this many idle minutes. `0` disables it. Azure only. |
| `--scheduled-shutdown <TIME>` | — | Daily hard shutdown at this UTC time, formatted `HH:MM`. Azure only. |

Both flags are silently ignored on the Scaleway and Hetzner paths. The saved record stores an idle value of `0` and no scheduled-shutdown call is made. On Hetzner, remember that neither idle nor scheduled shutdown would save money anyway — only `pks vm destroy` stops the meter.

## 7. Verify

```bash
pks vm list
```

The new machine appears with its provider, power status, public IP, and size. Connect to confirm SSH:

```bash
pks ssh connect my-vm
```

## 8. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The wizard exits at the cloud prompt | The provider's `init` did not complete. Run `pks azure init`, `pks scaleway init`, or `pks hetzner init` on its own, then retry. |
| Hetzner: `uniqueness_error` on the SSH key | Handled: the key already exists in the project and its id is reused. If it still fails, the key in the project has a different comment but the same material — delete it in the Cloud Console. |
| Hetzner: `resource_unavailable` on create | That type is out of stock in that location right now. Rerun and pick another location — the type list only shows what Hetzner reported orderable at the time of the call. |
| Provisioning fails on capacity | Accept the retry offer and pick a different size or location. |
| The wizard crashes at the key-generation step | `ssh-keygen` is missing from PATH. The key-generation call is unguarded, so this throws and aborts the wizard before the VM is provisioned — install `ssh-keygen` and rerun `pks vm init`. |
| Cloud-init warning after Azure provisioning | Docker may still be installing. Check with `pks vm status`, which reports the Docker server version once it is up. |

## Next steps

- [Start, stop, and destroy VMs](/tools/pks/vm/power) — the power lifecycle after provisioning
- [Inspect VMs with list and status](/tools/pks/vm/inspect) — confirm the machine is healthy
- [Auto-shutdown for Azure VMs](/tools/pks/vm/autoshutdown) — change the idle and daily settings later
- [Join a VM to Tailscale](/tools/pks/vm/tailscale) — reach LAN devices from the new box
- [pks vm](/tools/pks/vm) — the full command group
