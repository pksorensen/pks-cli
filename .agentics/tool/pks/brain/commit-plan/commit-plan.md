---
title: "pks brain commit-plan"
description: "Group a pile of uncommitted files by the Claude session that produced them, so one messy working tree becomes several focused, session-coherent commits."
tags: [how-to, brain, git, commits]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks brain commit-plan (--files <paths> | --files-from <path> | --uncommitted) [options]"
examples:
  - command: "pks brain commit-plan --uncommitted"
    description: "Group everything git reports as changed"
  - command: "pks brain commit-plan --files ./src/a.cs ./src/b.cs"
    description: "Plan groups for an explicit file list"
  - command: "pks brain commit-plan --uncommitted --format json"
    description: "Emit machine-readable groups for scripting"
---

Commit-plan answers a specific problem: a long agent-assisted day leaves thirty modified files that belong to four unrelated pieces of work. It maps each file back to the Claude session whose tool calls touched it, then groups the files by session so each group can become its own commit.

Each group reports its primary and contributing sessions, the files it shares with earlier groups, and — on request — the user prompts that drove the edits.

## 1. Prerequisites

- **A git repository**, when using `--uncommitted`. The command shells out to `git rev-parse --show-toplevel` and `git status --porcelain`.
- **Claude session history** at `~/.claude/projects`, or another directory passed via `--projects-dir`.
- **Nothing else.** This command is deterministic and calls no model.

## 2. Plan your working tree

```bash
pks brain commit-plan --uncommitted
```

Changed and untracked files are detected from `git status --porcelain`, then grouped. Unless `--no-refresh` is passed, an ingest pass runs first so the session-to-file graph is current — which makes a plain run slower than it looks, and writes to the global `~/.pks-cli/brain/` layer as a side effect.

## 3. Plan an explicit set

Exactly one input mode is required. Passing zero or more than one exits with code 1.

```bash
pks brain commit-plan --files ./src/a.cs ./src/b.cs
pks brain commit-plan --files-from ./changed.txt
```

`--files-from` reads one path per line.

## 4. Tune the grouping

```bash
pks brain commit-plan --uncommitted --min-files 3 --since 2026-07-01
```

`--min-files` sets how many files a group needs to qualify, defaulting to 2. `--since` filters sessions by their first-entry timestamp using an ISO date.

To see why a group exists:

```bash
pks brain commit-plan --uncommitted --include-prompts
```

This attaches up to ten of the user prompts that preceded the file edits in each group — useful raw material for the commit message.

## 5. Skip the refresh, or fall back to the scanner

```bash
pks brain commit-plan --uncommitted --no-refresh
pks brain commit-plan --uncommitted --force-scan
```

`--no-refresh` skips the automatic ingest pass, which makes the run faster at the cost of missing sessions recorded since the last ingest. `--force-scan` bypasses the firehose graph entirely and uses the per-file scanner instead — the same engine as [pks brain scan filepath](/tools/pks/brain/scan). It is the legacy path, and the thing to try when the firehose path behaves oddly.

## 6. Script it

```bash
pks brain commit-plan --uncommitted --format json
```

`--format` accepts `text` (default), `json`, or `jsonl`. An unknown value exits with code 1.

## 7. Verify

Run the plan, then stage one group at a time and confirm each commit stands on its own:

```bash
git add ./src/a.cs ./src/b.cs
git commit
```

If a group mixes unrelated work, raise `--min-files` or add `--include-prompts` to see which session actually drove each file.

## Options

| Flag | Default | Description |
|---|---|---|
| `--files <paths>` | — | Explicit list of file paths to plan groups for. |
| `--files-from <path>` | — | Read file paths, one per line, from the given file. |
| `--uncommitted` | `false` | Detect changed and untracked files from `git status --porcelain`. |
| `--since <date>` | — | Filter sessions by first-entry timestamp, as an ISO date. |
| `--min-files <n>` | `2` | Minimum number of files a group must contain to qualify. |
| `--include-bash` | `false` | Also match Bash tool-use entries when resolving which session touched a file. |
| `--projects-dir <path>` | `~/.claude/projects` | Override the Claude projects directory. |
| `--format <name>` | `text` | Output format: `text`, `json`, or `jsonl`. |
| `--include-prompts` | `false` | Include up to ten user prompts that preceded the edits in each group. |
| `--no-refresh` | `false` | Skip the automatic ingest pass before planning. |
| `--force-scan` | `false` | Bypass the firehose graph and use the per-file scanner. |

## Troubleshooting

**Exit code 1 before any output.** Either no input mode was given, more than one was given, or `--format` was not one of `text`, `json`, `jsonl`.

**"Not inside a git repository".** `--uncommitted` needs a repository. Use `--files` or `--files-from` outside one.

**A renamed file appears once, under its new path.** Renames and copies are reported by their new path.

**An untracked directory was ignored.** Entries that git reports as a directory are skipped rather than expanded. Pass the individual files.

**The run took much longer than expected.** The automatic ingest pass ran. Add `--no-refresh` when the firehose is already current.

## See also

- [pks brain scan filepath](/tools/pks/brain/scan) — the per-file scanner behind `--force-scan`
- [pks brain ingest](/tools/pks/brain/ingest) — the pass commit-plan runs automatically
- [pks brain conversation](/tools/pks/brain/conversation) — read the session a group points at
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
