---
title: "Quickstart: build your first brain"
description: "Go from zero to a readable wiki page in five steps — initialize the layout, ingest your Claude session logs, extract, synthesize, and render."
tags: [quickstart, brain, claude, pipeline]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain init && pks brain ingest && pks brain refresh"
---

Get a working knowledge base out of your existing Claude Code history in five steps: create the layout, ingest the raw session logs, summarize sessions with a cheap model, cluster them, and render a wiki. The first two steps cost nothing and are worth running even if you stop there.

This page walks the pipeline one phase at a time so you can see what each produces. Once you trust it, [pks brain refresh](/tools/pks/brain/refresh) does all of it in one command.

## 1. Prerequisites

- **A git repository.** Every generated artifact is written to `./.pks/brain/` inside the repo you are standing in. Commands that write there exit with code 1 outside a repo.
- **Claude Code session history** at `~/.claude/projects`. This is the only input. A machine that has never run Claude Code has nothing to ingest.
- **Claude billing or an API key**, for steps 4 and later. The AI phases invoke a Claude-capable backend and use whatever authentication is already configured on the machine. Steps 2 and 3 need neither.

## 2. Initialize the layout

```bash
pks brain init
```

This creates the global root at `~/.pks-cli/brain/` and, inside a git repository, the per-project directory `./.pks/brain/`. The project directory is appended to the nearest `.gitignore`, so generated artifacts stay out of your commits. The command is idempotent — re-running it is safe.

Outside a git repository the per-project step is skipped with a warning and global init still succeeds.

## 3. Ingest your session history

```bash
pks brain ingest
```

Ingest walks every session JSONL under `~/.claude/projects` and parses prompts, tool calls, file operations, and errors into four append-only firehose files under the global root. It is fully deterministic — no model calls, no cost. A per-session cursor based on file modification time means repeat runs only reprocess changed sessions.

A progress bar runs during the walk, then a summary table prints files scanned, ingested, skipped, and failed, along with the counts appended to each firehose.

Confirm the result:

```bash
pks brain status
```

You should see global totals for projects, sessions, prompts, tool calls, file operations, and errors, plus the last ingest time.

## 4. Plan the AI phases before spending

```bash
pks brain refresh --dry-run
```

Every AI phase plans before it spends. A dry run enumerates the eligible sessions and clusters and prints a combined cost and time estimate for extract, synth, wiki, and adr.

One thing to know: ingest still executes for real during a dry run, because its output count is what makes the downstream estimate accurate. Only the AI phases are simulated.

If the estimate is larger than you want, narrow it with `--since`:

```bash
pks brain refresh --since 7d --dry-run
```

## 5. Extract sessions

```bash
pks brain extract --since 7d
```

Extract summarizes each eligible session into `./.pks/brain/extracts/<session-id>.md` — what was worked on, what struggled, prompt techniques, the user story, and tags — plus a JSON sidecar recording model, tokens, cost, and the hash of the prompt that produced it. The default model is `haiku` and the default parallelism is 10.

A confirmation prompt appears only for large runs: at least 25 eligible sessions and at least $1.00 estimated. Pass `-y` to skip it.

## 6. Synthesize and render

```bash
pks brain synth
pks brain wiki
pks brain adr
```

`synth` clusters the extracts by theme and writes `synthesis/themes.md`, `synthesis/bad-habits.md`, and `synthesis/clusters.json`. `wiki` renders one page per cluster of at least 3 sessions into `wiki/`, plus `wiki/index.md`. `adr` distils clusters tagged as architectural, with at least 5 sessions, into `adr/`, plus `adr/index.md`.

Both `wiki` and `adr` read `clusters.json`, so `synth` has to run first. If you want the cluster index without paying for narratives, `pks brain synth --no-ai` writes `clusters.json` and a deterministic themes skeleton.

## 7. Verify

```bash
pks brain status
```

The per-project section now reports extract count, total and average cost, token totals, and models used. It also lists refresh suggestions — sessions with no extract, extracts older than their source session, and extracts made with a stale prompt version.

Read what was generated:

```bash
cat ./.pks/brain/wiki/index.md
```

Each linked page is a cluster narrative built from real sessions.

## 8. Next steps

- [pks brain refresh](/tools/pks/brain/refresh) — collapse steps 3 through 6 into one gated command
- [pks brain search](/tools/pks/brain/search) — find a prompt, tool call, or error across everything you ingested
- [pks brain commit-plan](/tools/pks/brain/commit-plan) — turn the same session graph into focused commits
- [pks brain skill](/tools/pks/brain/skill) — change the prompts if the extracts are not shaped the way you want
- [pks brain CLI reference](/tools/pks/brain/reference) — every flag and default in one table
