---
title: "pks brain status"
description: "Show what the brain currently knows — global raw totals, per-project extract cost and token stats, and the sessions that are due for a refresh."
tags: [how-to, brain, status, diagnostics]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain status"
examples:
  - command: "pks brain status"
    description: "Print global totals and per-project extract stats"
---

Status is the dashboard for the brain. It reports what the global raw layer contains, what the current repository has extracted and what that cost, and which sessions are due for a refresh. It reads local files only — no model calls, no cost.

## 1. Prerequisites

- **`pks brain init`**, or an earlier [pks brain ingest](/tools/pks/brain/ingest), so that `~/.pks-cli/brain/index.json` exists. Without it the command prints a note and exits 0.
- **Run it from inside a git repository** to see the per-project section. Outside one, only the global section appears.

## 2. Run it

```bash
pks brain status
```

The global section reports the number of projects, sessions, prompts, tool calls, file operations, and errors ingested so far, plus the timestamp of the last ingest.

The per-project section reports the extract count, total and average cost, token totals, and the models used.

## 3. Read the refresh suggestions

The last section is the actionable one. It calls out three conditions:

- **Sessions with no extract.** Ingested, never summarized. Run [pks brain extract](/tools/pks/brain/extract).
- **Extracts older than their source session.** The session continued after the extract was written. Re-extract that slice.
- **Extracts made with a stale prompt version.** The stored skill hash differs from the currently-resolved `brain-extract` skill. Run `pks brain extract --force` after changing a prompt.

An empty suggestions list means the repository's brain is current.

## 4. Verify

Compare the global session count with what you expect on disk. If it is zero after an ingest, no session files matched — see the troubleshooting notes in [pks brain ingest](/tools/pks/brain/ingest).

## Troubleshooting

**"Brain not initialized yet".** `~/.pks-cli/brain/index.json` does not exist. Run `pks brain init`, then `pks brain ingest`.

**No per-project section.** The working directory does not resolve to a git project root. Change into the repository you care about and re-run.

**The stale-prompt check is missing.** That check depends on resolving the current `brain-extract` skill. When it cannot be read, the check is skipped rather than reported as an error. Confirm the skill resolves with `pks brain skill show brain-extract`.

## See also

- [pks brain refresh](/tools/pks/brain/refresh) — act on the suggestions in a single command
- [pks brain extract](/tools/pks/brain/extract) — the phase whose cost and token totals status reports
- [pks brain skill](/tools/pks/brain/skill) — inspect the prompt version status compares against
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
