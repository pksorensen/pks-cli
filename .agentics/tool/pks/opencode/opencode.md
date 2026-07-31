---
title: "pks opencode"
description: "Run OpenCode directly against a Scaleway serverless model — one command, no proxy, no config file. Defaults to GLM 5.2."
tags: [how-to, opencode, scaleway, glm, openai-compatible]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks opencode [ARGS] [--model <id>]"
examples:
  - command: "pks opencode"
    description: "Start OpenCode on GLM 5.2 (the default) on Scaleway"
  - command: "pks opencode --model glm-5.2"
    description: "Explicitly select GLM 5.2"
  - command: "pks opencode run --format json \"Reply with exactly: OK\""
    description: "One-shot, non-interactive run — args pass through to the opencode CLI"
---

`pks opencode` launches the [OpenCode](https://opencode.ai) CLI directly against Scaleway's OpenAI-compatible serverless API. It is the no-proxy sibling of `pks claude scaleway`: where that command hosts a local Anthropic→OpenAI translation proxy for Claude Code, this one hands OpenCode a Scaleway provider and lets it call `api.scaleway.ai/v1` itself.

The default model is **GLM 5.2** (`glm-5.2`). Run `pks opencode` with no arguments and you are in.

## Prerequisites

- **The `opencode` CLI on your PATH.** Install it from [opencode.ai](https://opencode.ai/docs) — `npm install -g opencode-ai`. Without it, the command exits `127` with that instruction.
- **A completed `pks scaleway init`.** Stores your Scaleway secret key. Without it the command exits `1` with that instruction. If you have already authenticated for GPU or `pks claude scaleway` work, you are done.

## Why there is no proxy and no config file

OpenCode reads its provider list from a config file (`opencode.json`). Editing that file by hand is friction — and writing a Scaleway secret into it is a leak. `pks opencode` does neither:

1. It reuses the secret already stored by `pks scaleway init`.
2. It builds an inline provider config as JSON and passes it through the `OPENCODE_CONFIG_CONTENT` environment variable — OpenCode's runtime-override mechanism, which has the highest effective priority.
3. It passes the Scaleway secret through `PKS_SCALEWAY_API_KEY`, referenced inside the inline config as `{env:PKS_SCALEWAY_API_KEY}`.

The result: the provider registration is **process-local**. Your `opencode.json` stays untouched, and the secret is not duplicated into OpenCode configuration or placed on the command line.

## 1. Start an interactive session

```bash
pks opencode
```

OpenCode opens against `scaleway/glm-5.2`. The model id passed to OpenCode is always `scaleway/<model>`.

## 2. Pick the model explicitly

```bash
pks opencode --model glm-5.2
```

The default is already `glm-5.2`, so this is equivalent — but it documents intent. Any Scaleway serverless model id is accepted verbatim, so brand-new models work before any local catalog knows about them. A `scaleway/` prefix is stripped if present, so `--model scaleway/glm-5.2` and `--model glm-5.2` are identical.

## 3. Run non-interactively

```bash
pks opencode run --format json "Reply with exactly: OK"
```

Everything after the options is forwarded verbatim to the `opencode` CLI. Use `run`, `--format`, `--continue`, or any native OpenCode argument.

## The two-command setup

If you have ever run `pks scaleway init` (for GPU work, for `pks claude scaleway`, or otherwise), you are already at step 2:

```bash
pks scaleway init              # once — stores your Scaleway secret key
pks opencode --model glm-5.2   # every time after — launches OpenCode on GLM 5.2
```

## Options

| Argument | Required | Description |
|---|---|---|
| `ARGS` | no | Additional arguments passed through to the `opencode` CLI (e.g. `run --format json "..."`). |

| Flag | Default | Description |
|---|---|---|
| `-m`, `--model <id>` | `glm-5.2` | Scaleway serverless model id. A `scaleway/` prefix is accepted and stripped. |

## Verify

```bash
pks opencode --model glm-5.2
```

OpenCode launches and its model selector shows `scaleway/glm-5.2`. Send a prompt and confirm the reply.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Exit `1` naming `pks scaleway init` | No stored Scaleway secret key. Run `pks scaleway init`. |
| Exit `127` naming the opencode CLI | OpenCode is not on PATH. Install it: `npm install -g opencode-ai`. |
| Model not found at runtime | Unrecognized ids pass through verbatim by design. Check the spelling against the Scaleway serverless catalog. The endpoint is `https://api.scaleway.ai/v1`. |
| Your `opencode.json` was not modified | This is intentional. The provider config is injected per-process via `OPENCODE_CONFIG_CONTENT` and never written to disk. |

## See also

- [pks claude scaleway](/tools/pks/claude/scaleway) — the proxy-based sibling for Claude Code, covering the rest of the Scaleway catalog
- [pks scaleway](/tools/pks/scaleway) — authenticate against Scaleway with a static API key pair
- [pks codex](/tools/pks/codex) — run the upstream Codex CLI against an Azure AI Foundry deployment
