---
title: "pks hetzner"
description: "Store a Hetzner Cloud API token and a default location so pks vm can create, start, stop, and delete per-hour Hetzner Cloud servers with the price shown before you commit."
tags: [reference, hetzner, auth, vm]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks hetzner <command> [options]"
examples:
  - command: "pks hetzner init"
    description: "Validate and store a Hetzner Cloud API token"
  - command: "pks hetzner init --force"
    description: "Re-authenticate and overwrite the stored token"
---

`pks hetzner` stores the Hetzner Cloud API token that `pks vm` needs to provision and manage Hetzner Cloud servers — the cheapest per-hour machines in the fleet (a 2 vCPU / 4 GB server is under a cent an hour), for runners and agents that only need to exist while they work.

## Overview

Hetzner Cloud has no OAuth flow. A token is created per **project** in the Cloud Console (project → Security → API tokens → *Read & Write*) and sent as a bearer header on every API call. The token *is* the project — there is no organization or sub-account API — so `pks hetzner init` validates it by listing locations, asks you to label the project and pick a default location, and persists the result to the pks-cli global config store.

- **One command, one job.** `init` only sets up authentication — it never lists, creates, starts, stops, or deletes a server.
- **Feeds `pks vm`, not itself.** Every server operation runs through `pks vm init` / `pks vm list` / `pks vm status` / `pks vm destroy`, which pick Hetzner Cloud as one of the cloud-agnostic VM providers once this credential exists.
- **Static, not renewable.** Rotating the token means minting a new one in the Cloud Console and running `pks hetzner init --force`.

## The billing fact that shapes everything

**Hetzner bills a server from create until delete, whatever its power state.** A powered-off server costs the full hourly rate (capped at the monthly price), and its primary IPv4 is billed on top. Azure's "deallocated = free" model does not apply here.

So on Hetzner the cheap pattern is *create → use → delete*, not *start → stop*:

| You want | Do this | Costs |
|---|---|---|
| A runner one hour a day | `pks vm init` in the morning, `pks vm destroy` after | hourly rate × 1 h × 30 ≈ €0.20–0.60/month |
| Keep it, use it rarely | `pks vm stop` | the full monthly cap, every month |
| Keep the disk, not the server | snapshot it (Cloud Console or API) before destroy, create from the snapshot next time | €0.0119/GB/month net for the snapshot's actual size |
| Persistent data across machines | a Hetzner Volume attached at create | €0.044/GB/month net, survives the server |

`pks vm init` prints all of these numbers for the exact type and location before the create prompt.

## Commands

`init` is the only registered subcommand.

## init

Prompts for the API token (masked), validates it with `GET /locations`, asks for a project label, and lets you pick a default location from the live list (EU-central locations first, since the cheapest `cx` line only exists in Falkenstein, Nuremberg and Helsinki). Storing the token is gated by the `cloud.auth.write` second-factor action.

### Synopsis

```text
pks hetzner init [--force]
```

| Flag | Type | Default | Description |
|---|---|---|---|
| `-f`, `--force` | `bool` | `false` | Re-authenticate even if a Hetzner credential is already stored. |

> **Note.** Without `--force`, if a credential is already stored `init` prints the stored project label and location and exits — a token revoked in the Cloud Console is not detected until `pks vm` fails with `401 unauthorized`.

## Troubleshooting

- **`Authentication failed: Hetzner API 401 Unauthorized`.** The token is wrong, revoked, or from a different project than you think. Mint a new *Read & Write* token and rerun `init --force`.
- **`pks vm init` creates the server but SSH never answers.** Check the Cloud Console firewall for the project; `pks vm init` does not attach one, but a project-default firewall may block port 22.
- **A server you deleted is still on the invoice.** Hetzner bills per started hour; a server deleted at 10:05 is billed through 11:00. Primary IPv4s that were *unassigned* rather than deleted keep billing on their own — `pks vm destroy` deletes the server's IPv4 with it.
- **`vm stop` warned that billing continues.** That is correct. Use `pks vm destroy` when you are done, and a snapshot if you want the disk back.

## Defaults

| Setting | Value |
|---|---|
| Credential store key | `hetzner.auth.credentials` |
| Auth header | `Authorization: Bearer <token>` |
| API | `https://api.hetzner.cloud/v1` |
| Two-factor action | `cloud.auth.write` |
| Token scope | One Cloud Console project |

## See also

- [pks vm](/tools/pks/vm) — provision, start, stop, and destroy the Hetzner (or Azure, or Scaleway) VMs this credential unlocks
- [Create a VM with pks vm init](/tools/pks/vm/init) — the wizard, including the Hetzner price panel and boot timeline
- [pks scaleway](/tools/pks/scaleway) — the equivalent static-key flow for Scaleway GPU instances
