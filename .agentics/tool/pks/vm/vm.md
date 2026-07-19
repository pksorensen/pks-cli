---
title: "pks vm"
description: "Provision, connect to, monitor, schedule, and destroy Azure or Scaleway VMs used as remote devcontainer and GPU hosts, with SSH keys and 2FA handled."
tags: [reference, vm, azure, scaleway, ssh]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks vm <command> [options]"
examples:
  - command: "pks vm init"
    description: "Provision a new Azure or Scaleway VM interactively"
  - command: "pks vm list"
    description: "List every tracked VM with live power status"
  - command: "pks vm status"
    description: "Inspect one VM and act on it from a menu"
  - command: "pks vm start my-vm"
    description: "Start a stopped VM and wait for SSH"
  - command: "pks vm stop my-vm"
    description: "Deallocate a VM but keep its disk"
  - command: "pks vm destroy"
    description: "Permanently delete a VM and its cloud resources"
---

`pks vm` is the command group for cloud machines you rent, use, and give back — Azure VMs and Scaleway GPU instances that act as remote hosts for devcontainers, Claude sessions, and workloads too heavy for a laptop. It provisions the box, generates and stores the SSH key, registers the machine as a named pks SSH target, and tracks it so every later command can find it by name.

## Overview

`pks vm init` walks an interactive wizard: pick a cloud, authenticate, name the machine, choose size and region, and confirm. It generates an ed25519 keypair under `~/.pks-cli/keys/`, provisions the machine, waits for port 22, and registers an SSH target so `pks ssh connect <name>` and `pks claude --ssh-target <name>` work immediately. Every other subcommand operates on that tracked list, merged with live discovery from the provider.

- **Two providers, one interface.** Azure VMs (Docker preinstalled via cloud-init) and Scaleway GPU instances resolve to their own provider behind the same commands.
- **SSH is a first-class output.** Key generation, target registration, and connection panels are part of provisioning, not a follow-up chore.
- **Billing-affecting actions are gated.** Create, destroy, and start pass through the second-factor guard configured by `pks actions`.

## What you get

- **Interactive provisioning.** `pks vm init` lists live Azure SKUs with prices and lets you filter by RAM and vCPU, or lists Scaleway GPU types first when you pick that cloud.
- **Named SSH targets.** Each VM becomes an SSH target, so downstream commands address it by name rather than by IP.
- **Power management that waits.** `pks vm start` blocks until the machine has a public IP and port 22 answers, then prints the connection panel.
- **Live fleet visibility.** `pks vm list` merges local metadata with cloud discovery, probes power state and disk usage concurrently, and prunes records whose cloud resource has vanished.
- **Cost control.** Idle-based and daily scheduled shutdown for Azure VMs, configurable per machine with `pks vm autoshutdown` or the broader `pks schedule` wizard.
- **Key portability.** `pks vm add-ssh-key` adopts a machine pks did not create; `pks vm export-ssh-key` hands access to another machine or agent.

## How it fits together

`pks vm` manages the machine, not what runs on it. Provision with `pks vm init`, then launch work on the box with `pks devcontainer spawn --ssh-target <name>` or `pks claude --ssh-target <name>`. Generic SSH-target management lives in `pks ssh` once the VM is registered.

Underneath, a provider registry resolves each tracked VM to Azure or Scaleway and merges local metadata with live discovery, so a machine started or stopped in the cloud console still shows the right state. Power and provisioning calls are wrapped by the action guard, which may ask for a time-based one-time password before it proceeds.

- **Tracked metadata** lives in `~/.pks-cli/vms.json`; SSH keys live in `~/.pks-cli/keys/<name>`.
- **Live truth** comes from the provider on every list, status, and power call.

## Commands

`init` · `list` · `status` · `start` · `stop` · `destroy` · `autoshutdown` · `add-ssh-key` · `export-ssh-key` · `tailscale`

| Command | What it does |
|---|---|
| [`pks vm init`](/tools/pks/vm/init) | Provisions a new Azure or Scaleway VM and registers it as an SSH target. |
| `pks vm list` | Lists every tracked VM with live power state, disk, IP, and size. |
| `pks vm status` | Detailed panel for one VM plus an action menu. |
| `pks vm start` | Starts a VM and waits until SSH answers. |
| `pks vm stop` | Deallocates a VM, preserving its disk. |
| `pks vm destroy` | Permanently deletes a VM and its cloud resources. |
| [`pks vm autoshutdown`](/tools/pks/vm/autoshutdown) | Configures idle and daily shutdown for an Azure VM. |
| `pks vm add-ssh-key` | Registers a private key for a VM pks did not provision. |
| `pks vm export-ssh-key` | Prints a snippet installing a VM's key on another machine. |
| [`pks vm tailscale`](/tools/pks/vm/tailscale) | Joins a VM to your Tailscale tailnet over SSH. |

> **Note.** `pks schedule` is a separate top-level command, not `pks vm schedule`. It is the interactive superset of `pks vm autoshutdown` and adds auto-start scheduling. See [Scheduling VM start and stop](/tools/pks/vm/schedule).

## Defaults

| Setting | Value |
|---|---|
| Idle shutdown on a new Azure VM | `60` minutes |
| SSH key location | `~/.pks-cli/keys/<vm-name>` |
| VM metadata store | `~/.pks-cli/vms.json` |
| Wait for public IP after start | 5 minutes |
| Cloud-init and Docker wait on Azure | up to 10 minutes |
| Second factor required by default | `vm.create`, `vm.destroy`, `vm.start` |
| Second factor not required by default | `vm.stop`, `vm.autoshutdown.write` |

Second-factor requirements are per-user toggleable with `pks actions`. Scaleway VMs do not support scheduled shutdown; those commands decline with a notice and exit successfully.

## Next steps

- [Create a VM with pks vm init](/tools/pks/vm/init) — the provisioning wizard, both clouds, end to end
- [Start, stop, and destroy VMs](/tools/pks/vm/power) — the power lifecycle and the second-factor gates
- [Inspect VMs with list and status](/tools/pks/vm/inspect) — fleet view, live stats, and the action menu
- [Auto-shutdown for Azure VMs](/tools/pks/vm/autoshutdown) — idle and daily shutdown flags
- [Scheduling VM start and stop](/tools/pks/vm/schedule) — the top-level `pks schedule` wizard
- [Manage VM SSH keys](/tools/pks/vm/ssh-keys) — adopt and export private keys
- [Join a VM to Tailscale](/tools/pks/vm/tailscale) — reach LAN and NAS devices from the cloud
