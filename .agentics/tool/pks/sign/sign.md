---
title: "pks sign"
description: "Sign a Windows artifact (MSIX, EXE, DLL, or MSI) with a pks-held code-signing certificate, unattended, from a developer machine or a CI job container."
tags: [reference, cli, signing]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks sign <input> [options]"
examples:
  - command: "pks sign AgentShareCompanion.msix"
    description: "Sign with the sole cert in the local store, default output name"
  - command: "pks sign app.msix -o app-signed.msix -c agentics"
    description: "Explicit output path and explicit cert label"
  - command: "pks sign app.exe --timestamp http://timestamp.digicert.com"
    description: "Sign with a trusted RFC3161 timestamp"
---

`pks sign` Authenticode/MSIX-signs a Windows artifact with a pks-held code-signing certificate, producing a signed copy alongside the original. It is a single leaf command — registered as `pks sign`, not a branch — with no subcommands.

## Overview

`pks sign` takes one input artifact (`.msix`, `.msixbundle`, `.exe`, `.dll`, or `.msi`), resolves a signing certificate, and shells out to `osslsigncode` to produce a signed copy. It has no interactive prompts and no second-factor gate, so it runs unattended in CI.

- **From a developer machine.** Run `pks cert init` once on a trusted host, then sign locally with the certificate that command created.
- **From a CI job container.** Run `pks sign` inside a container spawned by `pks github runner start`; the signing key is fetched just-in-time over a local credential socket and never persisted in the container.

## What you get

- **A signed copy of the artifact.** The original file is untouched; the signed output is written to a new path.
- **Two cert-resolution paths, tried in order.** The local pks cert store first, then — only if no local cert is found — the runner credential socket.
- **No credential material left behind in a job container.** The fetched PFX is written to a 0600 temp file, used once by `osslsigncode`, then deleted in a best-effort `finally` block.
- **A stable timestamp option.** `--timestamp` passes an RFC3161/Authenticode server straight through to `osslsigncode`, so the signature stays valid after the certificate expires.

## How it fits together

`pks sign` resolves its signing key in two steps. First it looks in the local pks cert store (populated by `pks cert init`): if `-c`/`--cert` is given, it looks up that id or label; if omitted, it uses the sole cert when exactly one exists, or prints a disambiguation message and falls through. Second, only if no local cert was resolved, it checks for `PKS_TOKEN` and `PKS_TOKEN_URL` — env vars a runner host injects automatically into a job container it spawned via `pks github runner start`. When both are present, it fetches a short-lived PFX over that Unix domain socket with `Authorization: Bearer <PKS_TOKEN>`, so the encrypted private key and its key-encryption key never enter the container.

Once a PFX and password are in hand, the command shells out to `osslsigncode sign -pkcs12 <pfx> -pass <password> -h sha256 [-t <timestampUrl>] -in <input> -out <output>`. A non-zero exit surfaces `osslsigncode`'s stderr verbatim; a zero exit with no output file is also reported as a failure.

- **At a glance, the local path:** `pks cert init` on a trusted host → `pks sign` resolves the cert from `~/.pks-cli/certs/` → `osslsigncode` signs.
- **At a glance, the runner path:** `pks github runner start` on a host with a cert → the job container gets `PKS_TOKEN`/`PKS_TOKEN_URL` → `pks sign` fetches a materialized PFX over the credential socket → `osslsigncode` signs → the temp PFX is deleted.

## Prerequisites

- **`osslsigncode` on `PATH`**, or the `OSSLSIGNCODE` env var pointing at its binary. On Debian/Ubuntu: `sudo apt-get install -y osslsigncode`. This is the only signing backend registered today — every `pks sign` invocation goes through it.
- **A resolvable signing key**, either: a certificate already created with [`pks cert init`](/tools/pks/cert) on the machine running `pks sign`; or `pks sign` running inside a container spawned by [`pks github runner start`](/tools/pks/github/runner) on a host that itself holds a pks-held cert.
- **The input artifact must exist on disk** at the given path — `pks sign` fails immediately with `Input not found: <path>` otherwise.

## Synopsis

```text
pks sign <input> [options]
```

### Global environment variables

