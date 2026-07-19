---
title: "pks firecracker"
description: "Bootstrap a Linux host, smoke-test a boot, and run a daemon that executes ALP assembly-line jobs inside isolated Firecracker microVMs."
tags: [reference, cli, firecracker, microvm]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks firecracker <command> [options]"
examples:
  - command: "pks firecracker init"
    description: "Bootstrap the host: kernel, rootfs image, SSH keypair"
  - command: "pks firecracker test"
    description: "Boot a throwaway VM and run the smoke-test checks"
  - command: "pks firecracker test --keep-vm"
    description: "Boot a test VM and leave it running for SSH debugging"
  - command: "pks firecracker runner start --project owner/project"
    description: "Auto-register and start polling for jobs"
  - command: "pks firecracker runner start --server agentics.dk"
    description: "Start polling using an existing local registration"
---

`pks firecracker` runs a self-hosted job runner backed by hardware-virtualized microVMs instead of Docker containers. It initializes a Linux host with a guest kernel and rootfs image, smoke-tests a full VM boot, and runs a long-lived daemon that polls agentics.dk for `alp_runner_spawn` jobs and executes each one inside a freshly booted, network-isolated microVM before tearing it down.

## Overview

This is the Assembly Line Platform (ALP) equivalent of a self-hosted CI runner, but using Firecracker microVMs for stronger isolation between untrusted job commands than a container gives you. Each job gets its own VM, its own `/30` network slot, and is destroyed afterward — nothing persists between jobs.

- **Use it instead of** [`pks github runner`](/tools/pks/github) or [`pks agentics runner`](/tools/pks/agentics) when a project's assembly-line jobs run arbitrary or untrusted commands and need VM-grade isolation, not container-level isolation.
- **Requires** a Linux host with KVM exposed — bare metal, or a VM with nested virtualization enabled. It shells out directly to the `firecracker` binary and to `/dev/kvm`.
- **Not usable** on macOS or Windows, and not usable on a VM without nested virtualization.

## Prerequisites

- **A Linux host with `/dev/kvm` writable.** Checked by both `init` and `test`; a non-writable or missing device fails immediately with a clean prerequisite error.
- **The `firecracker` binary on `PATH`.** Also checked by `init` and `test` (`firecracker --version`).
- **Docker installed and running.** `init`'s rootfs build shells out to `docker build`, `docker create`, and `docker export`. This is **not** checked as a prerequisite — a missing or stopped Docker daemon surfaces as a raw docker-build error partway through `init`, not a clean message.
- **Root privileges for `runner start`.** Networking setup shells out to `ip tuntap add`, `ip addr add`, `ip link set up`, and `iptables -A FORWARD` to create a tap device and NAT rule per VM. There is no explicit privilege check — a non-root user sees a raw permission-denied error.

## How it fits together

Run `pks firecracker init` once per host to build the kernel, rootfs, and SSH keypair. Run `pks firecracker test` to confirm the whole pipeline boots and answers over SSH before trusting it with real jobs. Run `pks firecracker runner start --project owner/project` to register the host against an agentics.dk project and start the foreground polling daemon.

Each job the daemon claims gets a tap device (`tap-fc-{slot}`, MAC `AA:FC:00:00:HH:LL`) carved out of a `/30` inside the `172.16.0.0/16` range (hardcoded — the `--subnet` setting is currently ignored by the address computation, see Troubleshooting), a fresh copy of the base rootfs, and a VM sized from the job or the host's configured defaults. The job command runs over SSH with a 1-hour timeout; the VM, network slot, and rootfs copy are torn down in a `finally` block regardless of outcome.

- **Registration** happens automatically on `runner start --project <owner>/<project>` if `~/.pks-cli/firecracker-runners.json` has no matching entry yet.
- **The poll loop is foreground-only.** There is no background/daemonize mode — stop it with Ctrl-C or a process signal.

