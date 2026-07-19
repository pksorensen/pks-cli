---
title: "Hook events and their handlers"
description: "What each of the seven Claude Code hook events does when pks handles it — which one enforces policy, which pass through, and which only dump their payload."
tags: [concept, cli, claude-code, hooks]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
---

Claude Code fires named events at fixed points in an agent turn. `pks hooks init` registers a pks handler for each of them, but the handlers differ enormously in what they do — one enforces a rule, three read their input and allow the action, and three exist only to show you what a payload looks like.

## The seven events

| Event | Subcommand | Fires when |
| --- | --- | --- |
| PreToolUse | `pks hooks pre-tool-use` | Before Claude Code executes a tool. |
| PostToolUse | `pks hooks post-tool-use` | After a tool finishes executing. |
| UserPromptSubmit | `pks hooks user-prompt-submit` | Before a submitted prompt is processed. |
| Stop | `pks hooks stop` | When the agent is about to stop responding. |
| Notification | `pks hooks notification` | On a general Claude Code notification, such as a permission prompt. |
| SubagentStop | `pks hooks subagent-stop` | When a spawned subagent stops. |
| PreCompact | `pks hooks pre-compact` | Before Claude Code compacts its context. |

`pks hooks list` prints this same set of events as a table. It is informational — it reads no settings file and reports nothing about what is currently registered.

## How a handler talks back

Every handler reads the event payload from stdin. The decision-carrying handlers — PreToolUse, PostToolUse, UserPromptSubmit, and Stop — return a decision object. Only a non-trivial decision, meaning a block, an approval, a stop, or a suppression, is written to stdout as JSON. A plain proceed prints nothing at all.

Silence is therefore the correct output for a handler that allows an action, and it is why pks automatically suppresses its own banner for the four decision-carrying handlers — `pre-tool-use`, `post-tool-use`, `user-prompt-submit`, and `stop` — when they're invoked as hook events: any stray output there would corrupt the JSON contract.

That automatic suppression does **not** cover the three debug handlers. `notification`, `subagent-stop`, and `pre-compact` only skip the banner when `--json` is explicitly passed on the command line, and the command string `pks hooks init` writes for those three does not include `--json`. So when Claude Code fires Notification, SubagentStop, or PreCompact through the wiring `init` created, the full banner prints to the console before the debug dump. That's harmless — neither of those handlers writes a JSON contract for Claude Code to parse — but don't assume their output is clean if you're capturing it.

## Enforcing, pass-through, and debug

**Enforcing — one handler.** `stop` reads the configured lint command, runs it, and blocks the turn from ending if it fails. Setup is on [Block a turn on a failing lint](/tools/pks/hooks/stop).

**Pass-through — three handlers.** `pre-tool-use`, `post-tool-use`, and `user-prompt-submit` consume their payload and always proceed. None of them blocks anything today. `post-tool-use` echoes its stdin and working directory to the console when `--json` is absent; `user-prompt-submit` prints the first 200 characters of the prompt in the same case.

Note the matchers `init` writes: `PreToolUse` and `PostToolUse` are registered with a `Bash` matcher, so they fire only for Bash tool calls. `UserPromptSubmit` is registered with no matcher and fires for every prompt.

**Debug — three handlers.** `notification`, `subagent-stop`, and `pre-compact` are scaffolding. Each dumps the full environment variable list, the command-line arguments, and raw stdin to the console, then exits zero. They return no decision and block nothing.

> **Note.** The three debug handlers print every environment variable on every invocation, unconditionally — the `--json` flag does not suppress it. Anything sensitive in the process environment, including API keys and cloud credentials, appears in that output. Treat any terminal recording or log that captures these hooks as containing secrets, and remove the corresponding entries from `settings.json` if that matters in your setup.

## Which handlers should I register?

- If you want the lint gate → the `Stop` entry is the only one you need.
- If you are inspecting what Claude Code sends → register the debug handler for the event you're studying, read the output, then remove it.
- If you want tool-call policy enforcement → `pre-tool-use` does not provide it. It always proceeds regardless of the tool call.

## Running a handler by hand

The handlers are meant to be invoked by Claude Code with a payload on stdin. Running one manually is useful only for inspecting behavior:

```bash
echo '{"tool_name":"Bash","tool_input":{"command":"ls"}}' | pks hooks pre-tool-use
```

## See also

- [Block a turn on a failing lint](/tools/pks/hooks/stop) — the one handler with enforcement behavior
- [Register pks hooks in a project](/tools/pks/hooks/init) — how these entries get into `settings.json`
- [pks hooks reference](/tools/pks/hooks/reference) — per-command behavior and the shared option table
- [pks hooks](/tools/pks/hooks) — the group overview and mental model
