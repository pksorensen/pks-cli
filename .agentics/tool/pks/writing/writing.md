---
title: "pks writing"
description: "Danish-first writing linter, agent-driven rubric scoring, sentence-level naturalness rewrites, and a portable writer profile that moves between machines."
tags: [reference, writing, danish, linting, agents]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks writing <command> [options]"
examples:
  - command: "pks writing init"
    description: "Create the global and per-project writing layout"
  - command: "pks writing lint blog-posts/"
    description: "Deterministic anglicism pass over a folder"
  - command: "pks writing prompt post.md"
    description: "Emit a critique prompt bundle for your own LLM"
  - command: "pks writing accept post.md --from reply.json"
    description: "Validate the reply and write the report sidecar"
  - command: "pks writing naturalness review post.md"
    description: "Pick sentence rewrites interactively"
---

`pks writing` is the writing side of the pks CLI: a Danish-first terminology linter, an LLM-agnostic critique loop, a sentence-level rewrite loop, and a writer profile that carries all of it between machines. It exists because generic grammar tools do not know your voice, and because a critique that vanishes after one post teaches nothing.

## Overview

The commands split into two independent flows that share one profile store. The first flow is terminology and rubric work: `lint` finds anglicisms deterministically, `prompt` and `accept` run a full rubric critique through an agent's own model, and `learn`, `corpus`, and `apply` turn accumulated findings into permanent profile entries. The second flow is `naturalness` — a finer-grained loop that flags awkward sentences, proposes three labelled rewrites each, and lets you pick one.

- **No model is called by pks in the modern flow.** `prompt` emits a bundle, the agent calls its own LLM, `accept` validates the reply against a JSON schema. Only the deprecated `score` spawns a local `claude` process.
- **Everything is local filesystem state.** No auth, no network, no account. Global state lives in `~/.pks-cli/writing/`; per-project overrides live in `./.pks/writing/`.
- **Findings compound.** Each cycle can be promoted into the profile, so the next lint and the next critique start smarter.

## What you get

- **Deterministic anglicism linting.** `pks writing lint` scans markdown against your anglicism list and allowlist, writes a report sidecar per file, and exits 0 regardless of finding count so it never breaks a build — it only exits 1 if the profile's anglicism list is empty (run `pks writing init` first).
- **Agent-driven rubric critique.** `pks writing prompt` bundles the post body, writer profile, channel rubric, and reference samples into one JSON payload; `pks writing accept` validates the reply against a five-dimension schema before persisting it.
- **A learning loop.** `pks writing learn` proposes profile edits from findings, `pks writing corpus` keeps only terms recurring across several posts, and `pks writing apply` commits the accepted ones to the global profile.
- **Sentence-level naturalness rewrites.** `pks writing naturalness` flags awkward sentences, collects three labelled alternatives per sentence from one or more named critics, and applies your picks in place with a versioned archive of the original.
- **A portable profile.** `pks writing profile export` and `import` move the entire learned profile as a single `.tgz`.

## How it fits together

Start with `pks writing init`, which creates `~/.pks-cli/writing/` and seeds a `profile.md` template. Author that profile — either by hand in your editor or by pasting the `profile prompt` output into a chat session and ingesting the JSON reply. From then on, every other command reads that profile.

For a single post the loop is: `lint` for free deterministic findings, then `prompt` → your LLM → `accept` for the rubric critique. Both write into `<postdir>/_review/<stem>.WRITING-REPORT.{json,md}`. When the report has taught you something worth keeping, `learn` turns it into a `<stem>.LEARN.json` proposal with per-action `accept` flags, and `apply` commits the ones still flagged true.

The naturalness loop is separate and runs on the same file: `naturalness prompt` → your LLM → `naturalness accept` writes a per-critic candidates sidecar and merges all critics into a canonical one, `naturalness review` walks it interactively, and `naturalness apply` rewrites the source markdown.

- **Terminology flow** answers *which words are wrong* and permanently fixes the vocabulary.
- **Naturalness flow** answers *which sentences read like a translation* and permanently fixes the phrasing habits.

## Commands

`init` · `lint` · `score` (deprecated) · `prompt` · `accept` · `learn` · `apply` · `corpus` · `skill install` · `profile show` · `profile author` · `profile prompt` · `profile ingest` · `profile export` · `profile import` · `naturalness prompt` · `naturalness accept` · `naturalness review` · `naturalness apply` · `naturalness merge` · `naturalness patterns show` · `naturalness patterns export`

Full flag surface for every command is on the [pks writing CLI reference](/tools/pks/writing/reference).

## Subcommand map

| Command | What it does |
|---|---|
| `pks writing init` | Creates the global and per-project writing layout. |
| `pks writing lint` | Deterministic anglicism pass over a file or folder. |
| `pks writing prompt` | Emits a rubric-critique prompt bundle for an agent's own LLM. |
| `pks writing accept` | Validates the critique reply and writes the report sidecar. |
| `pks writing score` | Deprecated one-shot lint plus critique via a local `claude` process. |
| `pks writing learn` | Turns report findings into a reviewable profile-edit proposal. |
| `pks writing corpus` | Aggregates per-post proposals, keeping only recurring terms. |
| `pks writing apply` | Commits accepted proposal actions to the global profile. |
| `pks writing skill install` | Installs the bundled skill so Claude Code discovers the workflow. |
| `pks writing profile …` | Shows, authors, ingests, exports, and imports the writer profile. |
| `pks writing naturalness …` | Sentence-level rewrite loop plus the learned-patterns store. |

## Next steps

- [Quickstart: lint and score your first post](/tools/pks/writing/quickstart) — go from nothing installed to a scored post with a report sidecar.
- [The writer profile](/tools/pks/writing/profile) — author, inspect, and move the profile that every other command reads.
- [Linting and rubric scoring](/tools/pks/writing/scoring) — the `lint`, `prompt`, and `accept` cycle in detail, including the reply schema.
- [Teaching the profile: learn, corpus, apply](/tools/pks/writing/learning) — how findings become permanent profile entries.
- [Naturalness: sentence-level rewrites](/tools/pks/writing/naturalness) — the prompt, accept, review, apply loop and the patterns store.
- [pks writing CLI reference](/tools/pks/writing/reference) — every command, flag, argument, exit code, and file path.
