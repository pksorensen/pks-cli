---
title: "Copy files to and from an SSH target"
description: "Move files and directories between your machine and a registered SSH target with scp, addressing the remote side as target:path instead of user@host."
tags: [how-to, ssh, files]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks ssh copy <SOURCE> <DEST> [options]"
examples:
  - command: "pks ssh copy ./build.tar hetzner:~/build.tar"
    description: "Upload a file to a target"
  - command: "pks ssh copy hetzner:~/out.json ./out.json"
    description: "Download a file from a target"
  - command: "pks ssh copy ./dist hetzner:~/dist --recursive"
    description: "Upload a directory tree"
---

`pks ssh copy` transfers files and directories over `scp`, with the remote side written as `<target>:<path>`. The target part is resolved from the registry the same way `connect` and `run` resolve it, so the label replaces the full user, host, port, and identity.

## 1. Prerequisites

- **A registered target.** Create one with [pks ssh register](/tools/pks/ssh/register).
- **The `scp` binary on PATH.** `copy` shells out to it directly.
- **Key-based access that needs no prompt.** `copy` uses `BatchMode=yes`, so no passphrase prompt is possible.
- **An enrolled authenticator, if two-factor is required.** The `ssh.connect` guard applies to `copy` as well.

## 2. Upload a file

```bash
pks ssh copy ./build.tar hetzner:~/build.tar
```

Exactly one of `SOURCE` and `DEST` must resolve to a registered target. Both remote or neither remote is rejected with an explicit error.

## 3. Download a file

```bash
pks ssh copy hetzner:~/out.json ./out.json
```

The remote spec goes in the source position; the local path in the destination.

## 4. Copy a directory

```bash
pks ssh copy ./dist hetzner:~/dist --recursive
```

Without `-r`, `scp` refuses a directory.

## 5. Verify

Confirm the file landed on the far side:

```bash
pks ssh run hetzner -- "ls -l ~/build.tar"
```

The listing prints the expected size.

## Options

| Flag | Description |
|---|---|
| `-r, --recursive` | Recurse into directories. |

| Argument | Required | Description |
|---|---|---|
| `SOURCE` | yes | Source path — local, or `<target>:<remote-path>`. |
| `DEST` | yes | Destination path — local, or `<target>:<remote-path>`. |

## Troubleshooting

**`Exactly one of SOURCE/DEST must be a remote`.** Either both sides parsed as targets or neither did. Check the label against `pks ssh list`, and confirm the remote side is written as `label:/path`.

**A Windows path like `C:\build` was treated as local.** That is correct. A target prefix needs at least two characters before the colon, so single-letter drive prefixes are never mistaken for a label.

**The transfer fails on a directory.** Add `-r`.

**Permission denied with no prompt.** `BatchMode=yes` prevents interactive fallback. Load the key into your agent, or bind the target to a pks-held key — see [pks ssh key](/tools/pks/ssh/key).

**A custom port seems ignored.** `copy` passes the port with `scp`'s uppercase `-P`, taken from the registry entry. Re-register with `--port` if the stored value is wrong.

## Next steps

- [Run a command on a target](/tools/pks/ssh/run) — verify what you transferred, or stream an archive instead
- [Connect to an SSH target](/tools/pks/ssh/connect) — interactive access to the same host
- [Register an SSH target](/tools/pks/ssh/register) — change the port or identity a target uses
- [pks ssh reference](/tools/pks/ssh/reference) — the complete flag and file-path surface
