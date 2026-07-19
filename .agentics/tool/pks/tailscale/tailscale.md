---
title: "pks tailscale reference"
description: "Store a Tailscale auth key and join preferences that `pks vm tailscale` reads to join a provisioned VM to your tailnet over SSH."
tags: [reference, cli, tailscale, vm]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks tailscale init"
examples:
  - command: "pks tailscale init"
    description: "Configure a Tailscale auth key and join preferences"
  - command: "pks tailscale init --force"
    description: "Re-enter and overwrite the stored key and toggles"
---

`pks tailscale` stores a Tailscale auth key and a handful of join preferences in the global `pks-cli` settings file. It does not install or run Tailscale on anything itself — it only persists the credentials that `pks vm tailscale` later reads to run `tailscale up` on a provisioned VM over SSH.

## Overview

The group has one command, `init`, which prompts for a reusable Tailscale auth key plus three join toggles and writes them to `~/.pks-cli/settings.json` under the key `tailscale.auth.credentials`. Run it once before your first `pks vm tailscale` call to seed the config non-interactively ahead of time, or run it with `--force` later to rotate the key or change a toggle. If you skip it, `pks vm tailscale` detects the missing config and runs this same prompt flow inline before continuing, so running it standalone first is a convenience, not a requirement.

## Prerequisites

- **A Tailscale (or Headscale) account with a reusable auth key.** Generate one at `login.tailscale.com` → Settings → Keys — a reusable, ephemeral key is recommended.
- **No `pks` authentication of its own.** `pks tailscale init` only prompts and writes local config; it does not need you to be signed in to anything.

## Synopsis

```text
pks tailscale <command> [options]
```

```text
init    Store a Tailscale auth key and join preferences for `pks vm tailscale`
```

## init

Prompts for a Tailscale auth key (masked input) and three yes/no join toggles — Tailscale SSH, accept subnet routes, advertise as exit node — each defaulting to enabled, followed by an optional, blank-allowed custom control-server URL for a self-hosted Headscale deployment. It then writes the result as `TailscaleStoredCredentials` to the global config key `tailscale.auth.credentials` in `~/.pks-cli/settings.json`. This is the credential source `pks vm tailscale` reads to build its `tailscale up` argument string when joining a VM to the tailnet.

If credentials already exist and `--force` is not passed, the command short-circuits with a confirmation message and exits without prompting for anything.

| Flag | Default | Description |
|---|---|---|
| `-f, --force` | `false` | Re-enter the auth key and all toggles even if credentials are already stored. |

```bash
pks tailscale init
```

Walks through the masked auth-key prompt, then the three toggles (Tailscale SSH, accept subnet routes, advertise exit node — all default on), then an optional Headscale control-server URL. Leave the control-server prompt blank to use Tailscale's own SaaS control plane.

```bash
pks tailscale init --force
```

Re-prompts for the auth key and every toggle, overwriting whatever is already stored. This is the only way to rotate the key or change a single toggle — there is no partial-edit path.

## Troubleshooting

> **Note.** The auth-key prompt only checks that the value is non-blank — it does not validate the `tskey-` prefix and makes no live call to Tailscale's API. A mistyped or already-revoked key is accepted silently and only surfaces as a failure when `pks vm tailscale` later runs `tailscale up` on the target VM.

- **Credentials are global, not per-project.** They're stored with `global: true` in `~/.pks-cli/settings.json`, so one Tailscale identity and auth key is shared across every VM and every `pks` project on the machine.
- **Running `init` again does nothing until you pass `--force`.** If a key is already stored, the command never re-prompts — not even to change one toggle, such as turning off exit-node advertising.
- **`pks tailscale init` never touches the Tailscale daemon or network.** Nothing is installed, authenticated against Tailscale's servers, or joined to a tailnet until you separately run `pks vm tailscale <vm-name>`.
- **The exit-node toggle only stores a preference.** After the VM actually joins via `pks vm tailscale`, you still have to approve it as an exit node in the Tailscale admin console (Machines → … → Edit route settings) — `pks tailscale init` gives no signal that this approval step is pending, since it runs before any VM exists.
- **Enabling accept-routes or advertise-exit-node has a downstream side effect.** When either toggle was enabled here, `pks vm tailscale` additionally enables IPv4/IPv6 forwarding on the VM (`/etc/sysctl.d/99-tailscale.conf`) as a consequence of the preference you set during `init`.

## See also

- [pks](/tools/pks) — the CLI's full command surface and installation paths
- [pks vm tailscale](/tools/pks/vm/tailscale) — the command that reads these credentials and runs `tailscale up` on a provisioned VM
