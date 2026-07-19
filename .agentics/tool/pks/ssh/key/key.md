---
title: "Hold SSH keys in pks"
description: "Import an SSH private key into the encrypted pks key store, list what is held, and remove keys, so targets connect without a plaintext key on disk."
tags: [how-to, ssh, keys, security]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks ssh key <import|list|remove> [options]"
examples:
  - command: "pks ssh key import --label hetzner --register root@1.2.3.4"
    description: "Import a key and bind a target to it"
  - command: "pks ssh key import --label hetzner --from-file ./id_ed25519"
    description: "Import a key from a file instead of pasting"
  - command: "pks ssh key list"
    description: "Show held keys with fingerprints"
  - command: "pks ssh key remove hetzner"
    description: "Delete a held key permanently"
---

`pks ssh key` moves an SSH private key into a pks-held store at `~/.pks-cli/ssh-keys/`, encrypted with AES-GCM. A target bound to a held key connects through `pks ssh connect`, `run`, or `copy` — each of which passes the action guard — so the key stops being a plaintext file that any process running as you can copy and use unobserved.

> **Note.** The key-encryption key lives at `~/.pks-cli/.ssh-keys-kek`, outside the key directory but in the same home directory. While pks runs as the same operating-system user as your agents, this is isolation against casual reads, not confidentiality against a local attacker. The command's own source says as much.

## 1. Prerequisites

- **`ssh-keygen` on PATH.** Import derives the public key and fingerprint from the private key with it.
- **An unencrypted private key.** A passphrase-protected key fails `ssh-keygen` parsing with a clear error.
- **Access to the remote host's `authorized_keys`.** Import prints the public key; installing it on the host is your step.

## 2. Import the key

Paste it interactively:

```bash
pks ssh key import --label hetzner
```

Or read it from a file:

```bash
pks ssh key import --label hetzner --from-file ./id_ed25519
```

Interactive capture ends on a blank line after the `-----END` line. Input pasted in an unusual shape can end the capture early or wait for a terminator that never arrives — use `--from-file` when the paste misbehaves.

## 3. Bind a target in the same call

```bash
pks ssh key import --label hetzner --register root@1.2.3.4 --port 2222
```

`--register` takes `user@host`. Malformed input skips target registration with a yellow warning; the import itself still succeeds, and you can register separately with [pks ssh register](/tools/pks/ssh/register).

## 4. Install the public key on the host

Import prints the derived public key. Add that line to the remote account's `~/.ssh/authorized_keys`. pks does not push it for you.

## 5. Verify

```bash
pks ssh key list
```

The table shows the key's id, label, fingerprint, and import timestamp. Then exercise the binding:

```bash
pks ssh run hetzner -- "echo ok"
```

`ok` is printed. At connect time the key is decrypted into a short-lived 0600 temp file for the duration of that one `ssh` or `scp` process, then shredded.

## 6. Remove a key

```bash
pks ssh key remove hetzner
```

Omit the argument for an interactive picker. The destructive confirmation defaults to no.

## Options

### import

| Flag | Default | Description |
|---|---|---|
| `--label <LABEL>` | — | Friendly label for the key, such as `hetzner`. |
| `--from-file <PATH>` | — | Read the private key from a file instead of pasting it. |
| `--register <TARGET>` | — | Also register an SSH target in `user@host` form bound to this key. |
| `-p, --port <PORT>` | `22` | SSH port for the target created by `--register`. |

### list

Takes no arguments and no flags. An empty store prints a hint pointing at `pks ssh key import`.

### remove

| Argument | Required | Description |
|---|---|---|
| `KEY` | no | Key id or label to delete. An interactive picker is shown when omitted. |

## Troubleshooting

**`ssh-keygen` rejects the key.** The key is passphrase-protected or truncated. Import an unencrypted key, and prefer `--from-file` over pasting.

**The paste never finishes.** Capture ends on a blank line following `-----END`. Press Enter once more, or re-run with `--from-file`.

**`--register` was ignored.** The target string was not `user@host`. Register the target separately with [pks ssh register](/tools/pks/ssh/register).

**`Could not access pks-held key` on connect.** The key was removed while a target still references it. Removing a key does not remove the target. Import the key again, or re-register the target against a key file.

**Removal cannot be undone.** `pks ssh key remove` deletes the private key material rather than archiving it. Keep your own copy before removing anything you still need.

## Next steps

- [Register an SSH target](/tools/pks/ssh/register) — bind a target to a held key after the fact
- [Connect to an SSH target](/tools/pks/ssh/connect) — use the held key in an interactive session
- [Run a command on a target](/tools/pks/ssh/run) — confirm the key works non-interactively
- [pks ssh reference](/tools/pks/ssh/reference) — file paths, storage layout, and the whole flag surface
