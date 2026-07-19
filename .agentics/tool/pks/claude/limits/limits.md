---
title: "pks claude limits"
description: "Poll Claude Code session and week usage limits as structured JSON — percentages, reset countdowns, and whether you are pacing ahead of the clock."
tags: [how-to, quota, automation, claude-code]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks claude limits [options]"
---

`pks claude limits` answers "can I spend more, or will I run out" as data rather than as a panel you squint at. It reports SESSION and WEEK usage-limit percentages, reset times, and pace — whether you are ahead of or behind the clock, and at what burn ratio — in a form you can poll hourly from cron.

`pks claude session-usage` is a second registered name for the identical command class. Everything on this page applies to both.

## Prerequisites

- **The `claude` CLI on your PATH, already logged in.** The command drives a real headless Claude Code session.
- **`tmux` on your PATH.** The capture runs inside a detached tmux session.

## How it gets the numbers

There is no local file holding your quota, so the command produces one. It spawns a fresh detached tmux session running `claude --dangerously-skip-permissions`, types `/usage`, captures the rendered panel text, and parses it deterministically.

When the deterministic parse finds zero blocks, it automatically retries through a second path: a real `claude -p` call configured with `--mcp-config` pointing at `pks mcp --transport stdio`, restricted to the `report_session_limits` tool. That tool writes a JSON sink file, which is read back and mapped into the same block structure. `--llm` forces this path directly.

The tmux capture viewport is fixed at 200x200. A shorter height silently drops `Current week (<Model>)` blocks that scroll below the fold, so the size is deliberate rather than arbitrary.

## 1. Read it interactively

```bash
pks claude limits
```

You get a colored bar-and-pace table covering session and week usage, with reset countdowns.

## 2. Poll it from a script

```bash
pks claude limits --json
```

JSON goes to stdout. The flag turns on automatically when stdout is not a TTY, so a cron job or a pipe gets machine-readable output without passing anything.

## 3. Force the fallback path

```bash
pks claude limits --llm --model claude-haiku-4-5-20251001
```

Use this when the deterministic parser misreads a changed panel layout. `--model` selects the model for the round trip; the default is already a cheap one, and keeping it cheap matters because this path issues a real billed request.

## 4. Give a slow session more room

```bash
pks claude limits --timeout 120
```

The default 60-second budget covers boot, `/usage` render, and kill together. On a slow-booting `claude`, raise it.

## Options

| Flag | Default | Description |
|---|---|---|
| `--json` | auto | Emit structured JSON to stdout. Enabled automatically when stdout is not a TTY. |
| `--llm` | — | Force the MCP round-trip fallback instead of the deterministic parser. |
| `--timeout <SECONDS>` | `60` | Whole-capture timeout covering boot, render, and kill. |
| `--model <ID>` | `claude-haiku-4-5-20251001` | Model id used for the fallback round trip. |
| `--debug` | — | Dump the raw captured tmux pane to stderr before parsing. |

## Verify

```bash
pks claude limits --json | head -c 200
```

You should see a JSON document containing the session and week blocks. An error message instead of blocks means the capture timed out or the panel never rendered.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Error instead of a report, roughly at the timeout | The whole operation exceeded `--timeout`. Raise it, and confirm `claude` starts quickly on its own. |
| An unexpected billed request appears in your usage | A transient parse miss auto-retries through `--llm`, which costs a real invocation against `--model`. Run with `--debug` to see what the parser saw. |
| Week blocks missing from the output | Panel content scrolled below the capture fold. The viewport is fixed at 200x200 for exactly this reason, so report the panel layout rather than shrinking it. |
| Temp files accumulate in the OS temp directory | The `--llm` path writes a temp MCP config and sink file and removes them in a `finally` block. A hard kill leaks them. Delete them manually. |
| `tmux: command not found` | Install tmux. There is no alternative capture mechanism. |

## See also

- [pks claude usage](/tools/pks/claude/usage) — spent cost from local transcripts
- [pks claude stats](/tools/pks/claude/stats) — latency and activity over the same transcripts
- [pks claude reference](/tools/pks/claude/reference) — every command and flag in the branch
