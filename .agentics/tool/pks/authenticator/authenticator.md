---
title: "pks authenticator"
description: "Enroll and check the local TOTP second factor that gates sensitive pks actions, with full command reference and troubleshooting."
tags: [reference, cli, auth, security]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks authenticator <command>"
examples:
  - command: "pks authenticator init"
    description: "Enroll a TOTP second factor for the first time"
  - command: "pks authenticator status"
    description: "Check whether a factor is enrolled"
---

`pks authenticator` manages the local TOTP (time-based one-time password) second factor that `pks actions` and `IActionGuard` use to gate sensitive commands. State lives in a single file, `~/.pks-cli/authenticator.json`, written with restricted 0600 permissions.

## Overview

The store deliberately exposes no way to read back the seed or compute a current code — only enroll and verify-a-supplied-code. That asymmetry is the point: it lets the gate hold even against an agent that can run any `pks` subcommand as the same OS user, because the agent still cannot produce a valid code without the physical authenticator app. Verification is single-use per 30-second time step (replay-checked, cross-process file-locked) and rate-limited: 5 failed attempts trigger an exponential-backoff lockout that doubles from 30 seconds up to 1800 seconds.

- **Enroll once, from a trusted terminal.** `pks authenticator init` generates the secret and recovery codes and shows them exactly one time.
- **Check status anywhere.** `pks authenticator status` is read-only and safe to run from any context, including inside the sudo/agent path.
- **Gate the actions separately.** Enrolling a factor here does not itself lock anything down — `pks actions` decides which sensitive actions require a code.

## When to use it

Run `pks authenticator init` before turning on gating with `pks actions`, so there is a working second factor for `IActionGuard` to check against. Run `pks authenticator status` any time you want to confirm a factor is enrolled and see how many one-time recovery codes are left, without exposing the secret itself.

## Prerequisites

- **A real interactive terminal.** `init` refuses to run with redirected stdin or stdout — the secret and recovery codes must never land in a captured log or pipe.
- **A non-sudo, non-container invocation.** `init` refuses to run when `SUDO_USER` or `SUDO_UID` is set, on the theory that an in-container coding agent could otherwise capture the freshly generated secret from `pks`'s own stdout.
- **An authenticator app or code generator** ready to scan the printed setup URI or accept the base32 secret, since the enrollment does not complete until you type back a live 6-digit code.

## Synopsis

```text
pks authenticator <command>
```

```text
init      Enroll or re-enroll a TOTP second factor
status    Show whether a factor is enrolled and recovery codes remaining
```

Neither command takes options or arguments — `AuthenticatorSettings` is an empty settings base with no `[CommandOption]` or `[CommandArgument]` members.

## init

Enrolls a brand-new TOTP secret — or re-enrolls, replacing an existing one — and prints the otpauth setup URI, the raw base32 secret, and a set of one-time recovery codes. Nothing is persisted to `~/.pks-cli/authenticator.json` until you prove you captured the secret by typing back a live 6-digit code generated from it, checked against a +/-1 time-step window.

If a factor is already enrolled, re-enrolling first shows a confirmation warning that it will replace the existing factor and invalidate the old recovery codes, then requires passing the `authenticator.write` action guard — a current TOTP or recovery code — before proceeding. Denial aborts with no changes.

```bash
pks authenticator init
```

You should see a panel with the setup URI, the base32 secret, and the recovery codes, followed by a prompt for a live code. Entering a valid code writes `~/.pks-cli/authenticator.json` (mode 0600) and confirms enrollment; entering a wrong code aborts with "nothing was saved" and exit code 1.

> **Do not commit.** The secret and recovery codes shown by `init` are the only time they are ever displayed — `TotpSeedStore` has no read-back API. Store them in your password manager immediately.

## status

Read-only check of whether a TOTP second factor is currently enrolled and, if so, how many of its one-time recovery codes are still unused. Never reveals the secret or a computable code — it is purely a status/audit view.

```bash
pks authenticator status
```

If nothing is enrolled, you'll see `No authenticator enrolled. Sensitive actions run without a second factor.` and the command exits 0 — this is a normal, unlocked state, not a fault. If a factor is enrolled, you'll see a two-row table: `Second factor: TOTP enrolled` and `Recovery codes left: N`. Both branches recommend `pks authenticator init` as the follow-up to enroll or re-enroll.

## Troubleshooting

> **Note.** `pks authenticator init` prints "Enrollment can't run via sudo inside the container" when `SUDO_USER` or `SUDO_UID` is set. Run `docker exec -it -u pks <container> pks authenticator init` from the Docker host instead, not from inside the container as the agent.

If `init` refuses to run because stdin or stdout is redirected, invoke it directly from a terminal rather than through a script, pipe, or CI runner. If you already have an enrolled factor and want to re-enroll but the guard keeps denying you, you need a current TOTP or recovery code from the *existing* enrollment — there is no bypass. There is currently no `pks authenticator reset` or `disable` command reachable from the CLI; clearing enrollment means deleting `~/.pks-cli/authenticator.json` by hand.

## See also

- [pks](/tools/pks) — command families and how the authenticator fits into the broader toolbelt
