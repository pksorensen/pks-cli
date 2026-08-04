---
title: "pks opencode"
description: "Run OpenCode on a configured model provider — one command, no proxy, no config file. GLM 5.2 on Scaleway by default, Kimi K3 on Moonshot one --model away."
tags: [how-to, opencode, scaleway, moonshot, glm, kimi, openai-compatible]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks opencode [ARGS] [--model <id>] [--provider <id>]"
examples:
  - command: "pks opencode"
    description: "Start OpenCode on GLM 5.2 (the default) on Scaleway"
  - command: "pks opencode --model kimi-k3"
    description: "Start OpenCode on Kimi K3 — the provider (Moonshot) is resolved automatically"
  - command: "pks opencode run --format json \"Reply with exactly: OK\""
    description: "One-shot, non-interactive run — args pass through to the opencode CLI"
---

`pks opencode` launches the [OpenCode](https://opencode.ai) CLI directly against a configured OpenAI-compatible provider API. It is the no-proxy sibling of `pks claude scaleway`: where that command hosts a local Anthropic→OpenAI translation proxy for Claude Code, this one hands OpenCode a provider registration and lets it call the provider's API itself.

The default model is **GLM 5.2** (`glm-5.2`) on Scaleway. Run `pks opencode` with no arguments and you are in. Pass `--model kimi-k3` and you are on **Kimi K3** via Moonshot instead — same command, no extra flags.

## Run pks your way

Use an installed `pks` command, run the NuGet tool directly with .NET 10, or run the npm package without installing it globally:

```commandtabs
{
  "tabs": [
    {
      "label": "pks",
      "command": "dotnet tool install --global pks-cli\npks scaleway init\npks opencode --model glm-5.2",
      "hint": "Installs the permanent pks command as a .NET global tool"
    },
    {
      "label": "dnx",
      "command": "dotnet dnx pks-cli --yes -- scaleway init\ndotnet dnx pks-cli --yes -- opencode --model glm-5.2",
      "hint": "Runs directly from NuGet · requires .NET 10"
    },
    {
      "label": "npx",
      "command": "npx @pks-cli/cli scaleway init\nnpx @pks-cli/cli opencode --model glm-5.2",
      "hint": "Runs directly from npm · requires Node.js"
    }
  ]
}
```

## The model picks the provider

You never tell `pks opencode` where a model lives. pks keeps a small provider catalog, and the model id resolves it:

| Model | Provider | API | Authenticate with |
|---|---|---|---|
| `glm-5.2` (default) | `scaleway` | `https://api.scaleway.ai/v1` | `pks scaleway init` |
| `kimi-k3` | `moonshot` | `https://api.moonshot.ai/v1` | `pks moonshot init` |
| Scaleway serverless catalog (Mistral, Qwen, Llama, gpt-oss…) | `scaleway` | `https://api.scaleway.ai/v1` | `pks scaleway init` |

- **Automatic.** `--model kimi-k3` selects Moonshot because Moonshot is the catalogued provider for that model. If only one configured provider offers the model, it is used.
- **Explicit, if you like.** A `moonshot/kimi-k3` prefix or `--provider moonshot` works too — but is redundant when the model is unambiguous.
- **Guarded.** A model pks doesn't know is rejected up front with the list of known models. A model whose provider you haven't authenticated with fails with the exact `pks <provider> init` to run. A `moonshot/…` prefix combined with a conflicting `--provider` is caught before OpenCode even starts. And if a model is ever offered by more than one configured provider, pks asks you to pick with `--provider`.

## Prerequisites

- **The `opencode` CLI on your PATH.** Install it from [opencode.ai](https://opencode.ai/docs) — `npm install -g opencode-ai`. Without it, the command exits `127` with that instruction.
- **One authenticated provider.** `pks scaleway init` for the Scaleway catalog (including the GLM 5.2 default), `pks moonshot init` for Kimi K3. Without the matching credential the command exits `1` and names the init command to run. If you have already authenticated for GPU or `pks claude scaleway` work, Scaleway is done.

## Why there is no proxy and no config file

OpenCode reads its provider list from a config file (`opencode.json`). Editing that file by hand is friction — and writing an API key into it is a leak. `pks opencode` does neither:

1. It reuses the credential already stored by `pks scaleway init` or `pks moonshot init`.
2. It builds an inline provider config as JSON and passes it through the `OPENCODE_CONFIG_CONTENT` environment variable — OpenCode's runtime-override mechanism, which has the highest effective priority.
3. It passes the API key through the provider's environment variable — `PKS_SCALEWAY_API_KEY` or `MOONSHOT_API_KEY` — referenced inside the inline config as `{env:…}`.

The result: the provider registration is **process-local**. Your `opencode.json` stays untouched, and the key is not duplicated into OpenCode configuration or placed on the command line.

## 1. Start an interactive session

```bash
pks opencode
```

OpenCode opens against `scaleway/glm-5.2`. The model id passed to OpenCode is always `<provider>/<model>`.

## 2. Pick the model explicitly

```bash
pks opencode --model kimi-k3
```

OpenCode opens against `moonshot/kimi-k3`. The provider is resolved from the catalog — no `--provider` needed. A provider prefix is accepted and stripped, so `--model moonshot/kimi-k3` is identical.

## 3. Run non-interactively

```bash
pks opencode run --format json "Reply with exactly: OK"
```

Everything after the options is forwarded verbatim to the `opencode` CLI. Use `run`, `--format`, `--continue`, or any native OpenCode argument.

## The two-command setup

For Kimi K3 on Moonshot:

```bash
pks moonshot init                # once — validates and stores your Moonshot API key
pks opencode --model kimi-k3     # every time after — launches OpenCode on Kimi K3
```

For GLM 5.2 on Scaleway, if you have ever run `pks scaleway init` (for GPU work, for `pks claude scaleway`, or otherwise), you are already at step 2:

```bash
pks scaleway init                # once — stores your Scaleway secret key
pks opencode --model glm-5.2     # every time after — launches OpenCode on GLM 5.2
```

## Options

| Argument | Required | Description |
|---|---|---|
| `ARGS` | no | Additional arguments passed through to the `opencode` CLI (e.g. `run --format json "..."`). |

| Flag | Default | Description |
|---|---|---|
| `-m`, `--model <id>` | `glm-5.2` | Model id from the provider catalog. A provider prefix (`scaleway/…`, `moonshot/…`) is accepted and stripped. Unknown ids are rejected with the list of known models. |
| `-p`, `--provider <id>` | resolved from the model | Explicit provider (`scaleway` or `moonshot`). Only needed when a model is offered by multiple configured providers — or to document intent. Must not conflict with a model prefix. |

## Verify

```bash
pks opencode --model kimi-k3
```

OpenCode launches and its model selector shows `moonshot/kimi-k3`. Send a prompt and confirm the reply.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Exit `1`: "Unknown model '…'. Known models: …" | The id is not in the pks provider catalog. Check the spelling against the listed models. (Earlier pks versions passed unknown ids through to Scaleway verbatim — that fallback is gone; the catalog is now the single source of truth.) |
| Exit `1` naming `pks moonshot init` or `pks scaleway init` | The model's provider has no stored credential. Run the named init command. |
| Exit `1`: "available from multiple configured providers" | The model exists in more than one authenticated provider's catalog. Disambiguate with `--provider <id>`. |
| Exit `1`: "Model prefix '…/' conflicts with --provider …" | You gave both a prefixed model and a different provider flag. Drop one of them. |
| Exit `127` naming the opencode CLI | OpenCode is not on PATH. Install it: `npm install -g opencode-ai`. |
| Your `opencode.json` was not modified | This is intentional. The provider config is injected per-process via `OPENCODE_CONFIG_CONTENT` and never written to disk. |

## See also

- [pks moonshot](/tools/pks/moonshot) — register and validate the Moonshot API key behind `kimi-k3`
- [pks scaleway](/tools/pks/scaleway) — authenticate against Scaleway with a static API key pair
- [pks claude scaleway](/tools/pks/claude/scaleway) — the proxy-based sibling for Claude Code, covering the rest of the Scaleway catalog
- [pks codex](/tools/pks/codex) — run the upstream Codex CLI against an Azure AI Foundry deployment
