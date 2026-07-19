---
title: "pks claude codex"
description: "Run the Claude Code TUI against a Codex or GPT-5.x deployment on Azure AI Foundry through a local proxy that translates the Anthropic Messages API."
tags: [how-to, foundry, proxy, claude-code]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks claude codex [claudeArgs] [options]"
---

`pks claude codex` runs the real Claude Code agent loop against a Codex or GPT-5.x deployment on Azure AI Foundry. You keep the Claude Code interface and tooling; the model behind it is one of yours on Foundry.

## Prerequisites

- **The `claude` CLI on your PATH.** The command launches it as a child process.
- **A completed `pks foundry init`.** Azure AI Foundry OAuth plus a selected resource. Without it the command exits `1` with that instruction.
- **A deployment name you can reach.** For example `gpt-5.1-codex` or `gpt-5.3-codex`.

## How the translation works

pks hosts an in-process ASP.NET Core proxy on `127.0.0.1`. Inbound it speaks the Anthropic Messages API — exactly what Claude Code calls through `ANTHROPIC_BASE_URL`. Outbound it issues Azure OpenAI Responses API calls against your Foundry deployment, and streams the reply back as Anthropic server-sent events. Codex reasoning summaries are surfaced as Claude thinking blocks unless you pass `--no-thinking`.

Two safety behaviors matter. The proxy validates a per-run random `ANTHROPIC_AUTH_TOKEN` on every inbound request. And `ANTHROPIC_API_KEY` is stripped from the child process environment, so a real Anthropic key on your host cannot silently take over the routing.

Before launching, the command preflights the deployment with a small real request. A wrong deployment name or an auth problem surfaces here as a clear error rather than as a vague Claude Code failure later.

## 1. Launch on the default deployment

```bash
pks claude codex
```

Claude Code starts as usual. Requests go to Foundry.

## 2. Pick a deployment

```bash
pks claude codex --model gpt-5.1-codex
```

Deployment selection falls back in order: `--model` if given, then your stored Foundry default model but only when its name contains `gpt` or `codex`, then the literal `gpt-5.5`. A mismatch shows up as a preflight failure, not as a silently wrong model.

## 3. Tune reasoning

```bash
pks claude codex --reasoning-effort high
pks claude codex --no-thinking
```

`--reasoning-effort` accepts `none`, `low`, `medium`, `high`, and `xhigh`. `--no-thinking` stops reasoning summaries from being rendered as Claude thinking blocks.

## 4. Wire it manually instead

```bash
pks claude codex --print-env
```

This runs the proxy in the foreground and prints the `ANTHROPIC_` exports instead of launching Claude Code. It does not exit after printing — it keeps serving until Ctrl+C. Use it when you want to drive the proxy from another shell or a script.

## 5. Pin the port

```bash
pks claude codex --port 8123
```

The proxy binds a random free port by default.

## Options

| Argument | Required | Description |
|---|---|---|
| `claudeArgs` | no | Extra arguments passed through to the `claude` CLI. |

| Flag | Default | Description |
|---|---|---|
| `-m`, `--model <NAME>` | stored Foundry default (if codex-like), else `gpt-5.5` | Foundry deployment name. |
| `-e`, `--reasoning-effort <LEVEL>` | `medium` | One of `none`, `low`, `medium`, `high`, `xhigh`. |
| `-p`, `--port <PORT>` | random free port | Port for the local proxy. |
| `--print-env` | — | Run the proxy in the foreground and print `ANTHROPIC_` exports instead of launching Claude Code. |
| `--no-thinking` | — | Do not surface Codex reasoning summaries as Claude thinking blocks. |

## Verify

```bash
pks claude codex --model gpt-5.1-codex
```

The preflight completes and Claude Code opens. A preflight error names the deployment or the authentication problem.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Exit `1` naming `pks foundry init` | Not authenticated to Azure AI Foundry, or no endpoint configured. Run `pks foundry init`. |
| Preflight fails with an unknown deployment | The fallback chain resolved to `gpt-5.5` or to a stored default that does not exist on your resource. Pass `--model` explicitly. |
| A small charge appears on every launch | The preflight is a real request against the Foundry Responses endpoint. That is by design. |
| `--print-env` appears to hang | It runs the proxy in the foreground until Ctrl+C. That is the intended behavior. |
| Requests go to Anthropic instead of Foundry | Unlikely — `ANTHROPIC_API_KEY` is removed from the child environment. Confirm you launched through `pks claude codex` and not a bare `claude`. |

## See also

- [pks claude scaleway](/tools/pks/claude/scaleway) — the same proxy pattern against Scaleway-hosted models
- [Devcontainer and inline sessions](/tools/pks/claude/devcontainer-sessions) — the inline Foundry path on the default command
- [pks claude reference](/tools/pks/claude/reference) — every command and flag in the branch
