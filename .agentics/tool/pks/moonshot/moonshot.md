---
title: "pks moonshot"
description: "Register a Moonshot API key so pks opencode can launch OpenCode on Kimi K3 — validated before it is stored, reused process-locally."
tags: [reference, moonshot, auth, kimi, opencode]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks moonshot <command> [options]"
examples:
  - command: "pks moonshot init"
    description: "Register and validate a fresh Moonshot API key"
  - command: "pks moonshot init --force"
    description: "Replace the stored Moonshot API key"
---

`pks moonshot` stores the Moonshot API key that `pks opencode --model kimi-k3` needs to launch OpenCode against Moonshot's OpenAI-compatible API.

## Overview

Moonshot has no OAuth flow. Authentication is a static API key, generated in the [Moonshot platform console](https://platform.moonshot.ai/console/api-keys) and sent as a `Bearer` token on every API call. `pks moonshot init` is the only command in this group: it collects the key, **validates it against the live Moonshot API before storing anything**, then persists the result to the pks-cli global config store.

- **One command, one job.** `pks moonshot init` only sets up authentication — it never lists models, starts sessions, or spends tokens.
- **Feeds `pks opencode`, not itself.** The stored key is reused process-locally when you launch OpenCode on `kimi-k3`; it is never written into an OpenCode config file.
- **Validated up front.** `init` calls the Moonshot `GET /models` endpoint with your key as the bearer token. A rejected key is never stored.
- **Static, not renewable.** There is no token refresh. Rotating the key means generating a new one in the Moonshot console and running `pks moonshot init --force`.

## What you get

- **A verified credential.** The key is confirmed against `https://api.moonshot.ai/v1` before it touches disk — a typo or revoked key fails fast instead of surfacing as a cryptic OpenCode error later.
- **A stored, reusable key.** The key persists globally under your user profile, so later `pks opencode --model kimi-k3` runs need no further Moonshot prompts.
- **A two-factor gate on writes.** Storing or replacing the key runs through the `cloud.auth.write` action guard, so an enrolled authenticator is asked to confirm a fresh `init --force` — the same gate that protects `pks scaleway init`.

## How it fits together

`pks moonshot init` prints the console URL where keys are created, prompts for the API key (masked input), and validates it with a `GET` to `https://api.moonshot.ai/v1/models`. Only after a successful validation — and after the two-factor `cloud.auth.write` gate confirms — is the key written to the global pks-cli config store under `moonshot.auth.credentials`.

When you later run `pks opencode --model kimi-k3`, the provider catalog resolves `kimi-k3` to Moonshot, reads this stored key, and hands it to the OpenCode process as the `MOONSHOT_API_KEY` environment variable, referenced from an inline provider config. The key never appears on the command line and never lands in `opencode.json`.

- **At a glance, the write path:** you run `pks moonshot init`, pks validates the key against Moonshot, then the two-factor gate confirms before the key is written to the config store.
- **At a glance, the read path:** `pks opencode --model kimi-k3` reads the same stored key and injects it per-process as `MOONSHOT_API_KEY`.

## Commands

`init` is the only registered subcommand.

## init

Interactively registers a Moonshot API key. Prompts for the key (secret-masked input), validates it against the Moonshot API, and stores it on success. If a key is already stored and `--force` is not passed, the command says so and exits without prompting.

Storing the result is gated by the `cloud.auth.write` two-factor action; on success it prints a confirmation and a hint to run `pks opencode --model kimi-k3` next.

### Synopsis

```text
pks moonshot init [--force]
```

| Flag | Type | Default | Description |
|---|---|---|---|
| `-f`, `--force` | `bool` | `false` | Replace an existing stored Moonshot API key. |

```bash
pks moonshot init
```

First-time setup: prompts for the API key, validates it against Moonshot, and stores the credential.

```bash
pks moonshot init --force
```

Overwrites a previously stored key — the only way to rotate a Moonshot key from this command group.

> **Note.** Without `--force`, if a key is already stored `init` only prints that registration exists and exits — it never re-prompts, so a key rotated or revoked on Moonshot's side is not detected automatically.

## Troubleshooting

- **`init` exits without prompting for a key.** A key is already stored. Pass `--force` to re-authenticate; there is no other way to trigger a re-prompt.
- **"Moonshot rejected the API key."** The key failed validation against `GET https://api.moonshot.ai/v1/models` — nothing was stored. Double-check the key in the Moonshot console and watch for stray whitespace when pasting. A network failure produces the same outcome, so retry once if the key is definitely right.
- **`init --force` prompts for the key, then fails at the very end.** The two-factor `cloud.auth.write` gate runs after the key has already been validated — only the final persist step is blocked if the authenticator isn't enrolled or the code is wrong or denied.
- **Need to remove a stored Moonshot key entirely.** There is no `pks moonshot logout` or `remove` command, even though the underlying service supports clearing credentials internally. The only CLI-level path is to overwrite it with `pks moonshot init --force`.
- **`pks opencode --model kimi-k3` says no Moonshot API key is configured.** Either `init` was never run, or it was run under a different user profile — the credential store is per user. Run `pks moonshot init`.

## Defaults

| Setting | Value |
|---|---|
| Credential store key | `moonshot.auth.credentials` |
| Token storage | Plaintext JSON, global config store |
| API base URL | `https://api.moonshot.ai/v1` |
| Validation call | `GET /models` with `Authorization: Bearer <key>` |
| Launch-time env var | `MOONSHOT_API_KEY` |
| Two-factor action | `cloud.auth.write` |

No environment variable overrides the credential store location — it is the same global pks-cli config store used by Scaleway, Azure, Foundry, and ADO credentials.

> **Do not commit.** The API key is stored in plaintext JSON in the global config store, with no OS keychain and no encryption on this path. Treat that store as a secret file.

## See also

- [pks opencode](/tools/pks/opencode) — launch OpenCode on Kimi K3 (or Scaleway's GLM 5.2) with a single command
- [pks scaleway](/tools/pks/scaleway) — the equivalent auth flow for Scaleway GPU and serverless work
- [pks](/tools/pks) — the full command surface and where `moonshot` fits among the cloud identity commands
