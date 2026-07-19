---
title: "pks brain ingest"
description: "Parse every Claude Code session log into four append-only firehose files under the global brain root — deterministic, cursor-based, and free to run."
tags: [how-to, brain, ingest, claude]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain ingest [options]"
examples:
  - command: "pks brain ingest"
    description: "Ingest every changed session since the last run"
  - command: "pks brain ingest --since 7d"
    description: "Ingest only sessions from the last seven days"
  - command: "pks brain ingest --project agentic-live --force"
    description: "Re-parse one project from scratch"
---

Ingest is phase 1 of the brain pipeline and the only phase everything else depends on. It walks every Claude Code session transcript under `~/.claude/projects` and parses prompts, tool calls, file operations, and errors into four append-only files under the global root at `~/.pks-cli/brain/`.

It is fully deterministic. No model is called, nothing is billed, and nothing leaves the machine. Running it often is the intended usage.

## 1. Prerequisites

- **`pks brain init`** — creates the global root. Run it once. Most leaf commands create directories lazily, but init is the documented starting point.
- **Claude Code session history** at `~/.claude/projects`. This is the sole input, and it is global rather than scoped to the repo you are standing in.

## 2. Run a first full ingest

```bash
pks brain ingest
```

The command discovers every session JSONL, filters by the options you passed, and parses each matched file in parallel with a live progress bar. A per-session cursor based on file modification time is written as it goes, so the next run only reprocesses sessions that changed.

When it finishes, a summary table reports files scanned, ingested, skipped, and failed, plus the number of prompts, tool calls, file operations, and errors appended.

## 3. Narrow the scope

Three filters compose. Use them when the full history is larger than you need.

```bash
pks brain ingest --project agentic-live    # match the encoded project-slug substring
pks brain ingest --since 24h               # only sessions newer than this
pks brain ingest --limit 50                # cap the number of files processed
```

`--since` accepts a relative shorthand — `7d`, `24h`, `30m` — or an ISO date. Anything else exits with code 1 before any work starts.

## 4. Force a re-parse

```bash
pks brain ingest --force
```

`--force` ignores the per-session cursor and re-parses every matched file. Reach for it after a pks upgrade changes how sessions are parsed, or when the firehose looks incomplete.

## 5. Tune throughput

```bash
pks brain ingest --parallel 4 --quiet
```

`--parallel` overrides the maximum degree of parallelism, which defaults to the CPU count. `--quiet` suppresses the progress bar and prints only the final summary — the form to use inside a script or another command's output.

## 6. Verify

```bash
pks brain status
```

The global section reports projects, sessions, prompts, tool calls, file operations, errors, and the last ingest time. If the totals are all zero, no session files matched — check that `~/.claude/projects` exists and that any `--project` filter matches the encoded slug rather than the human directory name.

## Options

| Flag | Default | Description |
|---|---|---|
| `-p, --project <slug>` | — | Match against the encoded project-slug substring, for example `agentic-live`. |
| `--since <window>` | — | Only ingest sessions newer than this. Accepts `7d`, `24h`, `30m`, or an ISO date. |
| `--limit <n>` | — | Cap the number of session files processed, applied after filtering. |
| `--force` | `false` | Ignore the per-session cursor and re-parse every matched file. |
| `--parallel <n>` | CPU count | Override the maximum degree of parallelism. |
| `--quiet` | `false` | Suppress the progress bar and print the final summary only. |

## Troubleshooting

**The command exits immediately with code 1.** The `--since` value could not be parsed. Use `Nd`, `Nh`, `Nm`, `Ns`, or an ISO date.

**A repeat run reports everything as skipped.** That is the cursor working — nothing changed since the last ingest. Pass `--force` to re-parse anyway.

**`--project` matches nothing.** The filter is a substring match against the encoded project slug that Claude Code writes into the directory name, not against your repository name as you type it. Run `pks brain status` to see the slugs that exist.

**Ingest wrote data but the project shows no extracts.** Ingest is global; extracts are per-repo. Run [pks brain extract](/tools/pks/brain/extract) from inside the repository you care about.

## See also

- [pks brain extract](/tools/pks/brain/extract) — phase 2, which turns ingested sessions into per-session summaries
- [pks brain search](/tools/pks/brain/search) — query the firehoses ingest produces
- [pks brain refresh](/tools/pks/brain/refresh) — run ingest and every downstream phase in one command
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
