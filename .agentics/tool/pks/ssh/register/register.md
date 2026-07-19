---
title: "Register an SSH target"
description: "Add a remote host to the pks SSH registry with a label, port, and identity file, then address it by name from connect, run, and copy."
tags: [how-to, ssh, targets]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks ssh register <user@host> [options]"
examples:
  - command: "pks ssh register root@projects.si14agents.com -i ./id_rsa"
    description: "Register a host with a specific private key"
  - command: "pks ssh register deploy@1.2.3.4 --label hetzner --test"
    description: "Register with a label and probe connectivity"
  - command: "pks ssh list"
    description: "Show every registered target"
  - command: "pks ssh remove projects.si14agents.com"
    description: "Drop a target from the registry"
---

Turn a remote host into a named target that the rest of `pks ssh` can address: register it once with a user, host, port, and identity, confirm it appears in the registry, and probe the connection.

Registration writes only to the local registry at `~/.pks-cli/ssh-targets.json`. It never contacts the remote host except for the optional `--test` probe.

## 1. Prerequisites

- **The `ssh` binary on PATH.** `pks ssh register --test` shells out to the system `ssh` for its connectivity probe.
- **A private key file, or a loaded SSH agent.** Pass the key with `-i`. Omit `-i` and the target is recorded as using the ambient SSH agent.
- **The remote host already trusting your key.** Registration does not install anything in the remote `~/.ssh/authorized_keys`.

## 2. Register the host

```bash
pks ssh register root@projects.si14agents.com -i ./id_rsa
```

The target argument is split on the first `@`, so it must contain at least one `@` with non-empty text on both sides; anything after a second `@` is folded into the host rather than rejected. A target with zero `@` characters, or an empty user/host side, is rejected and the command exits with code 1.

Add a label to give the target a short name, a port when the host does not listen on 22, and `--test` to probe the connection right away:

```bash
pks ssh register deploy@1.2.3.4 --label hetzner --port 2222 --test
```

Omit the target argument and pks prompts for it interactively.

## 3. Confirm the entry

```bash
pks ssh list
```

The table prints one row per target with its label, host, user, port, key path, and registration timestamp. An empty registry prints a hint pointing back at `pks ssh register` rather than an error.

## 4. Verify

Run a command through the new target:

```bash
pks ssh run hetzner -- uname -a
```

The remote kernel line is printed on stdout, and the command exits with the remote exit code. If the action guard is armed, approve the `ssh.connect` prompt first.

## Options

| Flag | Default | Description |
|---|---|---|
| `-i, --identity <PATH>` | — | Path to the SSH private key file. Omitted means the ambient SSH agent. |
| `-p, --port <PORT>` | `22` | SSH port of the remote host. |
| `--label <LABEL>` | — | Friendly label used to address the target later. |
| `--test` | — | Probe SSH connectivity immediately after registering. |

| Argument | Required | Description |
|---|---|---|
| `TARGET` | no | SSH target in `user@host` form. Prompted interactively when omitted. |

## Troubleshooting

**The command exits 1 without saving.** The target string did not parse. It must contain at least one `@` with non-empty text on both sides of the first `@`, as in `root@10.0.0.4`.

**`--test` reported a failure but the target was saved anyway.** That is the designed behavior — a failing probe warns, and registration still completes. Fix the key or the firewall, then re-run `pks ssh run` to check.

**Registering again produced no confirmation prompt.** A registration with the same host, username, and port replaces the previous entry silently. Use `pks ssh list` to confirm the surviving values.

**The target has no key path in `pks ssh list`.** `-i` was omitted, so the target uses the SSH agent. Re-register with `-i`, or import the key with [pks ssh key](/tools/pks/ssh/key) and bind a target to it.

## Next steps

- [Connect to a target](/tools/pks/ssh/connect) — open an interactive shell on what you registered
- [Hold SSH keys in pks](/tools/pks/ssh/key) — import the key instead of pointing at a file on disk
- [Run a command on a target](/tools/pks/ssh/run) — one-shot commands and pipe idioms
- [pks ssh reference](/tools/pks/ssh/reference) — every command, flag, and file path in the group
