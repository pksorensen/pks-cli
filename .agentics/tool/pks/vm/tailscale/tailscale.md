---
title: "Join a VM to Tailscale"
description: "Install the Tailscale client on a cloud VM over SSH and join it to your tailnet, so the machine can reach NAS and LAN devices behind your home network."
tags: [how-to, vm, tailscale, networking]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks vm tailscale [VM_NAME]"
examples:
  - command: "pks vm tailscale"
    description: "Pick a VM and join it to your tailnet"
  - command: "pks vm tailscale my-vm"
    description: "Join a named VM without the picker"
---

A cloud VM cannot see your NAS or your LAN by default. `pks vm tailscale` starts the machine if needed, installs the Tailscale client over SSH, optionally enables IP forwarding, and runs `tailscale up` with the arguments from your stored configuration — leaving the box on your tailnet with both its public IP and its tailnet IP printed.

## Prerequisites

- **Tailscale configured.** The command chains into `pks tailscale init` if it is not, and aborts if that setup does not complete.
- **A usable local SSH key for the VM.** The command needs SSH to run the install and configuration steps. It fails with a pointer to [pks vm add-ssh-key](/tools/pks/vm/ssh-keys) when no key exists.
- **A second factor, if enrolled.** The command reuses the reachability routine behind `pks vm start`, so it inherits the `vm.start` gate when the machine needs starting.

## 1. Run the command

```bash
pks vm tailscale my-vm
```

| Argument | Required | Description |
|---|---|---|
| `VM_NAME` | no | The VM to join to the tailnet. An interactive picker appears when omitted. |

## 2. What happens on the machine

1. The VM is started if it is not running, and the command waits for SSH.
2. The Tailscale client is installed.
3. IP forwarding for IPv4 and IPv6 is enabled — but only when the stored configuration advertises an exit node or accepts subnet routes.
4. `tailscale up` runs with the arguments built from your `pks tailscale init` configuration.
5. The tailnet IP is read back and shown alongside the public IP.

Each remote step runs with its own timeout, between 20 seconds and 3 minutes. A step that times out aborts the whole command with a non-zero exit status. Failures in the IP-forwarding step are deliberately swallowed, so a sysctl hiccup does not block the join.

## 3. Approve an exit node, if you advertised one

When the configuration advertises an exit node, the command reminds you that it still needs manual approval in the Tailscale admin console, under Machines and then the route settings for that machine. Running `tailscale up` does not activate it on its own.

## 4. Verify

The command prints both connection paths when it finishes. Confirm the tailnet route by connecting to the machine on its tailnet IP rather than its public IP.

## 5. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The command chains into `pks tailscale init` and then stops | The Tailscale setup did not complete. Run `pks tailscale init` on its own and finish it, then retry. |
| Pointer to `pks vm add-ssh-key` | No usable local private key for the machine. Adopt the key first. |
| A remote step times out | The machine is slow or unreachable mid-run. Confirm with `pks vm status`, then rerun. |
| Exit node advertised but traffic does not route | Approve the route in the Tailscale admin console. |
| An unexpected second-factor prompt | The machine was stopped, so the `vm.start` gate applied. |

## See also

- [Start, stop, and destroy VMs](/tools/pks/vm/power) — the reachability routine this command reuses
- [Manage VM SSH keys](/tools/pks/vm/ssh-keys) — required before a Tailscale join
- [Create a VM with pks vm init](/tools/pks/vm/init) — Scaleway machines can join at boot instead
- [pks vm](/tools/pks/vm) — the full command group
