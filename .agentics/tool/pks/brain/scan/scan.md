---
title: "pks brain scan filepath"
description: "Find every Claude session whose tool calls touched a file or directory, scanning the raw session logs directly with no ingest and no model calls."
tags: [how-to, brain, scan, git]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain scan filepath <path> [options]"
examples:
  - command: "pks brain scan filepath ./src/foo.cs"
    description: "List sessions that edited one file"
  - command: "pks brain scan filepath ./src/ --format jsonl"
    description: "Scan a directory and emit one JSON object per line"
  - command: "pks brain scan filepath ./src/foo.cs --include-bash"
    description: "Also match Bash commands mentioning the path"
---

Scan answers one question: which Claude sessions touched this file, when, and with which tool. It walks every session JSONL under `~/.claude/projects` looking for tool-use entries against the path you name. No model is called and nothing is billed.

It is lower-level and slower than [pks brain commit-plan](/tools/pks/brain/commit-plan), which prefers the pre-ingested firehose. The trade is that scan needs no ingest at all — it reads the raw logs on every invocation.

## 1. Prerequisites

- **Claude session history** at `~/.claude/projects`, or another directory passed via `--projects-dir`.
- **No ingest required.** This is the tool to reach for on a machine where the brain has never been initialized.

## 2. Scan a file

```bash
pks brain scan filepath ./src/foo.cs
```

Matching sessions print with their timestamps and the tool that touched the path.

## 3. Scan a directory

```bash
pks brain scan filepath ./src/
```

A directory argument matches every tool-use entry beneath it.

## 4. Widen or narrow the match

```bash
pks brain scan filepath ./src/foo.cs --include-bash
pks brain scan filepath ./src/foo.cs --since 2026-07-01
```

`--include-bash` also matches Bash tool-use entries whose command text contains the path as a substring. That is a plain substring test, so it can match a command that merely mentions the path in a comment or an unrelated argument.

`--since` skips sessions whose first entry predates the given date. It requires a parseable ISO date — the relative `7d` and `24h` shorthand accepted by other brain commands exits with code 1 here.

## 5. Emit machine-readable output

```bash
pks brain scan filepath ./src/ --format jsonl
```

`--format` accepts `text` (default), `json`, or `jsonl`.

## 6. Verify

Pick a file you edited today and scan it. The most recent session in the output should be the one you remember. If the list is empty, confirm `--projects-dir` points at real session logs.

## Options

| Flag | Default | Description |
|---|---|---|
| `--include-bash` | `false` | Also match Bash tool-use entries whose command contains the path as a substring. |
| `--since <date>` | — | Skip sessions whose first entry is older than this ISO date. |
| `--projects-dir <path>` | `~/.claude/projects` | Override the Claude projects directory. |
| `--format <name>` | `text` | Output format: `text`, `json`, or `jsonl`. |

The positional argument `<path>` is required and accepts a file or a directory.

## Troubleshooting

**Exit code 1 on `--since 7d`.** This command needs an ISO date. Relative windows are accepted by ingest, extract, refresh, and search, but not here.

**Unexpected Bash matches.** `--include-bash` is a substring test against raw command text. Drop the flag for edit-only results.

**The scan is slow.** Every JSONL under the projects directory is read on every invocation — there is no cursor or cache. For repeated queries, ingest once and use [pks brain search](/tools/pks/brain/search) or `commit-plan` instead.

## See also

- [pks brain commit-plan](/tools/pks/brain/commit-plan) — the faster firehose-backed grouping built on the same idea
- [pks brain search](/tools/pks/brain/search) — full-text search once the firehose is populated
- [pks brain conversation](/tools/pks/brain/conversation) — read a session that scan surfaced
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
