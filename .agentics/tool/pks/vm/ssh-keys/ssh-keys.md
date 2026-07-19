---
title: "Manage VM SSH keys"
description: "Adopt a VM that pks did not provision by pasting its private key, and export a known key so another machine or agent can reach the same box over SSH."
tags: [how-to, vm, ssh, keys]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks vm add-ssh-key [VM_NAME] · pks vm export-ssh-key [VM_NAME]"
examples:
  - command: "pks vm add-ssh-key"
    description: "Attach an existing private key to a tracked VM"
  - command: "pks vm add-ssh-key my-vm"
    description: "Attach a key to a named VM without the picker"
  - command: "pks vm export-ssh-key my-vm"
    description: "Print a snippet installing the key elsewhere"
---

A VM provisioned by [pks vm init](/tools/pks/vm/init) already has its key under `~/.pks-cli/keys/<vm-name>`. Two commands cover the cases where that is not true: `pks vm add-ssh-key` adopts a machine created in a cloud console, and `pks vm export-ssh-key` hands a known key to another machine or to an agent running somewhere else.

## Prerequisites

- **A tracked VM.** Both commands operate on the list shown by `pks vm list`. If no VMs are tracked at all, `add-ssh-key` tells you to run `pks vm list` first.
- **The private key itself,** for `add-ssh-key` — either as text you can paste or as a file on disk.
- **`ssh-keygen` on PATH,** to derive the matching public key. Its absence does not block the command.

## add-ssh-key

```bash
pks vm add-ssh-key my-vm
```

| Argument | Required | Description |
|---|---|---|
| `VM_NAME` | no | The VM to attach the key to. An interactive picker appears when omitted. |

The command offers two sources for the key:

- **Pasted inline.** Paste the key material; input stops at the `-----END ... PRIVATE KEY-----` line. Echoed prompt lines before the `BEGIN` line are ignored, so a messy paste still works.
- **From a file path.** The file must literally contain the string `PRIVATE KEY` or it is rejected as invalid.

The key is written to `~/.pks-cli/keys/<vm-name>` with `0600` permissions on POSIX systems, and the matching `.pub` file is derived with `ssh-keygen -y`. Public-key derivation is best-effort — if it fails, the `.pub` file is not written and the command still succeeds.

> **Note.** An existing key file at the same path is overwritten without warning.

Afterwards, `pks ssh connect <name>` and `pks claude --ssh-target <name>` reach the machine, and `pks vm status` can fetch its remote stats.

## export-ssh-key

```bash
pks vm export-ssh-key my-vm
```

| Argument | Required | Description |
|---|---|---|
| `VM_NAME` | no | The VM to export the key for. An interactive picker appears when omitted. |

The output is a shell heredoc you can paste on another machine. It installs the private key there and immediately opens an SSH session to the VM — the fastest way to give a coding agent running in a different devcontainer access to the same box.

> **Do not commit.** The raw private key is printed directly to standard output with no redaction or masking. Treat the whole output as a secret, and never paste it into a chat log, an issue, or a repository.

The printed connect command uses `-o StrictHostKeyChecking=no`, matching every other SSH invocation in this command group.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "No local private key" from `export-ssh-key` | pks has no key file for that machine. Run `pks vm add-ssh-key` to paste it in first. |
| The file source is rejected | The file does not contain the string `PRIVATE KEY`. Confirm you pointed at the private key, not the `.pub`. |
| No `.pub` file appears next to the key | `ssh-keygen -y` failed. The private key still works; regenerate the public key by hand if you need it. |
| `pks vm add-ssh-key` says to run `pks vm list` | No VMs are tracked. This command adopts keys for machines pks already knows about, not brand-new ones. |

## See also

- [Create a VM with pks vm init](/tools/pks/vm/init) — provisioning generates the key for you
- [Inspect VMs with list and status](/tools/pks/vm/inspect) — remote stats need a working key
- [Join a VM to Tailscale](/tools/pks/vm/tailscale) — requires a usable local key
- [pks vm](/tools/pks/vm) — the full command group
