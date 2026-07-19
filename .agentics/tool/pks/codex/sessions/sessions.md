---
title: "Manage Codex sessions through Foundry"
description: "Fork, archive, unarchive, and delete Codex sessions over the pks loopback passthrough — the session lifecycle verbs, what pks contributes, and what stays native Codex behavior."
tags: [how-to, codex, sessions, foundry]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks codex [options] <fork|archive|unarchive|delete> [ARGS]"
examples:
  - command: "pks codex fork"
    description: "Branch a Codex session through the Foundry passthrough"
  - command: "pks codex archive"
    description: "Archive a session so it stops appearing in the active list"
  - command: "pks codex unarchive"
    description: "Restore a previously archived session"
  - command: "pks codex delete"
    description: "Permanently delete a Codex session — no pks-side undo"
---

`fork`, `archive`, `unarchive`, and `delete` are the session-lifecycle verbs of the `pks codex` group. All four are forwarded to the real `@openai/codex` CLI through the same authenticated loopback passthrough that `run` and `resume` use. pks contributes the Foundry endpoint and the Entra token; the session store, the semantics, and the on-disk effects belong entirely to Codex.

## 1. The lifecycle in one picture

A Codex session starts with [`run`](/tools/pks/codex/run) and is reopened with [`resume`](/tools/pks/codex/resume). The four verbs on this page move it around from there:

| Verb | What it does to a session |
|---|---|
| `fork` | Branches it, so one starting point can carry two divergent continuations. |
| `archive` | Moves it out of the active set without destroying it. |
| `unarchive` | Reverses `archive` and returns it to the active set. |
| `delete` | Removes it permanently. |

`fork`, `archive`, and `unarchive` are all recoverable. `delete` is not — see [step 5](#5-delete-a-session).

## 2. Prerequisites

- **A Foundry login** — `pks foundry init`, with a resource endpoint selected. Every one of these subcommands runs that check before anything else and exits 1 with a hint if it fails.
- **The upstream Codex CLI on PATH** — `npm i -g @openai/codex`. Missing binary exits 127.
- **A session Codex itself can see.** pks keeps no session store. If the local Codex CLI cannot resolve the session, neither can pks.

## 3. Fork a session

```bash
pks codex fork
```

Branches an existing session so you can explore a second direction without disturbing the first. Any native arguments Codex's own `fork` accepts — a session ID, for example — follow the subcommand and are forwarded verbatim:

```bash
pks codex fork 019f3e01-b0c1-7bf2-b1d8-d0befe7232fd
```

pks prepends its own launch flags to the invocation, so the fork is created against the resolved Foundry deployment using the normal [deployment resolution order](/tools/pks/codex/reference#deployment-resolution-order).

## 4. Archive and unarchive

```bash
pks codex archive
pks codex unarchive
```

Archiving takes a session out of the active list; unarchiving puts it back. Nothing is destroyed either way, so this is the safe way to clear a cluttered session list.

pks still prepends `-m`, the provider override, and the reasoning-effort override to the invocation even though none of them are meaningful for an archive operation — the passthrough starts, the deployment is resolved, and the flags ride along harmlessly. That is a consequence of all subcommands sharing one implementation, not a signal that the model matters here.

## 5. Delete a session

```bash
pks codex delete
```

Permanently removes a session.

> **This is destructive.** There is no pks-side confirmation prompt, no dry run, and no undo. pks forwards the verb and tunnels authentication; whatever the real `codex delete` removes is gone. Reach for `archive` first if what you actually want is a shorter session list.

Any confirmation behavior you see comes from the Codex CLI itself, and may change with the upstream version you have installed. Do not rely on pks to catch a mistyped session ID.

## 6. Put pks flags before the subcommand

All four verbs accept the shared launch options, but placement matters. pks recovers native arguments from the raw process arguments: it finds `codex` in the argument list, finds the subcommand name after it, and forwards **everything that follows the subcommand** to the real binary untouched.

```bash
# Unambiguous: pks parses -m, and only the session ID reaches codex.
pks codex -m gpt-5.6-sol archive 019f3e01-b0c1-7bf2-b1d8-d0befe7232fd
```

Putting a pks flag after the subcommand means it is both bound by pks and forwarded to Codex, which is rarely what you want. Keep pks flags on the left of the verb and native arguments on the right.

## 7. Verify the environment first

```bash
pks codex --print-env
```

Runs the passthrough in the foreground and prints the launch command with the resolved deployment and port, without starting Codex. Useful for confirming which Foundry deployment a lifecycle command would run against before you delete anything. Stop it with Ctrl-C.

## Options

Every subcommand on this page accepts the same pks-level flags.

| Flag | Default | Description |
|---|---|---|
| `-m, --model <name>` | Configured deployment, else `gpt-5-codex` | Foundry deployment name, for example `gpt-5.6-sol`. |
| `-e, --reasoning-effort <level>` | Configured value, else `medium` | Reasoning effort: `none`, `low`, `medium`, `high`, `xhigh`, or `default` (omits the `-c model_reasoning_effort` override and lets Codex use its own built-in default). |
| `-p, --port <port>` | Configured value, else `8788` | Loopback port for the passthrough. |
| `--print-env` | `false` | Run the passthrough in the foreground and print the launch command instead of starting Codex. |
| `--safe` | `false` | Keep Codex approval prompts and sandbox enabled. |

The positional `ARGS` are native `codex <subcommand>` arguments, forwarded verbatim.

## Troubleshooting

**Codex reports no such session.** The session store belongs to the Codex CLI. Confirm the ID with Codex directly; pks cannot list, recover, or undelete sessions.

**A pks flag reached Codex as well.** It was placed after the subcommand name. Move it before the verb — see [step 6](#6-put-pks-flags-before-the-subcommand).

**Exit code 1 pointing at `pks foundry init`.** Authentication or resource selection is missing.

**Exit code 127.** The `codex` binary is not on PATH. Install `@openai/codex`, or run with `--print-env` and launch it yourself.

**Port already in use.** pks falls back to a free port with a yellow warning rather than failing.

## See also

- [pks codex](/tools/pks/codex) — group overview and mental model
- [Resume a Codex session through Foundry](/tools/pks/codex/resume) — reopening a session before you branch or archive it
- [Launch the Codex CLI on Foundry](/tools/pks/codex/run) — starting a fresh session
- [How the Foundry passthrough works](/tools/pks/codex/passthrough) — proxy internals, config files, and the failure log
- [pks codex CLI reference](/tools/pks/codex/reference) — every subcommand, flag, and environment variable
