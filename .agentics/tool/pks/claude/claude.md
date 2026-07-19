---
title: "pks claude"
description: "Launch Claude Code in a devcontainer, inline, or against Foundry and Scaleway models, and analyse your local session transcripts for cost, pace, and quota."
tags: [cli, claude-code, devcontainer, analytics]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks claude <command> [options]"
examples:
  - command: "pks claude"
    description: "Spawn a devcontainer and attach a Claude Code session"
  - command: "pks claude --inline"
    description: "Run Claude Code in the current shell, no container"
  - command: "pks claude usage"
    description: "Price every billed request from local transcripts"
  - command: "pks claude limits --json"
    description: "Session and week quota as machine-readable JSON"
  - command: "pks claude codex --model gpt-5.1-codex"
    description: "Drive Claude Code from an Azure AI Foundry deployment"
  - command: "pks claude backup"
    description: "Mirror ~/.claude to every registered rsync target"
---

`pks claude` is the branch of the pks CLI that starts Claude Code sessions and reads what those sessions left behind. It launches the real `claude` binary — in a devcontainer, in your current shell, or pointed at a non-Anthropic model provider — and it parses your local `~/.claude/projects/**/*.jsonl` transcripts into cost, pace, and quota numbers.

## Overview

The branch has one default command and eleven named subcommands. Running bare `pks claude` invokes the default: it spawns a devcontainer for the current project on a local Docker host or a registered remote SSH target, then attaches an interactive `claude --dangerously-skip-permissions` session over `docker exec`. Everything else hangs off that.

- **Launchers.** The default command, plus `codex`, `scaleway`, `mistral`, `qwen`, and `anthropic` — each starts the real Claude Code TUI against a different backend.
- **Local analytics.** `stats`, `usage`, and `limits` read files on your own machine. `stats` and `usage` never touch the network at all.
- **Utilities.** `backup` mirrors `~/.claude` to remote targets; `managed-settings` renders an enterprise policy file.

## What you get

- **Containerized sessions, local or remote.** Spawn a devcontainer for the project and attach Claude Code to it. Pass `--ssh-target` to do the same on a registered remote box, so the agent runs somewhere other than your laptop.
- **Container reuse with a real diff.** The default command detects an existing container for the project and compares three hashes — your host `.devcontainer`, the container's build-time label, and the live volume — then prompts to sync, discard, or resolve the conflict.
- **Model-provider swapping without hand-rolled proxies.** `codex` and the Scaleway family host an in-process proxy on `127.0.0.1` that speaks the Anthropic Messages API inbound and relays to Azure AI Foundry Responses or the OpenAI Chat Completions API that Scaleway exposes.
- **Cost and pace answers on demand.** `usage` prices every deduplicated billed request; `limits` reports SESSION and WEEK percentages, reset countdowns, and burn ratio as JSON you can poll from cron.
- **A mirror of your Claude state.** `backup` rsyncs the whole `~/.claude` directory to every target you registered with `pks rsync init`.

## How it fits together

Think of the branch in two halves that never talk to each other. The **launch** half always ends in the same place: the real `claude` CLI running as a child process. What differs is the environment it inherits — a devcontainer with forwarded credentials, your bare shell, or a shell whose `ANTHROPIC_BASE_URL` points at a proxy pks started moments earlier. That is why every launcher needs `claude` on your PATH, and why a broken Anthropic login breaks all of them the same way.

The **analysis** half never launches anything except in one case. `stats` and `usage` are pure file readers over `~/.claude/projects/`. `limits` is the exception: there is no file holding your quota, so it spawns a detached tmux session running `claude`, types `/usage`, captures the rendered panel, and parses it. That makes `limits` the only analytics command that costs wall-clock time and, on its fallback path, a real billed request.

Two things at a glance:

- **Launchers** need `claude`, and often Docker, tmux, Foundry, or Scaleway credentials.
- **Analytics** need transcripts that already exist, and nothing else.

## Commands

`stats` · `usage` · `limits` · `session-usage` · `codex` · `scaleway` · `mistral` · `qwen` · `anthropic` · `backup` · `managed-settings`

| Command | Status | What it does |
|---|---|---|
| `pks claude` (default) | stable | Spawns a devcontainer locally or over SSH and attaches an interactive Claude Code session. `--inline` skips the container. |
| [`pks claude stats`](/tools/pks/claude/stats) | stable | Activity heatmap, streaks, token totals, and response-time trends from local transcripts. |
| [`pks claude usage`](/tools/pks/claude/usage) | stable | Deduplicates and prices billed requests; hourly and daily cost charts plus a top-models table. |
| [`pks claude limits`](/tools/pks/claude/limits) | stable | SESSION and WEEK limit percentages, reset times, and pace, as a table or JSON. |
| `pks claude session-usage` | stable | A second registered name for the identical command class as `limits`. |
| [`pks claude codex`](/tools/pks/claude/codex) | beta | Claude Code against a Codex or GPT-5.x deployment on Azure AI Foundry, via a local translating proxy. |
| [`pks claude scaleway`](/tools/pks/claude/scaleway) | beta | Claude Code against any model in the Scaleway serverless catalog. |
| `pks claude mistral` | beta | The Scaleway launcher narrowed to the Mistral and Devstral family. |
| `pks claude qwen` | beta | The Scaleway launcher narrowed to the Qwen family. |
| `pks claude anthropic` | beta | Launches `claude --model <id>` directly against a first-party Anthropic model. No proxy. |
| [`pks claude backup`](/tools/pks/claude/backup) | stable | `rsync -avz --delete` of `~/.claude` to every registered rsync target. |
| [`pks claude managed-settings`](/tools/pks/claude/managed-settings) | beta | Renders a Claude Code `managed-settings.json` from registered marketplaces. |

There is no literal `spawn` subcommand name. `claude.SetDefaultCommand<ClaudeSpawnCommand>()` means bare `pks claude [PROJECT_PATH]` routes straight into the spawn settings.

## Next steps

- [Devcontainer and inline sessions](/tools/pks/claude/devcontainer-sessions) — the default command, container reuse, remote spawns, and the post-quit prompts
- [pks claude reference](/tools/pks/claude/reference) — every command, flag, argument, and environment variable in the branch
- [pks claude usage](/tools/pks/claude/usage) — how requests are deduplicated and priced
- [pks claude limits](/tools/pks/claude/limits) — quota and pace as pollable JSON
- [pks claude codex](/tools/pks/claude/codex) — Claude Code on Azure AI Foundry
- [pks claude scaleway](/tools/pks/claude/scaleway) — Mistral, Qwen, and the rest of the Scaleway catalog