> **Note.** Two commands print help text pointing at subcommands that do not exist in this build: `test --keep-vm` suggests running `pks firecracker cleanup <vmId>`, and `runner start` without a registration suggests `pks firecracker runner register <owner/project>`. Neither is registered. Use `--project owner/project` on `runner start` to auto-register, and clean up a kept-alive test VM manually (see [Troubleshooting](#troubleshooting)).

## Commands

`init` · `test` · `runner start`

| Command | What it does |
|---|---|
| `pks firecracker init` | One-time host bootstrap: kernel, rootfs image, SSH keypair. |
| `pks firecracker test` | Boots a throwaway VM and runs six smoke checks. |
| `pks firecracker runner start` | Foreground daemon that polls for and executes ALP jobs. |

## init

```text
pks firecracker init [options]
```

One-time host bootstrap. Verifies `/dev/kvm` and the `firecracker` binary are present, generates an ed25519 SSH keypair for reaching guest VMs, downloads the guest Linux kernel, builds a rootfs ext4 image from a Dockerfile (`docker build` → `docker create` → `dd`/`mkfs.ext4` → mount → `docker export` | tar extract → unmount), and saves the resulting paths and defaults to `~/.pks-cli/firecracker-runners.json`. Run this once per host before `test` or `runner start` will work.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |
| `--work-dir <PATH>` | `~/.pks-cli/firecracker` | Working directory for Firecracker files. |
| `--vcpus <COUNT>` | `2` | Default vCPU count for VMs. |
| `--mem-mib <SIZE>` | `2048` | Default memory in MiB for VMs. |
| `--subnet <CIDR>` | `172.16.0.0/16` | Network subnet for VMs. Saved and displayed but currently has no effect on the addresses actually allocated — see below. |
| `--skip-rootfs` | `false` | Skip building the rootfs image. |
| `--skip-kernel` | `false` | Skip downloading the kernel. |
| `--dockerfile <PATH>` | bundled `Dockerfile.rootfs` | Path to a custom Dockerfile for the rootfs. |

Any option not passed is filled in interactively.

```bash
pks firecracker init
```

Runs interactively, prompting for work directory, vCPUs, memory, and subnet where not passed as flags.

```bash
pks firecracker init --vcpus 4 --mem-mib 4096
```

Sets non-default VM sizing defaults for every VM this host boots.

The SSH key, kernel, and rootfs are each skipped if the corresponding file already exists at its resolved path — re-running `init` is idempotent unless you delete those files first. `--skip-kernel`/`--skip-rootfs` are for explicitly opting out of a step, not for forcing a rebuild. The bundled `Dockerfile.rootfs` is located by searching, in order, `./firecracker/`, the executing binary's own directory, then `/usr/share/pks-cli/firecracker/`; if none exist, `init` fails listing all three candidates — pass `--dockerfile` explicitly if your layout doesn't match one of them. The guest kernel is always downloaded from a single hardcoded Firecracker CI URL (`vmlinux-5.10.217`); there is no version or URL override.

- `--subnet` is saved to `~/.pks-cli/firecracker-runners.json` and echoed back in the settings table, but the address-computation logic (`FirecrackerNetworkManager.ComputeAddresses`) hardcodes the `172.16.x.x` octets regardless of this setting — changing `--subnet` away from the default does not change the tap device, VM, or gateway addresses actually allocated.

## test

```text
pks firecracker test [options]
```

Boots a throwaway Firecracker microVM end to end — network allocation, rootfs copy, boot, SSH wait, then six smoke checks (kernel version, outbound ping, DNS resolution, `docker --version`, disk space, memory) — and prints a pass/fail table. Run this right after `init` to confirm the whole pipeline works on this host before pointing a real runner daemon at it, or any time after a host change.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |
| `--vcpus <COUNT>` | from saved config | Override vCPU count for the test VM. |
| `--mem-mib <SIZE>` | from saved config | Override memory for the test VM. |
| `--keep-vm` | `false` | Don't clean up the VM after testing, for debugging. |

```bash
pks firecracker test
```

Runs the full smoke-test sequence and cleans up afterward.

```bash
pks firecracker test --keep-vm
```

Leaves the VM, tap device, and rootfs copy alive for manual SSH debugging.

`test` refuses to run if `init` has not completed successfully — it checks the saved kernel and base-rootfs paths and fails with "Kernel not found. Run pks firecracker init first." (or the equivalent rootfs message) if either is missing. The SSH connectivity check retries for up to 30 seconds; if the guest never comes up, the whole test aborts as failed after that timeout and still attempts best-effort cleanup. Network allocation, rootfs preparation, VM boot, and SSH connectivity are hard-fail-and-abort steps; the six smoke checks themselves are soft — they all run to completion, and the exit code reflects passed-count versus total.

## runner start

```text
pks firecracker runner start [options]
```

Runs the Firecracker runner as a long-lived foreground daemon. It resolves (or auto-registers, with `--project`) a project runner registration, then polls `{server}/api/owners/{owner}/projects/{project}/runners/jobs` every `--polling-interval` seconds for `alp_runner_spawn` capability jobs. On receiving a job it claims it (`POST .../runners/generate-jitconfig`), allocates a tap-device network slot, copies the base rootfs, boots a microVM sized from the job or the host defaults, marks the run `in_progress` (`PATCH .../runs/{runId}/jobs/{jobId}`), waits up to 30 seconds for SSH, runs the job's command over SSH with a 1-hour timeout, then marks the run `completed`/`success` or `failure`. The VM, network slot, and rootfs copy are always torn down in a `finally` block. Ctrl-C (`SIGINT`) or process exit (`SIGTERM`) stops the poll loop and cleans up any VM mid-flight.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |
| `--server <SERVER>` | `AGENTIC_SERVER` env, then `agentics.dk` | Agentics server URL. |
| `--project <OWNER_PROJECT>` | — | Project in `owner/project` format. Auto-registers if not already registered. |
| `--polling-interval <SECONDS>` | `10` | Polling interval in seconds. |
| `--max-concurrent-vms <COUNT>` | `5` | Accepted but currently has no effect — see below. |

```bash
pks firecracker runner start --server agentics.dk
```

Uses the first saved registration (or auto-registers if `--project` is also given) against the named server.

```bash
pks firecracker runner start --project owner/project
```

Auto-registers this owner/project against the resolved server if no local registration exists yet, then starts polling.

**Endpoint:** `POST /api/owners/{owner}/projects/{project}/runners` (auto-registration, unauthenticated), `POST /api/owners/{owner}/projects/{project}/runners/jobs` (polling), `PATCH /api/owners/{owner}/projects/{project}/runs/{runId}/jobs/{jobId}` (status updates, `Authorization: Bearer {token}`).

- `--max-concurrent-vms` is read into settings but never referenced in the polling loop — the daemon always processes jobs one at a time regardless of this flag's value.
- The job-command SSH timeout is fixed at 3600 seconds with no override flag; a command still running past an hour is killed.
- If the main job-handling block throws, the catch path builds an `HttpClient` but never sends a `PATCH` — a crashed job can leave the server-side run showing `in_progress` indefinitely.
- Auto-registration's `POST .../runners` call carries no auth header at all; registration itself is unauthenticated at the CLI side.
- Passing `--server` alongside an existing saved registration whose stored server differs silently rewrites that registration's server field to the new value (with an info message printed) — repointing an existing runner identity to a different environment.

## Auth model

`runner start --project owner/project` auto-registers against `{server}/api/owners/{owner}/projects/{project}/runners` if no local registration exists, storing the returned `{id, name, token}` in `~/.pks-cli/firecracker-runners.json`. Every subsequent poll and job-status update sends `Authorization: Bearer {token}`. The server defaults to `agentics.dk` over `https://`; `localhost`/`127.0.0.1` hosts get `http://` automatically.

### Global environment variables

| Variable | Default | Purpose |
|---|---|---|
| `AGENTICS_SERVER` | `agentics.dk` | Overrides the agentics.dk server host used for auto-registration when `--project` is given and `--server` is not. Checked before `AGENTIC_SERVER`. |
| `AGENTIC_SERVER` | `agentics.dk` | Fallback server host override, same role as `AGENTICS_SERVER`, checked second. |

## Troubleshooting

> **Note.** The suggested follow-up commands `pks firecracker cleanup <vmId>` and `pks firecracker runner register <owner/project>` are not registered in this build. Treat any message referencing them as stale help text.

- **`init` fails with a raw Docker error.** Docker is not checked as a prerequisite even though the rootfs build needs it. Confirm the Docker daemon is running before re-running `init`.
- **`test` says the kernel or rootfs is missing.** `init` has not completed successfully, or its output files were deleted. Re-run `pks firecracker init`.
- **`test --keep-vm` left a VM around and you want it gone.** There is no `cleanup` subcommand. Delete the VM's directory under `{workDir}/vms/{vmId}` (`workDir` defaults to `~/.pks-cli/firecracker`), then release its tap device and `iptables` rule by hand, or remove the corresponding entry from `{workDir}/network-state.json`.
- **`runner start` says no registration exists.** There is no `runner register` subcommand. Pass `--project owner/project` to auto-register instead.
- **`ip tuntap add` or `iptables` commands fail with permission denied.** `runner start` needs root for tap-device and NAT setup; there is no explicit privilege check ahead of the failure.
- **A run stays `in_progress` on the server after a crash.** The daemon's exception-handling path for a failed job does not send a status `PATCH` in every case. Check the run manually and re-trigger if needed.
- **VMs still get `172.16.x.x` addresses after passing a custom `--subnet`.** `--subnet` is saved and displayed but `FirecrackerNetworkManager.ComputeAddresses` hardcodes the `172.16.` octets and never reads the configured subnet — there is currently no way to change the actual VM address range.

## Defaults

| Setting | Value |
|---|---|
| Work directory | `~/.pks-cli/firecracker` |
| Runner registration store | `~/.pks-cli/firecracker-runners.json` |
| Network-slot state | `{workDir}/network-state.json` |
| Default vCPUs per VM | `2` |
| Default memory per VM | `2048` MiB |
| Default subnet | `172.16.0.0/16` (hardcoded in address computation, no override — see Troubleshooting) |
| Guest kernel | `vmlinux-5.10.217` (hardcoded, no override) |
| Job SSH timeout | `3600` seconds |
| SSH wait during `runner start` | `30` seconds |
| Default polling interval | `10` seconds |

These live in `~/.pks-cli/firecracker-runners.json` after `init`; `--work-dir`, `--vcpus`, and `--mem-mib` on `init` are the only way to change them (`--subnet` is stored but not applied).

## See also

- [pks agentics runner](/tools/pks/agentics) — the container-based ALP runner this replaces when VM-grade isolation isn't needed
- [pks github runner](/tools/pks/github) — self-hosted GitHub Actions runner registration
- [pks vm](/tools/pks/vm) — cloud VM provisioning, a different concern from local microVM job execution
- [pks ssh](/tools/pks/ssh) — generic SSH-target management, unrelated to the guest-VM SSH keypair `init` generates
