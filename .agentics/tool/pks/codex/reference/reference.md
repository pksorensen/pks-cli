---
title: "pks codex CLI reference"
description: "Complete command, flag, file, and environment-variable reference for pks codex — launch, init, session commands, and the Azure AI Foundry passthrough."
tags: [reference, codex, foundry, cli]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks codex [subcommand] [options]"
examples:
  - command: "pks codex init --model gpt-5-codex"
    description: "Preflight a deployment and write the managed block"
  - command: "pks codex run --reasoning-effort high"
    description: "Launch with an explicit reasoning effort"
  - command: "pks codex resume --last"
    description: "Resume the most recent Codex session"
  - command: "pks codex exec resume --last \"Continue the previous task\""
    description: "Non-interactive run against the last session"
  - command: "pks codex --print-env"
    description: "Print the launch command instead of running codex"
---

`pks codex` runs the upstream `@openai/codex` CLI against an Azure AI Foundry Responses deployment through a loopback passthrough that injects a fresh Microsoft Entra ID bearer token into every request. Requests and responses are forwarded unchanged.

Every subcommand except `init` binds to the same command implementation: pks detects the subcommand name, prepends its own launch flags, and forwards the remaining native arguments into a `codex <subcommand> …` invocation of the real binary.

## Synopsis

```text
pks codex [subcommand] [options]
```

```text
run          Launch codex against the resolved Foundry deployment   [default]
resume       Forward `codex resume` through the passthrough
exec         Forward `codex exec` through the passthrough
fork         Forward `codex fork` through the passthrough
archive      Forward `codex archive` through the passthrough
unarchive    Forward `codex unarchive` through the passthrough
delete       Forward `codex delete` through the passthrough
init         Preflight a deployment and write the managed config block
```

### Shared launch options

Every subcommand accepts the same pks-level flags. Native Codex arguments follow them and are forwarded verbatim.

| Flag | Default | Description |
|---|---|---|
| `-m, --model <name>` | Configured deployment, else `gpt-5-codex` | Foundry deployment name, for example `gpt-5.6-sol`. |
| `-e, --reasoning-effort <level>` | Configured value, else `medium` | Reasoning effort: `none`, `low`, `medium`, `high`, `xhigh`, or `default` (omits the `-c model_reasoning_effort` override and lets Codex use its own built-in default). |
| `-p, --port <port>` | Configured value, else `8788` | Loopback port for the passthrough. |
| `--print-env` | `false` | Run the passthrough in the foreground and print the launch command instead of starting Codex. |
| `--safe` | `false` | Keep Codex approval prompts and sandbox enabled. |

### Deployment resolution order

1. `-m, --model` on the command line.
2. The Foundry account's stored default model, used only when it looks like a GPT or Codex model.
3. `Deployment` in `~/.pks-cli/codex.json`.
4. The built-in fallback `gpt-5-codex`.

### Authentication

Every subcommand checks, before doing anything else, that the Foundry credential is authenticated and that a resource endpoint is resolved. If either check fails it prints an error naming `pks foundry init` and exits 1.

### Global environment variables

| Variable | Default | Purpose |
|---|---|---|
| `PKS_CODEX_TOKEN` | (generated per run) | Random token pks passes to the launched Codex process; the passthrough validates every incoming request against it. Not user-set. |
| `OPENAI_API_KEY` | (removed) | Explicitly removed from the launched Codex environment so an ambient OpenAI key cannot bypass the selected Foundry provider. |
| `PKS_CODEX_FOUNDRY_API_KEY` | (unused) | Environment-key name for a direct Azure-API-key provider block. `pks codex init` always writes the passthrough block instead, so no current command path populates this. |

### Files

| Path | Contents |
|---|---|
| `~/.codex/config.toml` | The managed `pks-foundry` provider block, delimited by ``# >>> pks-codex (managed) — edit via `pks codex init` `` and `# <<< pks-codex`. Everything outside the markers is preserved. |
| `~/.pks-cli/codex.json` | pks-side launch defaults: deployment, port, reasoning effort. Written by `init`, read as a fallback by every other subcommand. |
| `~/.pks-cli/codex-passthrough-failures.log` | Auth failures, non-2xx upstream responses, and `response.failed` events. |

## run

Launches the real `codex` CLI on the resolved Foundry deployment through the passthrough. Bare `pks codex` with no subcommand runs the identical code path; `run` is the explicit form and the one to prefer when native Codex arguments follow, because it stays unambiguous which flags bind to pks.

