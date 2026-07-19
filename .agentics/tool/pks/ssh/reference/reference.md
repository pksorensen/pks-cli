---
title: "pks ssh reference"
description: "Complete command, argument, flag, and file-path reference for the pks ssh group — target registry, encrypted key store, and the action-guard gate."
tags: [reference, ssh, cli]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks ssh <command> [options]"
examples:
  - command: "pks ssh register root@projects.si14agents.com -i ./id_rsa"
    description: "Register a host with a specific private key"
  - command: "pks ssh key import --label hetzner --register root@1.2.3.4"
    description: "Import a key and bind a target to it"
  - command: "pks ssh list"
    description: "Show every registered target"
  - command: "pks ssh connect pks-vm-2e93"
    description: "Open an interactive shell on a target"
  - command: "pks ssh run hetzner -- uname -a"
    description: "Run one command and stream its output"
  - command: "pks ssh copy hetzner:~/out.json ./out.json"
    description: "Download a file from a target"
---

`pks ssh` maintains a registry of named SSH targets, an AES-GCM-encrypted store of pks-held private keys, and three gated commands that use them: an interactive session, a one-shot command, and an scp transfer.

All three outbound commands shell out to the system `ssh` and `scp` binaries — pks uses no SSH library. `ssh key import` additionally calls `ssh-keygen` to derive the public key and fingerprint. `pks ssh` is part of the pks CLI; see [pks](/tools/pks) for installation and the rest of the command surface.

## Synopsis

```text
pks ssh <command> [options]
```

```text
register [TARGET]     Add a host to the local SSH target registry
list                  Print every registered SSH target
remove [TARGET]       Delete a registered target from the registry
connect [TARGET]      Open an interactive SSH session on a target
run <TARGET> [CMD]    Run one non-interactive command on a target
copy <SOURCE> <DEST>  Copy a file or directory to or from a target
key import            Import a private key into the encrypted key store
key list              List pks-held SSH keys
key remove [KEY]      Delete a pks-held SSH key
```

### Files and storage

| Path | Contents |
|---|---|
| `~/.pks-cli/ssh-targets.json` | Registered targets as plaintext JSON — id, host, username, port, key path or held-key id, label, registration timestamp. |
| `~/.pks-cli/ssh-keys/index.json` | Metadata for pks-held keys — id, label, fingerprint, import timestamp. |
| `~/.pks-cli/ssh-keys/<id>.key` | One AES-GCM-encrypted private key blob per held key. |
| `~/.pks-cli/.ssh-keys-kek` | The 32-byte key-encryption key, deliberately stored outside the key directory. |

The `pks ssh` group reads no environment variables of its own.

### The action guard

`connect`, `run`, and `copy` call the action guard under the action id `ssh.connect` before starting any process. With an authenticator enrolled, the guard requires the operator's second factor; a denial exits with code 1 and the guard's message, and no connection is made. `register`, `list`, `remove`, and the `key` subcommands touch only local state and are not gated.

`connect` gates a VM start separately under the `vm.start` action.

### Host-key verification

`connect`, `run`, and `copy` all pass `-o StrictHostKeyChecking=no`, so no `known_hosts` prompt appears. Trust rests on the explicit local registration step. `run` and `copy` additionally pass `BatchMode=yes`, which rules out interactive password and passphrase prompts.

## register

Adds a host to the target registry so later commands address it by label or host. Parses `TARGET` as `user@host`, splitting on the first `@` — at least one `@` is required with non-empty text on both sides, and anything after a second `@` is folded into the host rather than rejected; a target with zero `@` characters, or an empty user/host side, exits with code 1. With `-i` the target records that key path; without it, the target is recorded as using the ambient SSH agent. `--test` runs a connectivity probe (`-o StrictHostKeyChecking=accept-new -o BatchMode=yes`) after saving; a failing probe warns and the target is still saved. Re-registering the same host, username, and port replaces the prior entry with no confirmation. Omitting `TARGET` prompts for it.

| Argument | Required | Description |
|---|---|---|
| `TARGET` | no | SSH target in `user@host` form. Prompted when omitted. |

| Flag | Default | Description |
|---|---|---|
| `-i, --identity <PATH>` | — | Path to the SSH private key file. |
| `-p, --port <PORT>` | `22` | SSH port. |
| `--label <LABEL>` | — | Friendly label for this target. |
| `--test` | — | Test SSH connectivity after registering. |

```bash
pks ssh register deploy@1.2.3.4 --label hetzner --test
```

## list

Prints a table of all registered targets — label, host, user, port, key path, and registration timestamp. Read-only and ungated. An empty registry prints a hint pointing at `pks ssh register` rather than an error.

