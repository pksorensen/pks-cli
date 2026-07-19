---
title: "pks claude scaleway"
description: "Run Claude Code against Scaleway-hosted Mistral, Qwen, and other open models, or pick a first-party Anthropic model, through one local proxy."
tags: [how-to, scaleway, proxy, claude-code]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks claude scaleway [model] [claudeArgs] [options]"
---

`pks claude scaleway` runs the real Claude Code agent loop against any model in the Scaleway serverless catalog. Two narrowed siblings, `mistral` and `qwen`, are the same command with the picker restricted to one family. A fourth sibling, `anthropic`, launches a first-party Claude model with no proxy at all.

## Prerequisites

- **The `claude` CLI on your PATH.** All four commands launch it as a child process.
- **A completed `pks scaleway init`.** Stores your Scaleway secret key. Required by `scaleway`, `mistral`, and `qwen`; not by `anthropic`. Without it those three exit `1` with that instruction.

## How the translation works

pks hosts a local proxy that accepts the Anthropic Messages API inbound — what Claude Code calls through `ANTHROPIC_BASE_URL` — and relays each request to the OpenAI Chat Completions API that Scaleway exposes, streaming the reply back as Anthropic server-sent events. Model reasoning surfaces as Claude thinking blocks unless `--no-thinking` is passed.

As with `codex`, `ANTHROPIC_API_KEY` is removed from the child process environment so a host Anthropic key cannot take over the routing, and the launch is preflighted against the chosen model before Claude Code starts.

## 1. Pick a model interactively

```bash
pks claude scaleway
```

A picker lists the full Scaleway catalog. Choose one and Claude Code starts against it.

## 2. Launch a specific model

```bash
pks claude scaleway qwen3.5-397b-a17b
```

An unrecognized model id is still accepted and passed through verbatim. That lets brand-new Scaleway models work before the local catalog knows about them — and it means a typo is only caught at the preflight HTTP call, not by the argument parser.

## 3. Narrow to one family

```bash
pks claude mistral
pks claude qwen
```

`mistral` restricts the picker and default to the Mistral and Devstral family; `qwen` to the Qwen family. Mechanics, flags, and failure modes are identical to `scaleway`.

## 4. Use a first-party Anthropic model

```bash
pks claude anthropic
```

This is the no-proxy sibling. It launches `claude --model <id>` using whatever real Anthropic authentication the `claude` CLI already has. The picker offers `claude-opus-4-8`, `claude-sonnet-4-6`, and `claude-haiku-4-5` from a hardcoded list, or pass a model id directly. It takes no flags, and it does not strip `ANTHROPIC_API_KEY` — it is meant to use your real Anthropic environment as-is.

## 5. Wire the proxy manually

```bash
pks claude scaleway --print-env
```

Runs the proxy in the foreground and prints the `ANTHROPIC_` exports instead of launching Claude Code.

## Options

These apply to `scaleway`, `mistral`, and `qwen`.

| Argument | Required | Description |
|---|---|---|
| `model` | no | Model id to use. Omit to choose interactively. |
| `claudeArgs` | no | Extra arguments passed through to the `claude` CLI. |

| Flag | Default | Description |
|---|---|---|
| `-p`, `--port <PORT>` | random free port | Port for the local proxy. |
| `--print-env` | — | Run the proxy in the foreground and print `ANTHROPIC_` exports instead of launching Claude Code. |
| `--no-thinking` | — | Do not surface model reasoning as Claude thinking blocks. |

`pks claude anthropic` takes the same two arguments and no flags.

## Verify

```bash
pks claude scaleway
```

The picker appears, the preflight completes, and Claude Code opens against the selected model.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Exit `1` naming `pks scaleway init` | No stored Scaleway secret key. Run `pks scaleway init`. |
| A model was chosen without prompting | In a non-interactive session with no model argument, the command silently picks the catalog or family default. Pass the model id explicitly in CI and scripts. |
| Preflight fails on a model id you typed | Unrecognized ids pass through verbatim by design. Check the spelling against the Scaleway catalog. |
| `pks claude anthropic` picked opus without asking | With no model argument in a non-interactive session it defaults to the first entry, `claude-opus-4-8`. Pass the id you want. |
| A newly released Claude model is missing from the `anthropic` picker | The list is a hardcoded array in source and does not query Anthropic. Pass the model id as an argument. |

## See also

- [pks claude codex](/tools/pks/claude/codex) — the same pattern against Azure AI Foundry
- [pks claude usage](/tools/pks/claude/usage) — compare what a session actually cost
- [pks claude reference](/tools/pks/claude/reference) — every command and flag in the branch
