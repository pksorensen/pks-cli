---
title: "Inspect VMs with list and status"
description: "See every tracked VM with live power state, disk, and IP, then drill into one machine for remote stats and an action menu that can reconnect, prune, or stop it."
tags: [how-to, vm, monitoring, ssh]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks vm list · pks vm status"
examples:
  - command: "pks vm list"
    description: "Table of every tracked VM with live status"
  - command: "pks vm status"
    description: "Detailed panel plus an action menu for one VM"
---

Two commands answer the questions you ask most about a fleet of remote boxes: which machines exist and what are they costing, and what is happening inside one of them right now. `pks vm list` gives the table; `pks vm status` gives the deep view plus a menu of actions you can run without leaving it.

## Prerequisites

- **At least one tracked VM.** Provision with [pks vm init](/tools/pks/vm/init).
- **An authenticated provider** for live discovery and power state.
- **A local SSH key** for any machine whose disk usage and remote stats you want. Without one the table shows a dash for disk.

## list

```bash
pks vm list
```

The table merges locally tracked metadata with live cloud discovery across Azure and Scaleway, and shows for each machine: name, provider, live power status, disk percentage, public IP, size or instance type, location, idle-shutdown setting, and creation date. Live status checks run concurrently, and disk usage is fetched over SSH for machines that are running.

`pks vm list` takes no arguments or options. When any VM exists it ends with an interactive prompt offering to inspect one, which hands off into the same view as `pks vm status`. That prompt makes it unsuitable for scripting.

Two behaviors worth knowing:

- **Disk probes are best-effort.** The SSH probe times out after 10 seconds per machine and renders a dash on failure, without delaying the table.
- **Vanished machines are pruned.** A locally tracked VM that live discovery can no longer find is removed, along with its SSH target, and the removal is logged as a yellow warning. Machines discovered only from the provider are never pruned this way, so pks does not clean up VMs it never owned metadata for.

## status

```bash
pks vm status
```

`pks vm status` takes no VM name argument. It auto-picks when a single VM is tracked, otherwise it shows a selection prompt.

The panel covers provider, resource group or zone, public IP, size, disk, idle and scheduled shutdown configuration, and live power state. If the machine is running, a single batched SSH command fetches a stats table: disk usage, memory, Docker container and image counts with the Docker server version, and uptime.

### The action menu

After the panel, a context-sensitive menu offers the actions that make sense for the machine's current state.

| Action | What it does |
|---|---|
| Reconnect (ssh) | Launches an interactive foreground `ssh` session and blocks until it exits, returning that session's exit code as the command's own. |
| Free disk space | Runs `docker system prune -af --volumes` on the remote machine. Offered when disk usage exceeds 70%. A three-minute timeout applies. |
| Start VM | Same guarded start as `pks vm start`. |
| Stop VM | Same guarded stop as `pks vm stop`. |
| Destroy VM | Same guarded destroy as `pks vm destroy`, including its own confirmation. |
| Quit | Leaves without acting. |

> **Do not commit.** The prune action deletes every unused Docker image, container, network, and volume on the machine, not only the ones pks created. Anything you cannot rebuild should be off the box before you run it.

Power actions from this menu run outside any spinner and carry the same second-factor gates as their standalone commands. Destroy still asks its own confirmation, defaulting to no, even though you navigated a menu to reach it.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Disk column shows a dash | The SSH probe failed or timed out. Check that a local key exists with [pks vm export-ssh-key](/tools/pks/vm/ssh-keys), and that the machine is running. |
| No remote stats table under the panel | The machine is not running, or SSH is unreachable. Start it with `pks vm start`. |
| A machine silently disappeared between runs | Its cloud resource no longer exists and pks pruned the record and SSH target. |
| You need to target a specific VM in a script | Neither command accepts a VM name. Use `pks vm start` or `pks vm stop` with a name argument for scripted work. |

## Next steps

- [Start, stop, and destroy VMs](/tools/pks/vm/power) — the standalone power commands behind the menu
- [Auto-shutdown for Azure VMs](/tools/pks/vm/autoshutdown) — avoid needing to stop machines by hand
- [Manage VM SSH keys](/tools/pks/vm/ssh-keys) — fix a machine whose stats will not load
- [pks vm](/tools/pks/vm) — the full command group