| Variable | Default | Purpose |
|---|---|---|
| `OSSLSIGNCODE` | `(unset)` | Explicit path to the `osslsigncode` binary, checked before a `PATH` lookup. |
| `PKS_TOKEN` | `(unset)` | Bearer token for the runner credential socket. Read only when no local cert was resolved. |
| `PKS_TOKEN_URL` | `(unset)` | Path to the runner credential Unix domain socket. Set automatically inside a job container spawned by `pks github runner start`. |

`PKS_TOKEN` and `PKS_TOKEN_URL` are set by the runner host, not by anything under `sign` — they exist only to explain why `pks sign` works unattended inside a job container without ever running `pks cert init` there.

## sign

Signs `INPUT` and writes the result to `OUTPUT` (or the default naming below). Cert resolution and the `osslsigncode` invocation are described in [How it fits together](#how-it-fits-together) above.

| Argument | Required | Description |
|---|---|---|
| `INPUT` | yes | Artifact to sign (`.msix`/`.msixbundle`/`.exe`/`.dll`/`.msi`). |

| Flag | Default | Description |
|---|---|---|
| `-o`, `--output <OUTPUT>` | `<input-dir>/<input-name>-signed<ext>` | Output path for the signed artifact, e.g. `app.msix` → `app-signed.msix`. |
| `-c`, `--cert <CERT>` | the sole pks-held cert, if exactly one exists | Cert id or label to sign with — looked up in the local pks cert store, or forwarded as the `?id=` query param when fetching the PFX from the credential socket. |
| `--timestamp <URL>` | `(none)` | RFC3161/Authenticode timestamp server URL, passed through verbatim to `osslsigncode`'s `-t` flag. |

```bash
pks sign AgentShareCompanion.msix
```

Signs with the sole cert in the local store; writes `AgentShareCompanion-signed.msix` next to the input.

```bash
pks sign app.msix -o app-signed.msix -c agentics
```

Signs with the cert labeled `agentics` and writes to the explicit output path.

```bash
pks sign app.exe --timestamp http://timestamp.digicert.com
```

Signs with a trusted timestamp so the signature stays valid after the certificate expires. `--timestamp` is not validated for URL shape or reachability by `pks` itself — a bad or unreachable server surfaces only as an `osslsigncode` non-zero exit.

## Troubleshooting

**`osslsigncode not found on PATH`.** Install it (`sudo apt-get install -y osslsigncode` on Debian/Ubuntu) or set the `OSSLSIGNCODE` env var to its binary path.

**`Multiple certs found — specify one with -c <id|label>`.** The local cert store holds more than one certificate and `-c`/`--cert` was omitted. Run [`pks cert list`](/tools/pks/cert) to find the id or label, then pass `-c` explicitly.

**`No certificate available. Run pks cert init (host) or run inside a pks runner container.`** No local cert resolved and either `PKS_TOKEN`/`PKS_TOKEN_URL` are unset or the credential socket returned nothing usable. Run `pks cert init` on the machine running `pks sign`, or run `pks sign` inside a container spawned by `pks github runner start` on a host that has one.

**A 404 from the credential socket.** This means the *runner host* — not the job container — has no pks-held certificate to serve. Run [`pks cert init`](/tools/pks/cert) on that host; running it inside the container does not fix it.

**`Input not found: <path>`.** The artifact path is wrong or the file does not exist. `pks sign` checks this before doing anything else.

**Signing runs but the output file is unexpected or missing.** A non-zero `osslsigncode` exit surfaces its stderr verbatim in the failure message; a zero exit with no output file produced is reported as `osslsigncode reported success but produced no output file`. Check the appended `osslsigncode` error text first.

> **Note.** `pks sign` overwrites `OUTPUT` if it already exists — `osslsigncode`'s own behavior applies, and `pks` does not pre-check or warn.

## See also

- [pks cert](/tools/pks/cert) — creates and manages the local pks-held certificate this command signs with
- [pks github runner](/tools/pks/github/runner) — spawns the job containers that get `PKS_TOKEN`/`PKS_TOKEN_URL` injected for unattended signing
- [pks](/tools/pks) — the full command surface and where `sign` fits among the identity commands
