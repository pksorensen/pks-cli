---
title: "pks actions"
description: "Choose which sensitive pks operations demand a TOTP code before they run, from an interactive checkbox list backed by a local policy file."
tags: [reference, cli, security, auth]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks actions"
examples:
  - command: "pks actions"
    description: "Open the checkbox list and edit which actions require 2FA"
---

`pks actions` is the policy editor for pks-cli's two-factor action gate. It opens one interactive checkbox list — no arguments, no flags — and lets you decide which sensitive operations (VM lifecycle, cloud-credential writes, remote devcontainer spawn, `pks update`, SSH connect, cert writes, runner credential forwarding, and the gate's own control-plane actions) require a TOTP code before they execute.

## Overview
`pks actions` reads and writes `~/.pks-cli/actions.json`, the on-disk policy that `IActionGuard` consults every time a gated action runs elsewhere in the CLI. It does not enroll a factor and it does not perform or block any operation itself — it only edits the table.

- **Toggle, don't script.** The command is purely interactive (Spectre.Console `MultiSelectionPrompt`); there is no `--set` flag or JSON-in option, so it cannot be driven from CI.
- **Paired with enrollment.** `pks actions` decides *what* is gated. [pks authenticator](/tools/pks/authenticator) decides *whether* the gate is live at all — before a TOTP factor is enrolled, every gated action passes silently.
- **Self-protecting.** Saving a policy change is itself gated by the `policy.write` action, so once a factor is enrolled, nothing can silently widen the gate without a code.

## What you get
- **A single checkbox list** titled "Actions that require a two-factor code," one row per entry in the built-in action catalog, grouped and sorted by category then id.
- **Readable labels.** Each row reads `[Category] Display Name (action.id)`.
- **A results table on save.** A rounded-border table (`Action` / `Two-factor`) showing `required` (green) or `off` (dim) for every action after a successful write.
- **A no-op fast path.** If your selections match the current policy exactly, the command prints `No changes.` and exits `0` without touching the file or invoking the guard.

## How it fits together
`pks actions` and `pks authenticator` are two halves of the same system. `pks authenticator init` enrolls a local TOTP secret and writes `~/.pks-cli/authenticator.json`; `pks actions` edits `~/.pks-cli/actions.json`, the map of which action ids require that factor. Enforcement itself happens later, transparently, at each action's own call site (inside the VM provider, inside devcontainer spawn, and so on) via `IActionGuard.RequireAsync`.

A satisfied challenge is remembered only for the lifetime of one `pks` invocation — entering a code for one gated action in a session does not carry over to the next `pks` command.

- **Before enrollment:** every gated action passes silently (trust-on-first-use), even if you've already checked its box here.
- **After enrollment:** checked actions demand a code; unchecked actions run as before.

## Prerequisites
- **A TOTP authenticator app**, if you intend the policy to have any effect — enroll one with `pks authenticator init` before or after running `pks actions`. Running `pks actions` first is fine; the policy is inert until enrollment.
- **Write access to `~/.pks-cli/`** — the command reads and writes `actions.json` there.

## Usage

```text
pks actions
```

The command takes no arguments and no options — every input happens inside the interactive prompt. Space toggles a row; Enter submits.

```bash
pks actions
```

You'll see a checkbox list of every gateable action. Toggle the ones you want to require a code, press Enter, and (once a factor is enrolled) confirm the resulting policy change with a TOTP code.

## The action catalog
The catalog currently lists 13 gateable actions across 5 categories: Compute, Cloud, Devcontainer, Control plane, and Access. Most default to **required**; `vm.stop` and `vm.autoshutdown.write` default to **off**, since stopping a VM isn't destructive.

| Action id | Default |
|---|---|
| `vm.create` | required |
| `vm.start` | required |
| `vm.stop` | off |
| `vm.destroy` | required |
| `vm.autoshutdown.write` | off |
| `cloud.auth.write` | required |
| `devcontainer.spawn.remote` | required |
| `pks.update` | required |
| `policy.write` | required |
| `authenticator.write` | required |
| `ssh.connect` | required |
| `cert.write` | required |
| `runner.credential.forward` | required |

These defaults apply automatically to any action missing from `actions.json` — including a freshly reset or corrupted file (see [Troubleshooting](#troubleshooting)).

> **Note.** `devcontainer.spawn.remote` satisfies `vm.start` for the rest of the same CLI invocation once approved — approving one remote devcontainer spawn also clears the VM-start gate for that run. This composition is not persisted; it resets on the next invocation.

## Behavior on save
- **No changes selected:** prints `No changes.`, exits `0`, does not touch the file or invoke the guard.
- **Changes selected, gate satisfied (or nothing enrolled):** writes the full action→bool map to `~/.pks-cli/actions.json` (mode `0600`) and prints the results table.
- **Changes selected, gate challenged and failed or cancelled:** prints `Denied — changes discarded:` and returns exit code `1`. The on-disk policy is left untouched — the whole batch is discarded, not just the risky entries.

## Troubleshooting

**My toggle has no effect on anything.** No TOTP authenticator is enrolled yet. `IActionGuard.RequireAsync` treats every action as satisfied (trust-on-first-use) until one exists. Run `pks authenticator init`, then re-run `pks actions` to confirm the box is still checked.

**Saving asks for a code I can't provide, and I lose my edits.** A failed or cancelled `policy.write` challenge discards the entire batch of changes and exits `1`. Re-run `pks actions`, have your authenticator app ready, and confirm the code before it expires.

**My customization disappeared after an edit outside pks.** `~/.pks-cli/actions.json` is parsed best-effort: on a parse failure the CLI silently falls back to an empty policy, meaning every action reverts to its catalog default rather than erroring. Re-run `pks actions` and re-select your customizations.

**I need this in CI or a script.** There is no non-interactive form. `pks actions` is deliberately interactive-only; policy edits are an operator action, not an automated one.

## See also

- [pks authenticator](/tools/pks/authenticator) — enroll or check the TOTP second factor that makes this policy binding
- [pks vm](/tools/pks/vm) — the VM lifecycle commands gated by `vm.create`/`vm.start`/`vm.stop`/`vm.destroy`
- [pks ssh](/tools/pks/ssh) — the `ssh connect` command gated by `ssh.connect`
- [pks cert](/tools/pks/cert) — the certificate commands gated by `cert.write`
- [pks devcontainer](/tools/pks/devcontainer) — remote spawn, gated by `devcontainer.spawn.remote`
