---
title: "pks cert reference"
description: "Command reference for pks cert — create, list, inspect, export, and remove the pks-held code-signing certificates that pks sign uses to sign Windows artifacts."
tags: [reference, cli, signing]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks cert <command> [options]"
examples:
  - command: "pks cert init"
    description: "Create the first self-signed certificate, interactively"
  - command: "pks cert list"
    description: "List every pks-held certificate"
  - command: "pks cert show agentics"
    description: "Print subject, thumbprint, and public PEM for one cert"
  - command: "pks cert export agentics -o AgentShare.cer"
    description: "Export the public trust certificate to a file"
  - command: "pks cert remove agentics"
    description: "Delete a certificate and its encrypted private key"
---

`pks cert` manages the code-signing certificates `pks sign` uses to sign Windows artifacts (MSIX/EXE/MSI). A certificate is created once with `cert init` and reused unattended across CI runs, so the signer identity stays stable release after release. It has five subcommands: `init`, `list`, `show`, `export`, and `remove`.

## Overview

Each certificate is stored AES-GCM-encrypted at rest under `~/.pks-cli/certs/` — an `index.json` entry plus one `{id}.pfx` blob per cert. The 32-byte key-encryption key lives in a sibling file, `~/.pks-cli/.certs-kek`, outside the certs directory. `pks` documents this as "obfuscation-grade while pks shares the agent's OS user; real isolation arrives when pks runs as its own user" — treat it as tamper resistance, not confidentiality against another process running as the same user.

None of the `cert` subcommands talk to a remote service; this is entirely a local, file-backed store. The public `.cer` file (no private key) is what you hand out to consumers so they trust the signer once instead of re-trusting on every release — never distribute the `.pfx`.

Use `pks cert` when you need a stable signing identity for local Windows-artifact signing, or on the host that runs `pks github runner start` so CI job containers can fetch a short-lived materialized PFX over the runner credential socket without ever holding the encrypted key or the KEK themselves. Actual signing happens through the separate top-level `pks sign` command, not through `cert` itself — see [pks github runner](/tools/pks/github/runner) for the runner side of that handoff.

## Synopsis

```text
pks cert <command> [options]
```

```text
init      Create a new self-signed code-signing certificate
list      List every pks-held certificate
show      Show full details of one certificate, including its public PEM
export    Export the public .cer trust certificate to a file
remove    Delete a certificate and its encrypted .pfx blob
```

### Global environment variables

| Variable | Default | Purpose |
|---|---|---|
| `SUDO_USER` | `(unset)` | Presence marks the invocation as coming through the in-container sudo escalation path. `cert init` refuses to run when set. |
| `SUDO_UID` | `(unset)` | Same detection as `SUDO_USER`, checked as an alternate signal. |
| `PKS_TOKEN` | `(unset)` | Not read by `cert` itself. Consumed by `pks sign`'s fallback path when no local cert is found. |
| `PKS_TOKEN_URL` | `(unset)` | Path to the runner credential socket. Consumed by `pks sign`'s fallback path, not by `cert`. |

`PKS_TOKEN` and `PKS_TOKEN_URL` are set inside a pks-runner job container by the host, not by anything under `cert`. They matter here only because they explain why `cert init` is normally run once on the runner host rather than once per job container: a container without a local cert falls back to fetching a materialized PFX over that socket instead.

## init

Creates a new self-signed code-signing certificate and stores it encrypted in the local pks cert store. This is the one-time trust event — run it interactively on a trusted host, never inside the devcontainer via `sudo`, so the in-container agent can never mint its own signing identity.

The command prompts for a subject (must match the MSIX appxmanifest Publisher; defaults to `CN=Agentic Live (Self-Signed)`), an optional friendly label (defaults to `agentics`), and a validity period (1, 2, 3, or 5 years, via a selection prompt). After creation it also prompts for a path to export the public `.cer` file, defaulting to `~/.pks-cli/certs/{id}.cer`.

`cert init` has no flags or arguments.

```bash
pks cert init
```

Follow the prompts for subject, label, and validity, then confirm the `.cer` export path when asked.

`cert init` refuses to run in three situations:

- **Invoked via sudo.** When `SUDO_USER` or `SUDO_UID` is set — i.e. run as `sudo -u pks pks cert init` inside the devcontainer — it prints an error and a host-enrollment hint (run it via `docker exec -it -u pks <container> pks cert init`, or directly on the trusted host) and exits `1`.
- **Non-interactive stdin or stdout.** Redirected/piped input or output means prompts have nowhere to go, so it exits `1` with the same guidance to run interactively.
- **A cert already exists.** Creating an *additional* certificate goes through the `cert.write` ActionGuard check (a local TOTP second-factor gate, action id `cert.write`, description "Create/replace signing cert", default **required** in the action catalog). This gate is trust-on-first-use: it only does anything once `pks authenticator init` has enrolled a TOTP factor. On a fresh install with nothing enrolled — the default state — the check passes silently and the second cert is created with no prompt and no way to fail; see [pks actions](/tools/pks/actions) and [pks authenticator](/tools/pks/authenticator). Once a factor is enrolled and `cert.write` is toggled on, a denial prints the denial reason and exits `1` without creating anything.

