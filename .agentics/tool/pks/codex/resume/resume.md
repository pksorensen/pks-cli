---
title: "Resume a Codex session through Foundry"
description: "Reattach to an earlier Codex session by ID or with --last, with Azure AI Foundry authentication injected transparently by the pks loopback passthrough."
tags: [how-to, codex, sessions, foundry]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks codex resume [ARGS] [options]"
examples:
  - command: "pks codex resume --last"
    description: "Resume the most recent Codex session through Foundry"
  - command: "pks codex resume 019f3e01-b0c1-7bf2-b1d8-d0befe7232fd"
    description: "Resume a specific session by ID"
---

Pick up an earlier Codex session on a Foundry deployment. `pks codex resume` forwards Codex's own resume feature through the loopback passthrough — the session lookup is native Codex behavior, and pks contributes only the authenticated endpoint.

## 1. Prerequisites

- **A Foundry login** — `pks foundry init`, with a resource endpoint selected.
- **The upstream Codex CLI on PATH** — `npm i -g @openai/codex`.
- **A session Codex itself can resume.** pks keeps no session store of its own. If the local Codex CLI cannot find the session, neither can pks.

## 2. Resume the last session

```bash
pks codex resume --last
```

The passthrough starts, Codex reopens the most recent session, and the conversation continues against the resolved Foundry deployment.

## 3. Resume a specific session

```bash
pks codex resume 019f3e01-b0c1-7bf2-b1d8-d0befe7232fd
```

The session ID and any other native arguments are recovered from the raw process arguments, so a UUID-shaped or flag-shaped value reaches the real `codex` binary unmangled.

## 4. Override the deployment for this session

```bash
pks codex resume --last --model gpt-5.6-sol --reasoning-effort high
```

The same resolution order as a fresh launch applies: the flag wins, then the Foundry account default when it looks like a GPT or Codex model, then `~/.pks-cli/codex.json`, then `gpt-5-codex`.

## 5. Verify

```bash
pks codex --print-env
```

The passthrough runs in the foreground and prints the launch command with the resolved deployment and port, confirming the environment a resume would use. Stop it with Ctrl-C.

## 6. Next steps

- [Run Codex non-interactively](/tools/pks/codex/exec) — resume plus a scripted instruction
- [How the Foundry passthrough works](/tools/pks/codex/passthrough) — the failure log and request sanitizing
- [Manage Codex sessions through Foundry](/tools/pks/codex/sessions) — `fork`, `archive`, `unarchive`, and `delete`

## Options

| Flag | Default | Description |
|---|---|---|
| `-m, --model <name>` | Configured deployment, else `gpt-5-codex` | Foundry deployment name for this session. |
| `-e, --reasoning-effort <level>` | Configured value, else `medium` | Reasoning effort: `none`, `low`, `medium`, `high`, `xhigh`, or `default` (omits the `-c model_reasoning_effort` override and lets Codex use its own built-in default). |
| `-p, --port <port>` | Configured value, else `8788` | Loopback port for the passthrough. |
| `--print-env` | `false` | Run the passthrough in the foreground and print the launch command instead of starting Codex. |
| `--safe` | `false` | Keep Codex approval prompts and sandbox enabled. |

The positional `ARGS` are native `codex resume` arguments, forwarded verbatim.

## Troubleshooting

**Codex reports no such session.** The session store belongs to the Codex CLI. Confirm the ID with Codex directly; pks cannot list or recover sessions.

**A native flag was consumed by pks instead of Codex.** Put the pks flags first and the native arguments after them, as in the examples above.

**Exit code 1 pointing at `pks foundry init`.** Authentication or resource selection is missing. Every `codex` subcommand runs that check before doing anything else.

**Exit code 127.** The `codex` binary is not on PATH.

## See also

- [pks codex](/tools/pks/codex) — group overview and mental model
- [Launch the Codex CLI on Foundry](/tools/pks/codex/run) — starting a fresh session
- [pks codex CLI reference](/tools/pks/codex/reference) — the full flag surface
