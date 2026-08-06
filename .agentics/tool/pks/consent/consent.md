---
title: "Consent"
description: "Out-of-band approval for scoped, irreversible actions — a caller files a request, a human approves it elsewhere, and the resulting grant is bound to an exact target list."
tags: [reference, cli, security, consent]
category: security
status: stable
author: Poul Kjeldager
component: pks
usage: "pks consent <command> [options]"
examples:
  - command: "pks consent list"
    description: "See what is waiting for a decision"
  - command: "pks consent show a1b2c3d4"
    description: "Review every target a request would touch"
  - command: "pks consent approve a1b2c3d4"
    description: "Grant it — single-use, 10 minutes, bound to that exact target set"
  - command: "pks consent deny a1b2c3d4 --reason 'wrong share'"
    description: "Close a request out"
---

`pks consent` is how a human authorises something a caller could not authorise for itself. A gated command files a request and stops; you review it and approve it somewhere the caller cannot reach; the caller re-runs and succeeds exactly once.

## Overview
The two-factor action guard ([Actions](/tools/pks/actions), [Authenticator](/tools/pks/authenticator)) works by challenging for a TOTP code at the moment of the action. That assumes two things an agent breaks: that there is a terminal to type into, and that a prompt in that terminal means a human answered it. An agent has neither property — it can drive a TTY perfectly well (tmux), and it frequently has no TTY at all.

`pks consent` is the second route. The gated command resolves what it is about to do, files a request describing it, and exits without doing it. The request lands in `~/.pks-cli/consent.json` (0600). A human — from their own terminal, or from the Docker host, but never through the container's `sudo -u pks` path — reviews the request and approves it. That produces a **grant**: single-use, time-boxed, and bound to a fingerprint of the exact items the action would touch.

- **Discover** what is waiting with `list`.
- **Review** the full target list with `show` before deciding.
- **Decide** with `approve` or `deny`.
- **Pre-request** with `request`, when a caller wants the decision in hand before it starts work.

## What you get
- **Approval bound to targets, not to patterns.** A grant approved for three files is rejected the moment the resolved set differs. Widening the blast radius after approval invalidates the grant instead of inheriting it.
- **Grants that expire.** Single-use and 10 minutes by default. "Yes, delete that" never decays into a standing permission.
- **A boundary the caller cannot cross.** `approve` refuses the `sudo` path — the only route an in-container agent has to the credential-bearing `pks` user — and requires the second factor whenever one is enrolled.
- **A useful denial.** The gated command's error carries the request id and the exact command to run, so the failure is actionable rather than a dead end.

## How it fits together
`IActionGuard` gates an action. When the request carries a `Resource` and a resolved `Targets` list, the guard gains this second route:

1. **A matching grant exists** → it is spent, the action proceeds. Matching means same action, same resource, and same target fingerprint.
2. **A second factor is enrolled and there is a terminal** → normal TOTP challenge, no consent request involved.
3. **Neither** → the guard files a consent request and throws. The action does not run.

Most actions are *fail-open* when nothing is enrolled — two-factor is opt-in, so an un-enrolled pks behaves as it always did. Irreversible actions opt out of that leniency and are marked **fail-closed** in the catalog: they always need an explicit yes from somewhere. `storage.delete` is the first of these.

The fingerprint is what makes approval meaningful. It is a SHA-256 digest of the normalised, sorted target paths, computed when the request is filed and re-checked when the grant is spent. Approving a request means approving *that list* — not the glob, not the directory, not whatever the directory happens to contain a minute later.

## Commands
`list` · `show` · `approve` · `deny` · `request`. Full flags and examples below.

## Reference

### pks consent list

Lists requests awaiting a decision, plus approved grants that have not yet been spent. Read-only.

```text
pks consent list [options]
```

| Flag | Default | Description |
|---|---|---|
| `--all` | `false` | Include resolved requests: consumed, denied, and expired. |
| `--json` | `false` | Machine-readable output. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

```bash
pks consent list
```

Prints a table of id, status, action, resource, target count, and the relevant deadline — the approval deadline while pending, the grant deadline once approved.

### pks consent show

Prints one request in full, **including every target it would touch**. This is the command to run before approving anything: the approve prompt shows the same list, but reviewing it separately costs nothing and commits to nothing.