Two more behaviors worth knowing before you run it: if certificate creation itself fails (for example an invalid subject), the command prints the exception message and exits `1` before the export prompt ever appears. And if creation succeeds but the `.cer` export afterward fails, that failure is non-fatal — it prints a yellow warning and the command still exits `0`, leaving the certificate created but unexported. Re-export later with `pks cert export` or `pks cert show --export-cer`.

## list

Lists every pks-held certificate in a table: Id, Label, Provider, Subject, Thumbprint, and Expires (`yyyy-MM-dd`). Use it to find the id or label to pass to `show`, `export`, or `remove`, or to check whether any certificate exists before running `pks sign`.

`cert list` has no flags or arguments.

```bash
pks cert list
```

When the store is empty it prints "No pks-held certificates." plus a hint to run `pks cert init`, and exits `0` — an empty store is not an error.

## show

Shows full details of one certificate: id, label, provider, subject, thumbprint, validity window, and the public certificate PEM printed to stdout. Use it to inspect a certificate's exact subject or thumbprint before trusting it, or to pipe the PEM elsewhere. It can also export the public `.cer` in the same invocation.

| Flag | Default | Description |
|---|---|---|
| `--export-cer <PATH>` | — | Also export the public `.cer` to this path. |

| Argument | Required | Description |
|---|---|---|
| `CERT` | no | Cert id or label. |

```bash
pks cert show agentics
```

Prints the details and PEM for the certificate labeled or id'd `agentics`.

```bash
pks cert show
```

Omitting `CERT` resolves to the sole stored certificate if exactly one exists in the store. If the store holds zero or more than one certificate, it prints "Cert not found." and exits `1` — unlike `cert remove`, `show` does not fall back to an interactive picker. Lookup matches by exact id first, then by exact label, case-insensitive.

## export

Exports the public trust certificate (DER-encoded `.cer`, no private key) of a stored certificate to a file, so it can be distributed to consumers or installed into their trusted-publisher store. This is the artifact you hand out — never the `.pfx`.

| Flag | Default | Description |
|---|---|---|
| `-o`, `--output <PATH>` | `{current-directory}/{cert-id}.cer` | Destination `.cer` path. |

| Argument | Required | Description |
|---|---|---|
| `CERT` | no | Cert id or label. Defaults to the sole cert. |

```bash
pks cert export agentics -o AgentShare.cer
```

Writes the public certificate for `agentics` to `AgentShare.cer`, creating any missing parent directories automatically.

If `-o`/`--output` is omitted, the destination defaults to `{current-directory}/{cert-id}.cer` — note that this uses the certificate's **id**, not its label. If `CERT` is omitted and the store holds more than one certificate, `export` reports "Cert not found (specify an id/label when more than one exists)." and exits `1`; there is no interactive picker here either.

## remove

Deletes a pks-held certificate permanently: removes it from the index and deletes its encrypted `.pfx` blob from disk. Use it when a certificate is compromised, expired, or superseded.

`cert remove` has no flags.

| Argument | Required | Description |
|---|---|---|
| `CERT` | no | Cert id or label to remove. |

```bash
pks cert remove agentics
```

Always asks for explicit y/n confirmation (default: no) before deleting — `Remove cert {id} ({label})?` — regardless of how the certificate was resolved.

If `CERT` is omitted, `remove` presents an interactive selection prompt listing every certificate — the one subcommand in this group that offers a picker instead of failing on ambiguity. That also means `remove` cannot run fully non-interactively without an explicit id or label argument. If the store is completely empty, it prints "No pks-held certificates." and exits `0` without prompting.

> **Do not commit.** Deletion is unrecoverable. There is no cloud or provider backing for the self-signed certificates this command creates — once the `.pfx` blob is deleted, the private key material is gone.

## Troubleshooting

**`cert init` exits immediately with a sudo error.** You ran it as `sudo -u pks pks cert init` inside the devcontainer. Run `docker exec -it -u pks <container> pks cert init` instead, or run it directly on the runner host.

**`cert init` exits immediately with no prompts shown.** stdin or stdout is redirected — for example the command is running inside a script or a non-TTY CI step. `cert init` needs a real interactive terminal; run it by hand on a trusted host.

**Creating a second certificate is denied.** Once one certificate exists, adding another goes through the `cert.write` ActionGuard check — but that check is inert until a TOTP authenticator has been enrolled (`pks authenticator init`); on a fresh install it silently passes and the cert is created. If you *are* enrolled and `cert.write` is toggled on in `pks actions`, a failed or cancelled challenge prints the denial reason and creates nothing — resolve the approval before retrying.

**`cert show` or `cert export` reports "Cert not found" with no picker.** Both subcommands only auto-resolve to a single certificate when the store holds exactly one. With zero or multiple certificates, pass `CERT` explicitly — run `pks cert list` first to find the id or label.

## See also

- [pks](/tools/pks) — the pks CLI overview and full command surface
- [pks github runner](/tools/pks/github/runner) — the runner that fetches a materialized PFX over the credential socket when no local cert exists
- [pks ssh key](/tools/pks/ssh/key) — the analogous pks-held key store, for SSH private keys instead of signing certificates
- [pks actions](/tools/pks/actions) — toggles whether `cert.write` (and other sensitive actions) require a TOTP code
- [pks authenticator](/tools/pks/authenticator) — enrolls the TOTP factor that makes the `cert.write` gate live at all; without it, the gate is inert