The command resolves the deployment, binds the loopback port, generates the per-run token, and execs `codex`. It always passes `--disable collaboration_modes --disable apps --disable multi_agent_v2 --disable multi_agent`, because Foundry rejects the corresponding OpenAI-internal tool namespaces. Unless `--safe` is given, it also appends `--dangerously-bypass-approvals-and-sandbox`, assuming an already-isolated environment.

Flags: the shared launch options above. Positional `ARGS` are passed through to the underlying `codex` command.

```bash
pks codex run --model gpt-5-codex --reasoning-effort high
```

Exit 127 with an install hint means the `codex` binary is not on PATH; install `@openai/codex` or use `--print-env` and launch it yourself. If the requested port is already bound, pks falls back to a free port with a yellow warning rather than failing.

## resume

Forwards `codex resume` through the passthrough, reattaching to a previous Codex session by ID or with `--last`. Session lookup is entirely native Codex behavior — pks keeps no session store.

Native arguments are recovered from the raw process arguments, so a UUID-shaped or flag-shaped positional value reaches the real binary unmangled.

Flags: the shared launch options above. Positional `ARGS` are native `codex resume` arguments.

```bash
pks codex resume 019f3e01-b0c1-7bf2-b1d8-d0befe7232fd
```

## exec

Forwards `codex exec`, Codex's own non-interactive execution mode, through the passthrough. Uses the same raw-argument recovery as `resume`, so nested native subcommands and flags survive.

Flags: the shared launch options above. Positional `ARGS` are native `codex exec` arguments.

```bash
pks codex exec resume --last "Continue the previous task"
```

## fork

Forwards `codex fork` through the passthrough — Codex's own branch-a-session feature, tunneled through Entra authentication. Behavior is inherited from the native command.

Flags: the shared launch options above. Positional `ARGS` are native `codex fork` arguments. Walkthrough: [Manage Codex sessions through Foundry](/tools/pks/codex/sessions).

## archive / unarchive

Forwards `codex archive` and `codex unarchive` through the passthrough — Codex's own session archiving and restoration. pks prepends the shared launch flags to the invocation even though model and effort are not meaningful for an archive operation.

Flags: the shared launch options above. Positional `ARGS` are native arguments. Walkthrough: [Manage Codex sessions through Foundry](/tools/pks/codex/sessions).

## delete

Forwards `codex delete` through the passthrough, permanently deleting a Codex session. The deletion is native Codex behavior; pks only tunnels authentication.

> **Note.** This is destructive and has no pks-side confirmation or undo.

Flags: the shared launch options above. Positional `ARGS` are native `codex delete` arguments. Walkthrough: [Manage Codex sessions through Foundry](/tools/pks/codex/sessions).

## init

Preflights a Foundry deployment with a real Responses HTTP call, then writes the managed `pks-foundry` provider block into `~/.codex/config.toml` and saves the resolved deployment, port, and reasoning effort into `~/.pks-cli/codex.json`.

If the preflight fails — a wrong deployment name, or an auth problem — the command prints the Foundry error body and exits 1 without touching either file. It always writes the passthrough block with Entra authentication and never the direct API-key block, so nothing secret reaches disk. It does not install or invoke the `codex` binary.

| Flag | Default | Description |
|---|---|---|
| `-m, --model <name>` | Foundry default model if it looks like a GPT or Codex model, else `gpt-5-codex` | Deployment to preflight and save as the default. |
| `-e, --reasoning-effort <level>` | `medium` | Reasoning effort saved as the default: `none`, `low`, `medium`, `high`, `xhigh`, or `default` (omits the `-c model_reasoning_effort` override and lets Codex use its own built-in default). |
| `-p, --port <port>` | `8788` | Loopback port saved as the default. |

`--print-env`, `--safe`, and the positional `ARGS` bind through the shared settings type but have no effect on `init`.

```bash
pks codex init --model gpt-5-codex
```

## See also

- [pks codex](/tools/pks/codex) — group overview and mental model
- [Set up pks codex against Azure AI Foundry](/tools/pks/codex/init) — the one-time setup walkthrough
- [Launch the Codex CLI on Foundry](/tools/pks/codex/run) — the everyday entry point
- [Manage Codex sessions through Foundry](/tools/pks/codex/sessions) — the fork/archive/unarchive/delete lifecycle
- [How the Foundry passthrough works](/tools/pks/codex/passthrough) — proxy internals, config files, and the failure log