## remove

Deletes a registered target from the registry. The remote host is untouched, and any pks-held key the target referenced remains in the store. A destructive confirmation defaults to no. Omitting `TARGET` opens an interactive picker.

| Argument | Required | Description |
|---|---|---|
| `TARGET` | no | Host, label, or `user@host` to remove. Picker shown when omitted. |

## connect

Opens an interactive SSH session with your terminal attached to the system `ssh` process. In order: resolves the target by label or host (picker when omitted), prunes any registered target labeled `pks-*` whose backing VM is absent from local VM metadata, checks the action guard for `ssh.connect`, offers to start a tracked Azure VM that is stopped or deallocated, and materializes a pks-held key to a short-lived 0600 temp file when the target uses one.

A VM start is confirmed interactively and then gated under `vm.start`; billing resumes on start. pks then polls SSH readiness for up to three minutes and prints a yellow warning on timeout instead of failing. The command's exit code is the remote `ssh` process's exit code.

| Argument | Required | Description |
|---|---|---|
| `TARGET` | no | Target label or host. Picker shown when omitted. |

```bash
pks ssh connect pks-vm-2e93
```

## run

Runs one non-interactive command on a target. Local stdin is forwarded to the remote process and remote stdout and stderr stream through untouched, so the command composes inside a pipeline. The remote command is taken from the arguments after `--`, which win over the positional `CMD` when both are present. Supplying neither prints a usage error and exits 1 without connecting. The exit code mirrors the remote command's.

| Argument | Required | Description |
|---|---|---|
| `TARGET` | yes | Target label or host. |
| `CMD` | no | Command to run. Passing it after `--` is preferred. |

```bash
tar czf - dir | pks ssh run hetzner -- "cd ~/dst && tar xzf -"
```

## copy

Copies a file or directory over `scp`, with the remote side written as `<target>:<path>` and resolved from the registry. Exactly one of `SOURCE` and `DEST` must resolve to a registered target; both or neither is rejected with an explicit error. A target prefix must be at least two characters before the colon, so a Windows drive prefix such as `C:\build` is treated as a local path. The port is passed with `scp`'s uppercase `-P`.

| Argument | Required | Description |
|---|---|---|
| `SOURCE` | yes | Source path — local, or `<target>:<remote-path>`. |
| `DEST` | yes | Destination path — local, or `<target>:<remote-path>`. |

| Flag | Description |
|---|---|
| `-r, --recursive` | Recurse into directories. |

```bash
pks ssh copy ./build.tar hetzner:~/build.tar
```

## key import

Imports an SSH private key into the encrypted store, pasted interactively or read from a file, deriving the public key and fingerprint with `ssh-keygen`. The derived public key is printed for you to add to the remote host's `~/.ssh/authorized_keys` — pks does not install it. `--register` additionally creates a target bound to the key; malformed `user@host` input skips registration with a yellow warning while the import still succeeds.

Interactive capture ends on a blank line after a line starting with `-----END`, or on a blank line once `-----END` has appeared in the buffer. A passphrase-protected key fails `ssh-keygen` parsing.

| Flag | Default | Description |
|---|---|---|
| `--label <LABEL>` | — | Friendly label for this key. |
| `--from-file <PATH>` | — | Read the private key from a file instead of pasting it. |
| `--register <TARGET>` | — | Also register an SSH target in `user@host` form bound to this key. |
| `-p, --port <PORT>` | `22` | SSH port for the registered target. |

```bash
pks ssh key import --label hetzner --register root@1.2.3.4
```

## key list

Lists pks-held keys with id, label, fingerprint, and import timestamp. Read-only and ungated. An empty store prints a hint pointing at `pks ssh key import`.

## key remove

Permanently deletes a held key — the encrypted blob and its index entry. A destructive confirmation defaults to no, and an interactive picker appears when `KEY` is omitted. Removal is irreversible: the private key material is deleted rather than archived. Any target still referencing the key's id fails at connect, run, or copy time with `Could not access pks-held key`.

| Argument | Required | Description |
|---|---|---|
| `KEY` | no | Key id or label to remove. Picker shown when omitted. |

## See also

- [pks ssh](/tools/pks/ssh) — the group landing page and mental model
- [Register an SSH target](/tools/pks/ssh/register) — the registration walkthrough
- [Connect to an SSH target](/tools/pks/ssh/connect) — interactive sessions and VM auto-start
- [Run a command on an SSH target](/tools/pks/ssh/run) — one-shot execution and pipe idioms
- [Copy files to and from an SSH target](/tools/pks/ssh/copy) — scp transfers by label
- [Hold SSH keys in pks](/tools/pks/ssh/key) — the encrypted key store end to end
