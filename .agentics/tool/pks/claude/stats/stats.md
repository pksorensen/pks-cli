---
title: "pks claude stats"
description: "Render a GitHub-style activity heatmap, streaks, token totals, and response-time trends from your local Claude Code session transcripts."
tags: [how-to, analytics, claude-code]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks claude stats [options]"
---

`pks claude stats` answers one question from files already on your disk: how has your Claude Code usage changed over time. It parses `~/.claude/projects/**/*.jsonl` session transcripts and renders activity, volume, and latency views. It never contacts a server.

## Prerequisites

- **Prior Claude Code usage on this machine.** The command reads transcripts under `~/.claude/projects/`. With none in scope it exits `1`.
- **A terminal.** Charts auto-size to the console, so a real TTY produces readable output.

## 1. Run it for the current project

```bash
pks claude stats
```

You get an activity heatmap in the GitHub contribution style, session streaks and durations, total token counts including a "times War and Peace" comparison, a response-time-over-time chart measured in milliseconds per output token, a per-model breakdown table, and an all-time percentile table.

The response-time chart splits the data into a recent window and the prior period, which is what makes a slowdown visible rather than merely suspected.

## 2. Widen the scope

```bash
pks claude stats --all-projects
```

This analyses every project under `~/.claude/projects/` instead of only the one matching your current directory.

To point at a different project without changing directory:

```bash
pks claude stats --project ~/code/other-repo
```

## 3. Change the recent window

```bash
pks claude stats --days 14
```

The default recent window is 7 days. Widening it smooths noise from a single unusual day when comparing recent latency against the prior period.

## Options

| Flag | Default | Description |
|---|---|---|
| `-d`, `--days <N>` | `7` | Number of recent days treated as the recent window. |
| `-p`, `--project <PATH>` | current directory | Project directory to analyse. |
| `--all-projects` | — | Analyse every project under `~/.claude/projects/` instead of only the current one. |

## Verify

```bash
pks claude stats --all-projects
```

You should see the heatmap and the per-model breakdown table. If the command instead prints that no Claude session files were found and exits `1`, no transcripts exist in scope yet.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `No Claude session files found`, exit `1` | The scope holds no `.jsonl` transcripts. Run `claude` in the project first, or pass `--all-projects`. |
| Current project shows nothing but `--all-projects` works | The project-path-to-folder-name mapping is exact — path separators become `-`. Unlike `usage`, there is no substring matching. Pass the exact directory with `--project`. |
| Charts are squashed or misaligned | Output was redirected or the console size is unavailable. The renderer falls back to `COLUMNS` and `LINES`, then `stty size`, then 120x40. Run in a TTY or set `COLUMNS` and `LINES`. |

## See also

- [pks claude usage](/tools/pks/claude/usage) — the money view over the same transcripts
- [pks claude limits](/tools/pks/claude/limits) — quota and pace, which transcripts cannot answer
- [pks claude reference](/tools/pks/claude/reference) — every command and flag in the branch
