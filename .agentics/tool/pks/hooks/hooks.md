---
title: "pks hooks"
description: "Wire pks into Claude Code's hook system: register handlers for seven lifecycle events and block a turn from ending when your lint command fails."
tags: [reference, cli, claude-code, quality-gate]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks hooks <command> [options]"
examples:
  - command: "pks hooks init"
    description: "Register pks handlers in this project's Claude settings"
  - command: "pks hooks"
    description: "Configure the lint command the Stop hook runs"
  - command: "pks hooks list"
    description: "Show the hook events pks can handle"
  - command: "pks hooks init --scope user"
    description: "Register handlers globally for every project"
---

`pks hooks` connects pks to Claude Code's hook system — the callbacks Claude Code fires at fixed points in an agent turn. One command writes the wiring, one interactive menu configures the only handler that currently enforces anything, and the remaining subcommands are the handlers themselves.

## Overview

`pks hooks init` merges a `hooks` block into a Claude Code `settings.json`. That block tells Claude Code to shell out to `pks hooks <event>` at seven lifecycle points: PreToolUse, PostToolUse, UserPromptSubmit, Stop, Notification, SubagentStop, and PreCompact. Each of those `pks hooks <event>` subcommands is the handler Claude Code invokes — it reads the event payload from stdin and, for the decision-carrying events, writes a JSON verdict on stdout.

- **One handler is meant to enforce policy, but currently can't.** `pks hooks stop` is supposed to run a lint command you configure and block Claude Code from ending its turn when that command exits non-zero — but the configured command is never persisted to disk (see [Block a turn on a failing lint](/tools/pks/hooks/stop)), so it doesn't survive into the separate process Claude Code actually invokes for each Stop event.
- **The tool-call and prompt handlers are pass-through.** `pre-tool-use`, `post-tool-use`, and `user-prompt-submit` read their payload and always allow the action.
- **Three handlers are debug scaffolding.** `notification`, `subagent-stop`, and `pre-compact` dump what they receive and exit.

## What you get

- **A lint gate on turn completion — not yet working.** The intent is to set a lint command once and have Claude Code refuse to finish a turn until it passes, with the failure output handed back so the agent can fix it. Today the configured command doesn't persist across processes, so this doesn't actually happen — see the known limitation on [Block a turn on a failing lint](/tools/pks/hooks/stop).
- **Non-destructive settings merge.** `init` merges into an existing `hooks` object instead of replacing it, so hook entries you already wrote survive.
- **Legacy key migration.** `init` detects the old camelCase hook keys an earlier pks version wrote (`preToolUse`, `stop`) and rewrites them to the PascalCase names Claude Code expects.
- **Path-stable wiring.** The command string written into `settings.json` is resolved from the running process, so the hook works whether or not `pks` is on `PATH`.
- **Three settings scopes.** Write to the user-global file, the current project, or a local `.claude` folder.

## How it fits together

There are two halves. The **setup half** is what you run by hand: `pks hooks init` writes the wiring, and bare `pks hooks` opens a menu that stores the lint command. The **runtime half** is what Claude Code runs: the leaf event commands, invoked with the event payload piped to stdin. You never run those yourself except to inspect a payload.

The two halves are independent, and both are required. Storing a lint command with no `Stop` entry in `settings.json` does nothing, because nothing invokes the handler. Registering the `Stop` entry with no lint command configured also does nothing, because the handler returns immediately. The menu closes that gap: when you enable a lint command in a project whose `settings.json` doesn't yet reference `pks hooks stop`, it registers the Stop hook for you.

In practice only the `Stop`-entry half survives today: the lint command the menu "stores" is never written to disk, so it's gone as soon as that `pks hooks` process exits — see the known limitation on [Block a turn on a failing lint](/tools/pks/hooks/stop).

- **Setup half:** `init` and the interactive menu — run by you, interactive, writes files.
- **Runtime half:** the seven event handlers — run by Claude Code, stdin-driven, silent by design.

## Commands

`init` · `list` · `pre-tool-use` · `post-tool-use` · `user-prompt-submit` · `stop` · `notification` · `subagent-stop` · `pre-compact`

Every subcommand shares the same three options. Full flag and behavior detail is on the [pks hooks reference](/tools/pks/hooks/reference).

## Next steps

- [Register pks hooks in a project](/tools/pks/hooks/init) — run `init`, choose a scope, and confirm what landed in `settings.json`
- [Block a turn on a failing lint](/tools/pks/hooks/stop) — configure the quality gate and see what the agent gets back
- [Hook events and their handlers](/tools/pks/hooks/events) — what each of the seven events does today, including the three debug handlers
- [pks hooks reference](/tools/pks/hooks/reference) — synopsis, per-command behavior, and the shared option table
