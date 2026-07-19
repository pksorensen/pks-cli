---
title: "pks ssh"
description: "Register remote hosts as named SSH targets, hold their private keys encrypted, and route every outbound connection through the pks action guard."
tags: [cli, ssh, remote, security]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks ssh <command> [options]"
examples:
  - command: "pks ssh register root@projects.si14agents.com -i ./id_rsa"
    description: "Register a host as a named target"
  - command: "pks ssh key import --label hetzner --register root@1.2.3.4"
    description: "Import a private key and bind a target to it"
  - command: "pks ssh list"
    description: "Show every registered target"
  - command: "pks ssh connect pks-vm-2e93"
    description: "Open an interactive shell on a target"
  - command: "pks ssh run hetzner -- uname -a"
    description: "Run one command and stream its output"
  - command: "pks ssh copy ./build.tar hetzner:~/build.tar"
    description: "Upload a file to a target"
---

`pks ssh` is the remote-access command group of the pks CLI: a local registry of named SSH targets, an optional encrypted store for the private keys those targets use, and a single gate that every outbound connection passes through.

## Overview

`pks ssh` keeps a registry of the machines you reach over SSH — host, username, port, and either a key-file path or a pks-held key — at `~/.pks-cli/ssh-targets.json`. After a host is registered, you address it by label instead of retyping connection details, and the same target is reusable by devcontainer-spawn and VM flows elsewhere in pks.

- **Named targets.** `pks ssh register` turns `deploy@1.2.3.4 -p 2222 -i ./id_rsa` into the label `hetzner`.
- **Held keys.** `pks ssh key import` moves a private key into an AES-GCM-encrypted store, so no plaintext key sits in a path a coding agent can read and copy.
- **One gate.** `connect`, `run`, and `copy` all call the action guard under the action id `ssh.connect`, so an enrolled second factor stands between an agent and any outbound SSH session.

## What you get

- **A target registry.** `register`, `list`, and `remove` maintain the set of hosts pks knows about. Re-registering the same host, username, and port replaces the previous entry.
- **An encrypted key store.** `key import`, `key list`, and `key remove` manage keys at `~/.pks-cli/ssh-keys/`. A held key is decrypted only into a short-lived 0600 temp file for the lifetime of one `ssh` or `scp` invocation.
- **Three ways to use a target.** An interactive shell (`connect`), a single streamed command (`run`), and file transfer (`copy`) — all resolving the target the same way.
- **Pipeline-safe output.** `pks ssh run` and `pks ssh copy` suppress the pks banner, so their stdout stays clean inside a shell pipeline.
- **Azure VM awareness.** `connect` recognizes a target backed by a VM that `pks vm` tracks, offers to start it when it is stopped, and waits for SSH to come up.

## How it fits together

Registration is the only step that writes new state. `pks ssh register` records the connection details; `pks ssh key import` records key material and can register a bound target in the same call. Everything after that reads the registry: you name a target, pks resolves it to host, user, port, and identity, then shells out to the system `ssh` or `scp` binary with those values. No SSH library is involved — the local `ssh`, `scp`, and `ssh-keygen` binaries do the work.

The gate sits between resolution and execution. Before the process starts, `connect`, `run`, and `copy` call the action guard for `ssh.connect`. With no authenticator enrolled the guard passes through; once one is enrolled it demands the second factor, and a denial exits with the guard's message instead of connecting.

- **Local-only commands:** `register`, `list`, `remove`, `key import`, `key list`, `key remove` — no network, no gate.
- **Outbound commands:** `connect`, `run`, `copy` — gated on `ssh.connect` every time.

## Commands

`register` · `list` · `remove` · `connect` · `run` · `copy` · `key import` · `key list` · `key remove`

Full argument and flag detail for every one of them is on the [pks ssh reference](/tools/pks/ssh/reference).

## Next steps

- [Register an SSH target](/tools/pks/ssh/register) — turn a host into a reusable named target and test it
- [Connect to a target](/tools/pks/ssh/connect) — interactive shells, VM auto-start, and the action guard
- [Run a command on a target](/tools/pks/ssh/run) — one-shot commands, stdin forwarding, and pipe idioms
- [Copy files to and from a target](/tools/pks/ssh/copy) — scp uploads and downloads by label
- [Hold SSH keys in pks](/tools/pks/ssh/key) — import, list, and remove keys in the encrypted store
- [pks ssh reference](/tools/pks/ssh/reference) — every command, flag, file path, and default

## Defaults

| Setting | Value |
|---|---|
| Target registry | `~/.pks-cli/ssh-targets.json` |
| Held-key store | `~/.pks-cli/ssh-keys/` |
| Key-encryption key | `~/.pks-cli/.ssh-keys-kek` |
| SSH port | `22` |
| Host-key checking (`connect`, `run`, `copy`) | `-o StrictHostKeyChecking=no` |
| Host-key checking (`register --test` probe) | `-o StrictHostKeyChecking=accept-new` |
| Guarded action id | `ssh.connect` |

The `pks ssh` group reads no environment variables of its own; behavior is driven entirely by the registry, the key store, and the action-guard policy. See the [pks ssh reference](/tools/pks/ssh/reference) for the per-command detail.
