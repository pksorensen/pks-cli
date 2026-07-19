---
title: "Launch the Codex CLI on Foundry"
description: "Start the upstream Codex CLI against an Azure AI Foundry deployment, choose the model and reasoning effort, and control the sandbox and loopback port."
tags: [how-to, codex, foundry, cli]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks codex run [ARGS] [options]"
examples:
  - command: "pks codex"
    description: "Launch on the default Foundry deployment"
  - command: "pks codex run --model gpt-5-codex --reasoning-effort high"
    description: "Explicit deployment plus reasoning effort"
  - command: "pks codex run --safe"
    description: "Keep Codex approval prompts and sandbox enabled"
  - command: "pks codex run --print-env"
    description: "Print the launch command instead of running codex"
---

Start a Codex session that bills through Azure AI Foundry: pks starts the loopback passthrough, resolves a deployment, and execs the real `codex` binary against it. Bare `pks codex` and `pks codex run` are the same command — `run` is the explicit form, and the one to use when you also pass native Codex arguments.

## 1. Prerequisites

- **A Foundry login** — `pks foundry init` must have completed, with a resource endpoint selected.
- **The upstream Codex CLI on PATH** — install it with `npm i -g @openai/codex`. Without it the command exits 127 with an install hint.
- **A completed setup run** — [pks codex init](/tools/pks/codex/init) stores the default deployment, port, and effort. Not strictly required, but without it every run falls back to `gpt-5-codex` on port `8788`.

## 2. Launch a session

```bash
pks codex
```

The passthrough binds a loopback port, and the Codex CLI opens against the resolved Foundry deployment. From here the interface is entirely Codex's own.

## 3. Choose a deployment and effort

```bash
pks codex run --model gpt-5.6-sol --reasoning-effort high
```

The deployment resolves in this order:

1. `-m, --model` on the command line.
2. The Foundry account's stored default model, used only when it looks like a GPT or Codex model.
3. `Deployment` in `~/.pks-cli/codex.json`, written by `pks codex init`.
4. The built-in fallback `gpt-5-codex`.

## 4. Decide on the sandbox

By default pks appends `--dangerously-bypass-approvals-and-sandbox` to the Codex invocation, on the assumption that you run in an already-isolated environment such as a devcontainer or a disposable VM.

```bash
pks codex run --safe
```

`--safe` keeps Codex's own approval prompts and sandboxing in place. Use it whenever the session runs directly on a machine you care about.

## 5. Pass native Codex arguments

Anything after the pks flags is forwarded to the real `codex` binary unchanged. Use the explicit `run` form so it stays unambiguous which flags bind to pks and which are forwarded.

```bash
pks codex run --model gpt-5-codex -- --help
```

## 6. Verify

```bash
pks codex --print-env
```

The passthrough runs in the foreground and prints the launch command instead of starting Codex. The printed command names the resolved deployment and the loopback port actually in use, which is the fastest way to confirm the resolution order landed where you expected.

## 7. Next steps

- [Resume a Codex session through Foundry](/tools/pks/codex/resume) — reattach to earlier work
- [Run Codex non-interactively](/tools/pks/codex/exec) — scripted, one-instruction runs
- [How the Foundry passthrough works](/tools/pks/codex/passthrough) — what pks strips from requests and why

## Options

| Flag | Default | Description |
|---|---|---|
| `-m, --model <name>` | Configured deployment, else `gpt-5-codex` | Foundry deployment name for this run. |
| `-e, --reasoning-effort <level>` | Configured value, else `medium` | Reasoning effort: `none`, `low`, `medium`, `high`, `xhigh`, or `default` (omits the `-c model_reasoning_effort` override and lets Codex use its own built-in default). |
| `-p, --port <port>` | Configured value, else `8788` | Loopback port for the passthrough. |
| `--print-env` | `false` | Run the passthrough in the foreground and print the launch command instead of starting Codex. |
| `--safe` | `false` | Keep Codex approval prompts and sandbox enabled. |

The positional `ARGS` are passed through to the underlying `codex` command.

## Troubleshooting

**Exit code 127 with an install hint.** The `codex` binary is not on PATH. Install `@openai/codex` globally, or use `--print-env` and launch Codex yourself with the printed command.

**Exit code 1 pointing at `pks foundry init`.** Authentication or the resource endpoint is missing. Re-run `pks foundry init`.

**A yellow warning about the port.** The requested port, `8788` by default, is already bound — often by a stale passthrough. pks falls back to a free port automatically instead of failing.

**The session dies with no visible error.** Auth failures, non-2xx upstream responses, and `response.failed` events are appended to `~/.pks-cli/codex-passthrough-failures.log` rather than printed in full. Read that file first.

**Foundry rejects a request mentioning tool namespaces.** pks launches Codex with `collaboration_modes`, `apps`, `multi_agent_v2`, and `multi_agent` disabled, and the passthrough strips collaboration-namespaced tool entries, because Foundry rejects OpenAI-internal encrypted tool namespaces. A newer Codex feature outside that list can hit the same wall.

## See also

- [pks codex](/tools/pks/codex) — group overview and mental model
- [Set up pks codex against Azure AI Foundry](/tools/pks/codex/init) — one-time setup
- [pks codex CLI reference](/tools/pks/codex/reference) — every subcommand and environment variable
