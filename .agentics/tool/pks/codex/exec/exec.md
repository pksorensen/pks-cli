---
title: "Run Codex non-interactively"
description: "Drive Codex's own scripted execution mode through the Azure AI Foundry passthrough, giving a resumed or fresh session one instruction without a terminal UI."
tags: [how-to, codex, automation, foundry]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks codex exec [ARGS] [options]"
examples:
  - command: "pks codex exec resume --last \"Continue the previous task\""
    description: "Resume the last session and give it a new instruction"
---

Run Codex without its interactive interface. `pks codex exec` forwards Codex's own non-interactive execution mode through the loopback passthrough, so a scripted instruction runs against an Azure AI Foundry deployment with Entra authentication supplied per request.

## 1. Prerequisites

- **A Foundry login** — `pks foundry init`, with a resource endpoint selected.
- **The upstream Codex CLI on PATH** — `npm i -g @openai/codex`.
- **Familiarity with `codex exec`.** The argument grammar after `exec` belongs to the Codex CLI, not to pks.

## 2. Run one instruction against the last session

```bash
pks codex exec resume --last "Continue the previous task"
```

pks starts the passthrough, forwards `resume --last "Continue the previous task"` to `codex exec` untouched, and streams whatever Codex produces.

Nested native subcommands and flags survive because pks recovers them from the raw process arguments rather than from parsed remaining arguments.

## 3. Pin the deployment for a scripted run

```bash
pks codex exec --model gpt-5-codex --reasoning-effort high resume --last "Run the test suite and fix failures"
```

Pinning the deployment keeps a scheduled or scripted run from drifting when the Foundry account default changes.

## 4. Choose the sandbox posture

An unattended run bypasses Codex's approval prompts by default, which is the point of running it unattended — pks appends `--dangerously-bypass-approvals-and-sandbox` unless told otherwise. Add `--safe` when the run happens on a machine that is not disposable.

```bash
pks codex exec --safe resume --last "Summarize what changed"
```

## 5. Verify

```bash
pks codex --print-env
```

The printed launch command shows the resolved deployment and loopback port that an `exec` run will use. Stop the foreground passthrough with Ctrl-C.

## 6. Next steps

- [Resume a Codex session through Foundry](/tools/pks/codex/resume) — the interactive counterpart
- [How the Foundry passthrough works](/tools/pks/codex/passthrough) — where silent failures are recorded
- [pks codex CLI reference](/tools/pks/codex/reference) — every subcommand and flag

## Options

| Flag | Default | Description |
|---|---|---|
| `-m, --model <name>` | Configured deployment, else `gpt-5-codex` | Foundry deployment name for this run. |
| `-e, --reasoning-effort <level>` | Configured value, else `medium` | Reasoning effort: `none`, `low`, `medium`, `high`, `xhigh`, or `default` (omits the `-c model_reasoning_effort` override and lets Codex use its own built-in default). |
| `-p, --port <port>` | Configured value, else `8788` | Loopback port for the passthrough. |
| `--print-env` | `false` | Run the passthrough in the foreground and print the launch command instead of starting Codex. |
| `--safe` | `false` | Keep Codex approval prompts and sandbox enabled. |

The positional `ARGS` are native `codex exec` arguments, forwarded verbatim.

## Troubleshooting

**The run produces no output and exits.** Read `~/.pks-cli/codex-passthrough-failures.log`. Auth failures, non-2xx upstream responses, and `response.failed` events land there instead of on the terminal.

**A quoted instruction arrives split or empty.** Quote the whole instruction as one shell argument, as in the examples, and place it after the native `resume` arguments.

**Exit code 1 pointing at `pks foundry init`.** Authentication or resource selection is missing.

**Exit code 127.** The `codex` binary is not on PATH.

## See also

- [pks codex](/tools/pks/codex) — group overview and mental model
- [Launch the Codex CLI on Foundry](/tools/pks/codex/run) — the interactive entry point
- [pks codex CLI reference](/tools/pks/codex/reference) — the full flag surface
