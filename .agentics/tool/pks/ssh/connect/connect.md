---
title: "Connect to an SSH target"
description: "Open an interactive shell on a registered SSH target, with stale pks VM entries pruned and a stopped Azure VM started before the session begins."
tags: [how-to, ssh, remote, vm]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks ssh connect [TARGET]"
examples:
  - command: "pks ssh connect pks-vm-2e93"
    description: "Open a shell on a named target"
  - command: "pks ssh connect"
    description: "Pick a target from an interactive list"
  - command: "pks ssh list"
    description: "See which targets are available"
---

`pks ssh connect` opens an interactive SSH session on a registered target. It resolves the target by label or host, prunes stale pks-provisioned VM entries, starts the backing Azure VM when one is tracked and stopped, passes the connection through the action guard, and hands your terminal to the system `ssh` process.

## 1. Prerequisites

- **A registered target.** Create one with [pks ssh register](/tools/pks/ssh/register), or let `pks vm init` register it for you.
- **The `ssh` binary on PATH.** The session is the real `ssh` process with your terminal attached.
- **An enrolled authenticator, if two-factor is required.** The `ssh.connect` action guard denies the connection when the factor is required and not satisfied.
- **Azure sign-in, only for VM auto-start.** Without it, `connect` skips the VM status check and attempts SSH directly.

## 2. Pick a target

```bash
pks ssh connect pks-vm-2e93
```

Omit the target and pks shows an interactive picker built from the registry. Before the picker appears, any target labeled `pks-*` whose backing VM is no longer present in local VM metadata is pruned from the registry.

## 3. Approve the connection

The action guard runs under the id `ssh.connect`. With an authenticator enrolled, supply the second factor at the prompt. A denial ends the command with exit code 1 and the guard's own message — no SSH process is started.

## 4. Let a stopped VM start

If the target maps to an Azure VM that `pks vm` tracks and that VM is stopped or deallocated, `connect` offers to start it. The start is gated separately under the `vm.start` action. Starting the VM resumes billing.

After a start, pks polls for SSH readiness for up to three minutes. A timeout prints a yellow warning rather than failing outright, so you can retry once the machine finishes booting.

## 5. Verify

You land on the remote shell prompt. Confirm the machine:

```bash
hostname
```

Leaving the session returns the remote `ssh` process's exit code as the exit code of `pks ssh connect`, so a non-zero result from the remote side is preserved.

## Arguments

| Argument | Required | Description |
|---|---|---|
| `TARGET` | no | Target label or host. An interactive picker is shown when omitted. |

`pks ssh connect` takes no flags. Connection details come from the registry entry created at registration time.

## Troubleshooting

**The target disappeared from the picker.** Its label started with `pks-` and the backing VM is gone from local VM metadata, so the entry was pruned. Register it again with [pks ssh register](/tools/pks/ssh/register) if the host still exists.

**The command exits 1 with a guard message.** Two-factor is required for `ssh.connect` and was not satisfied. Enroll or complete the factor, then retry.

**`Could not access pks-held key`.** The target references a key id that is no longer in the store — most often because `pks ssh key remove` deleted it. Import the key again and re-register the target. See [pks ssh key](/tools/pks/ssh/key).

**SSH still refuses after the VM starts.** The three-minute readiness poll expired and warned instead of failing. Wait for the boot to finish and run `pks ssh connect` again.

**No host-key prompt appeared.** `connect` passes `-o StrictHostKeyChecking=no`, so host-key verification is bypassed by design. Trust rests on the explicit local registration step.

## Next steps

- [Run a command on a target](/tools/pks/ssh/run) — non-interactive execution with streamed output
- [Copy files to and from a target](/tools/pks/ssh/copy) — scp transfers addressed by label
- [Register an SSH target](/tools/pks/ssh/register) — add or replace the entry you are connecting to
- [pks ssh reference](/tools/pks/ssh/reference) — argument, flag, and file-path detail for the whole group