```text
pks consent show <id> [options]
```

| Argument | Required | Description |
|---|---|---|
| `<id>` | yes | Consent request id (case-insensitive). |

| Flag | Default | Description |
|---|---|---|
| `--json` | `false` | Machine-readable output, including the target fingerprint. |

### pks consent approve

Turns a pending request into a grant.

```text
pks consent approve <id> [options]
```

| Argument | Required | Description |
|---|---|---|
| `<id>` | yes | Consent request id. |

| Flag | Default | Description |
|---|---|---|
| `--uses <int>` | `1` | How many times the grant may be spent. |
| `--minutes <int>` | `10` | How long the grant stays usable. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

```bash
pks consent approve a1b2c3d4
```

Prints the request and its full target list, asks for confirmation (defaulting to **No**), challenges the enrolled second factor, and then issues a single-use grant valid for ten minutes.

> **What actually holds here.** Interactivity is *not* the boundary — an agent can drive an interactive terminal. Two things do the work. First, `approve` refuses to run when `SUDO_USER`/`SUDO_UID` is set, which is the in-container agent's only path to the `pks` user; approval must come from your own terminal or from the Docker host (`docker exec -it -u pks <container> pks consent approve <id>`). Second, when an authenticator is enrolled, a TOTP code from your phone is required — something the caller cannot produce regardless of what it can type. With nothing enrolled, approval rests on terminal trust alone; run `pks authenticator init` to close that gap.

### pks consent deny

Marks a request denied, with an optional reason recorded against it. Denied requests can never be consumed.

```text
pks consent deny <id> [--reason <text>]
```

### pks consent request

Files a request up front rather than discovering the gate by tripping it. Gated commands file their own requests, so this is only needed when a caller wants the human's decision in hand before starting work.

```text
pks consent request <action> --resource <text> --target <text> [--target <text> ...] [options]
```

| Argument | Required | Description |
|---|---|---|
| `<action>` | yes | Action id from the catalog, e.g. `storage.delete`. See [Actions](/tools/pks/actions). |

| Flag | Default | Description |
|---|---|---|
| `--resource <text>` | — | **Required.** What the action applies to, e.g. `azure-fileshare:acct/share`. |
| `--target <text>` | `[]` | **Required, repeatable.** A specific item the action will touch. Approval binds to this exact set. |
| `--summary <text>` | derived | One-line description shown to the approver. |
| `--minutes <int>` | `15` | How long the request stays approvable. |
| `--json` | `false` | Machine-readable output. |

Re-filing an identical request (same action, resource, and targets) returns the **existing** id rather than creating a second one, so a retrying caller does not spray new ids at the approver.

## Prerequisites
- Nothing, to file or list requests.
- An enrolled authenticator ([Authenticator](/tools/pks/authenticator)) to make `approve` require a code rather than terminal trust alone.

## Troubleshooting

**"Approval can't run via sudo inside the container."** — `approve` was invoked through the escalation path an agent has. Approve from your own terminal, or from the Docker host: `docker exec -it -u pks <container> pks consent approve <id>`.

**"Approval must run in an interactive terminal."** — stdin is redirected or the console reports no interactive capability. Approval is a decision, not a pipeline step.

**"Request '<id>' is expired, not pending."** — Requests stay approvable for 15 minutes by default. Re-run the original command to file a fresh one.

**The grant was approved but the command still fails.** — Almost always a target mismatch: the resolved set changed between filing and execution, so the fingerprint no longer matches. Check with `pks consent show <id>` against a fresh `--dry-run` of the original command. It can also mean the grant was already spent (`--uses` defaults to 1) or timed out (`--minutes` defaults to 10).

**A grant disappeared without being used.** — Approved grants expire on `--minutes` and are then reported as `expired`. Approve again with a longer window if the work genuinely takes longer.

## See also
- [Actions](/tools/pks/actions) — the catalog of gateable actions and which require two-factor.
- [Authenticator](/tools/pks/authenticator) — enrolling the TOTP factor `approve` challenges.
- [Storage](/tools/pks/storage) — `pks storage rm`, the first consumer of scoped consent.
- [pks](/tools/pks) — the full command surface.
