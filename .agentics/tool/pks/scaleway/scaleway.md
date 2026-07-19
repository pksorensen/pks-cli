---
title: "pks scaleway"
description: "Store a Scaleway API key pair so pks vm can provision, start, and stop Scaleway GPU instances under a default project and zone."
tags: [reference, scaleway, auth, gpu]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks scaleway <command> [options]"
examples:
  - command: "pks scaleway init"
    description: "Authenticate with a fresh Scaleway API key pair"
  - command: "pks scaleway init --force"
    description: "Re-authenticate and overwrite stored credentials"
---

`pks scaleway` stores the Scaleway API key pair that `pks vm` needs to provision and manage Scaleway-backed VMs, including the GPU instance types (H100, L40S, L4) used for remote devcontainer and GPU hosts.

## Overview

Scaleway has no OAuth flow. Authentication is a static Access Key and Secret Key pair, generated once in the Scaleway console and sent as the `X-Auth-Token` header on every API call. `pks scaleway init` is the only command in this group: it collects that key pair, resolves your organization and a default project, lets you confirm the project and pick a default zone, then persists the result to the pks-cli global config store.

- **One command, one job.** `pks scaleway init` only sets up authentication — it never lists, creates, starts, stops, or destroys a Scaleway server.
- **Feeds `pks vm`, not itself.** Every actual VM operation runs through `pks vm init`/`pks vm start`/`pks vm status`/etc., which pick Scaleway as one of the cloud-agnostic VM providers once this credential exists.
- **Static, not renewable.** There is no token refresh. Rotating the key means generating a new pair in the Scaleway console and running `pks scaleway init --force`.

## What you get

- **A resolved organization and default project.** `init` calls the Scaleway IAM `api-keys/{accessKey}` endpoint to look up the key's organization and default project, then confirms the project against the Account `projects/{projectId}` endpoint.
- **A default zone for new VMs.** You pick one zone from a fixed list; `pks vm init` uses it unless told otherwise.
- **A stored, reusable credential.** The key pair persists globally under your user profile, so later `pks vm` commands need no further Scaleway prompts.
- **A two-factor gate on writes.** Storing or overwriting the credential runs through the `cloud.auth.write` action guard, so an enrolled authenticator is asked to confirm a fresh `init --force`. `pks scaleway init` is currently the only cloud-auth `init` command gated this way — `pks azure init` and `pks foundry init` write credentials with no two-factor confirmation, despite the action's catalog description ("Store or replace Scaleway/Azure/Foundry credentials") suggesting otherwise.

## How it fits together

`pks scaleway init` prompts for the Access Key, then the Secret Key (masked input), and calls the Scaleway IAM and Account APIs to resolve the organization ID and default project. If that lookup fails, it falls back to asking for the Organization ID and Project ID by hand — those manual entries aren't validated beyond non-empty. You then confirm a default project (auto-picked if there's only one, a selection prompt if there are several) and pick a default zone from a hardcoded list. The key pair and selections are written to the global pks-cli config store under `scaleway.auth.credentials`, and every later Scaleway API call reuses the secret key verbatim as the `X-Auth-Token` header.

Nothing under `pks scaleway` talks to compute at all. GPU-instance operations — list, create, power on/off, destroy — live behind the cloud-agnostic `pks vm` command group, which picks Scaleway as a provider once `pks scaleway init` has run.

- **At a glance, the write path:** you run `pks scaleway init`, pks resolves org/project against Scaleway's IAM and Account APIs, then the two-factor gate confirms before the key pair is written to the config store.
- **At a glance, the read path:** `pks vm` reads the same stored credential and sends it as `X-Auth-Token` on every Scaleway compute call.

## Commands

`init` is the only registered subcommand.

## init

Interactively authenticates with Scaleway. Prompts for the Access Key and a secret-masked Secret Key, then auto-resolves the organization ID and default project by calling the Scaleway IAM `api-keys/{accessKey}` and Account `projects/{projectId}` endpoints. If that lookup fails, it falls back to manual prompts for the Organization ID and Project ID.

You then confirm the default project — auto-picked if there's only one, a selection prompt otherwise — and pick a default zone from a fixed list (`fr-par-1`, `fr-par-2`, `fr-par-3`, `nl-ams-1`, `nl-ams-2`, `nl-ams-3`, `pl-waw-1`, `pl-waw-2`, `pl-waw-3`). GPU instance types like H100, L40S, and L4 aren't available in every zone, and `init` does not check that.

Storing the result is gated by the `cloud.auth.write` two-factor action; on success it prints a summary table (Organization, Project, Project ID, Default zone) and a hint to run `pks vm list` next.

### Synopsis

```text
pks scaleway init
```

| Flag | Type | Default | Description |
|---|---|---|---|
| `-f`, `--force` | `bool` | `false` | Force re-authentication even if a Scaleway credential is already stored. |

```bash
pks scaleway init
```

First-time setup: prompts for the API key pair, resolves the organization and project, picks a zone, and stores the credential.

```bash
pks scaleway init --force
```

Overwrites a previously stored credential — the only way to rotate a Scaleway key pair from this command group.

> **Note.** Without `--force`, if a credential is already stored `init` only prints the stored project and zone and exits — it never re-prompts, so a key rotated or revoked on Scaleway's side is not detected automatically.

## Troubleshooting

- **`init` exits without prompting for a key.** A credential is already stored. Pass `--force` to re-authenticate; there is no other way to trigger a re-prompt.
- **Organization or project came out wrong.** The automatic lookup against Scaleway's IAM and Account APIs can fail and fall back to manual entry, which isn't validated beyond non-empty. Re-run with `--force` and double-check the Organization ID and Project ID against the Scaleway console before confirming.
- **A chosen zone doesn't offer the GPU instance type you need.** The zone list is hardcoded in `pks-cli`, not fetched live from Scaleway, and `init` doesn't check GPU availability per zone. Confirm instance-type availability in the Scaleway console before relying on the default zone for a GPU workload.
- **`init --force` prompts for the key, then fails at the very end.** The two-factor `cloud.auth.write` gate runs after the key has already been validated against Scaleway's APIs — only the final persist step is blocked if the authenticator isn't enrolled or the code is wrong or denied.
- **Need to remove a stored Scaleway credential entirely.** There is no `pks scaleway logout` or `remove` command, even though the underlying service supports clearing credentials internally. The only CLI-level path is to overwrite it with `pks scaleway init --force`.
- **Looking for `pks scaleway list` or `status`.** They don't exist. Status is only ever surfaced inline by `init` itself ("Already authenticated…"); to see or use provisioned VMs, use `pks vm list` and `pks vm status`.

## Defaults

| Setting | Value |
|---|---|
| Credential store key | `scaleway.auth.credentials` |
| Token storage | Plaintext JSON, global config store |
| Auth header | `X-Auth-Token` |
| Two-factor action | `cloud.auth.write` |
| Available zones | `fr-par-1/2/3`, `nl-ams-1/2/3`, `pl-waw-1/2/3` |

No environment variable overrides the credential store location — it is the same global pks-cli config store used by Azure, Foundry, and ADO credentials.

> **Do not commit.** The Secret Key is stored in plaintext JSON in the global config store, with no OS keychain and no encryption on this path. Treat that store as a secret file.

## See also

- [pks vm](/tools/pks/vm) — provision, start, stop, and destroy the Scaleway (or Azure) VMs this credential unlocks
- [pks foundry](/tools/pks/foundry) — the equivalent auth-then-select flow for Azure AI Foundry
- [pks](/tools/pks) — the full command surface and where `scaleway` fits among the cloud identity commands
