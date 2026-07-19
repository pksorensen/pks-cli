---
title: "Set up pks codex against Azure AI Foundry"
description: "Authenticate with Azure AI Foundry, preflight a deployment, and write the managed pks-foundry provider block into your Codex config in one command."
tags: [quickstart, codex, foundry, setup]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks codex init --model gpt-5-codex"
examples:
  - command: "pks codex init"
    description: "Set up config.toml with the default deployment"
  - command: "pks codex init --model gpt-5-codex"
    description: "Set up with an explicit Foundry deployment"
  - command: "pks codex init --port 8801 --reasoning-effort high"
    description: "Save a different loopback port and effort as defaults"
---

Get the real Codex CLI talking to an Azure AI Foundry deployment: sign in to Foundry, run `pks codex init` once, then launch. `init` verifies the deployment with a live request before it writes anything, so a wrong name or a broken credential fails here instead of deep inside a session.

## 1. Prerequisites

- **A Foundry login.** Run `pks foundry init` first. `pks codex init` checks both that you are authenticated and that a Foundry resource endpoint is resolved; without both it exits 1.
- **A GPT or Codex deployment** on that Foundry resource. You need its deployment name, for example `gpt-5-codex` or `gpt-5.6-sol`.
- **The upstream Codex CLI** (`npm i -g @openai/codex`). `init` does not need it, but the launch commands do.

## 2. Sign in to Foundry

```bash
pks foundry init
```

Follow the browser sign-in and pick the Foundry resource holding your deployment. `pks foundry status` shows the selected resource afterwards.

## 3. Run init

```bash
pks codex init --model gpt-5-codex
```

`init` sends a real Responses request to that deployment. On success it writes two files:

- `~/.codex/config.toml` — the managed `pks-foundry` provider block, delimited by `# >>> pks-codex (managed)` and `# <<< pks-codex`. Everything outside those markers, including your own `model_provider` default and ChatGPT login, is preserved verbatim.
- `~/.pks-cli/codex.json` — the pks-side launch defaults: deployment, port, and reasoning effort.

The provider block always points at the loopback passthrough and carries Entra authentication. No key or token is written to disk.

## 4. Save non-default launch settings

Anything you pass to `init` becomes the default for later `pks codex` runs.

```bash
pks codex init --model gpt-5.6-sol --port 8801 --reasoning-effort high
```

Re-run `init` whenever you want to change the default deployment, port, or effort. The write into `config.toml` is idempotent — it replaces the marked block and nothing else.

## 5. Verify

```bash
pks codex --print-env
```

The passthrough starts in the foreground and prints the launch command it would have used, including the resolved deployment and loopback port. No Codex session starts. Stop it with Ctrl-C.

## 6. Next steps

- [Launch the Codex CLI on Foundry](/tools/pks/codex/run) — the everyday entry point
- [How the Foundry passthrough works](/tools/pks/codex/passthrough) — what the managed block contains and where failures are logged
- [pks codex CLI reference](/tools/pks/codex/reference) — the full flag surface

## Options

| Flag | Default | Description |
|---|---|---|
| `-m, --model <name>` | Foundry default model if it looks like a GPT or Codex model, else `gpt-5-codex` | Foundry deployment name to preflight and save as the default. |
| `-e, --reasoning-effort <level>` | `medium` | Reasoning effort saved as the default: `none`, `low`, `medium`, `high`, `xhigh`, or `default` (omits the `-c model_reasoning_effort` override and lets Codex use its own built-in default). |
| `-p, --port <port>` | `8788` | Loopback port saved as the default for the passthrough. |

`--print-env` and `--safe` bind to `init` through the shared settings type but have no effect on it. The positional `ARGS` argument is likewise ignored by `init`.

## Troubleshooting

**The command exits 1 and points at `pks foundry init`.** Either the stored Foundry credential is missing or no resource endpoint is selected. Run `pks foundry init`, then `pks foundry status` to confirm the selection.

**The preflight prints a Foundry error body and exits 1.** The deployment name is wrong, or the resource rejects the request. Nothing was written to `config.toml` or `codex.json`. Check the deployment name in the Foundry portal and re-run with `--model`.

**Codex still uses its own provider after init.** `init` writes the `pks-foundry` provider block but does not change your own `model_provider` default. Launch through `pks codex`, which selects the provider explicitly.

## See also

- [pks codex](/tools/pks/codex) — what the group is and when to use it
- [Launch the Codex CLI on Foundry](/tools/pks/codex/run) — running a session after setup
- [pks codex CLI reference](/tools/pks/codex/reference) — every flag and environment variable
