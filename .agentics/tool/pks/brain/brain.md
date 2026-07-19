---
title: "pks brain"
description: "Turn your own Claude Code session history into a searchable personal knowledge base — ingest, extract, synthesize, and render a wiki plus ADRs."
tags: [concept, brain, knowledge-base, claude]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain <command> [options]"
examples:
  - command: "pks brain init"
    description: "Create the global and per-project brain layout"
  - command: "pks brain ingest"
    description: "Parse Claude session logs into the raw firehoses"
  - command: "pks brain status"
    description: "Show what the brain currently knows"
  - command: "pks brain refresh --dry-run"
    description: "Plan the whole pipeline and print a cost estimate"
  - command: "pks brain search Keycloak --since 7d"
    description: "Full-text search recent prompts, tools, and errors"
---

`pks brain` is a personal knowledge base built from the Claude Code session logs already sitting on your machine. It reads `~/.claude/projects/**/*.jsonl`, turns those sessions into structured data, and — with a few AI passes — writes per-session extracts, cross-session themes, wiki pages, and architecture decision records into the git repo you are standing in.

## Overview

Every Claude Code session you run leaves a JSONL transcript on disk: your prompts, the assistant's replies, every tool call, every file it touched, every error it hit. That history is complete and local, and it is unreadable by hand. `pks brain` is the pipeline that makes it readable.

- **Deterministic first.** Ingest, search, scan, conversation export, and commit planning read local files only. No model calls, no cost, no network.
- **AI second, with a plan.** Extract, synth, wiki, and adr call a Claude-capable backend. Every one of them plans first — eligible items plus a cost and time estimate — before spending anything.
- **Two storage layers.** Raw ingested data is global at `~/.pks-cli/brain/`. Generated artifacts are per-repo at `./.pks/brain/`.
- **Local scope only.** Everything is scoped to one user's session logs and the current git repository. This is not a shared or team knowledge base.

## What you get

- **A raw firehose.** Ingest parses every session into four append-only files — prompts, tool calls, file operations, errors — with a per-session cursor so repeat runs stay cheap.
- **Per-session extracts.** One markdown summary per session: what was worked on, what struggled, which prompt techniques were used, the user story, and tags. Each carries a JSON sidecar with model, tokens, cost, and skill hash.
- **Cross-session synthesis.** Extracts are clustered by theme and narrated into `themes.md` and `bad-habits.md`, plus a machine-readable `clusters.json`.
- **A generated wiki.** One page per qualifying cluster — overview, user stories, what is built, open threads, hot files — plus an index.
- **Drafted ADRs.** Clusters tagged as architectural become Status/Context/Decision/Alternatives/Consequences/Evidence documents.
- **Session forensics.** Search across the firehose, scan which sessions touched a given file, export a single session as readable markdown, and group uncommitted files by the session that produced them.

## How it fits together

The pipeline runs in five phases, each consuming the previous one's output. `ingest` walks the Claude session logs and writes the global raw layer. `extract` summarizes each ingested session into `./.pks/brain/extracts/`. `synth` clusters those extracts and writes `./.pks/brain/synthesis/`, including the `clusters.json` that both later phases read. `wiki` and `adr` each render from those clusters, with different evidence thresholds — a wiki page needs 3 sessions by default, an ADR needs 5.

`pks brain refresh` runs all five in order with a single combined cost estimate and one confirmation prompt, and skips the downstream phases when extract produced nothing new. The remaining commands sit outside the pipeline: `search`, `scan filepath`, `conversation`, and `commit-plan` are ad-hoc tools, and `skill` manages the editable prompts that drive the four AI phases.

- **Global, cross-project:** `~/.pks-cli/brain/` — the raw ingested firehoses.
- **Per-repo, generated:** `./.pks/brain/` — extracts, synthesis, wiki, adr, conversations. `pks brain init` adds it to `.gitignore`.

## Commands

`init` · `ingest` · `extract` · `synth` · `wiki` · `adr` · `refresh` · `status` · `search` · `commit-plan` · `scan filepath` · `conversation` · `skill list` · `skill init` · `skill show`

| Command | Phase | What it does |
|---|---|---|
| `pks brain init` | setup | Creates the global and per-project layout, and gitignores the project directory. |
| [`pks brain ingest`](/tools/pks/brain/ingest) | 1 | Parses Claude session JSONL into the global raw firehoses. Deterministic. |
| [`pks brain extract`](/tools/pks/brain/extract) | 2 | AI-summarizes each session into a per-session markdown extract. |
| [`pks brain synth`](/tools/pks/brain/synth) | 3 | Clusters extracts and narrates cross-session themes and bad habits. |
| [`pks brain wiki`](/tools/pks/brain/wiki) | 4 | Renders one wiki page per qualifying cluster, plus an index. |
| [`pks brain adr`](/tools/pks/brain/adr) | 5 | Distils architectural clusters into ADRs, plus an index. |
| [`pks brain refresh`](/tools/pks/brain/refresh) | 1–5 | Runs the whole pipeline with one combined estimate and gate. |
| [`pks brain status`](/tools/pks/brain/status) | — | Dashboard of raw totals, extract stats, and refresh suggestions. |
| [`pks brain search`](/tools/pks/brain/search) | — | Full-text search across the firehoses and the project's extracts. |
| [`pks brain commit-plan`](/tools/pks/brain/commit-plan) | — | Groups uncommitted files by the session that produced them. |
| [`pks brain scan filepath`](/tools/pks/brain/scan) | — | Finds every session whose tool calls touched a path. |
| [`pks brain conversation`](/tools/pks/brain/conversation) | — | Exports one session as readable markdown. |
| [`pks brain skill`](/tools/pks/brain/skill) | — | Lists, installs, and shows the editable prompts behind the AI phases. |

For the full flag surface of every command in one place, read the [pks brain CLI reference](/tools/pks/brain/reference).

## Defaults

| Setting | Value |
|---|---|
| Global root | `~/.pks-cli/brain/` |
| Per-project root | `./.pks/brain/` |
| Session source | `~/.claude/projects` |
| AI model | `haiku` |
| Parallel AI calls | `10` |
| Summarizer backend | `pks` |

These are resolved from your home directory and the current git repository. There is no `PKS_HOME` or `PKS_CONFIG` override for the brain root — `HOME` (or `USERPROFILE` on Windows) determines both paths.

## Next steps

- [Quickstart: build your first brain](/tools/pks/brain/quickstart) — the shortest path from zero to a wiki page you can read
- [pks brain ingest](/tools/pks/brain/ingest) — the deterministic phase that everything else depends on
- [pks brain refresh](/tools/pks/brain/refresh) — run the whole pipeline in one command with one cost gate
- [pks brain commit-plan](/tools/pks/brain/commit-plan) — split a pile of uncommitted changes into session-coherent commits
- [pks brain skill](/tools/pks/brain/skill) — edit the prompts that drive extract, synth, wiki, and adr
- [pks brain CLI reference](/tools/pks/brain/reference) — every command, flag, and default
