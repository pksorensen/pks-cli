---
title: "pks claude usage"
description: "Price every billed Claude Code request from local transcripts, deduplicated and charted hourly and daily, with a top-models-by-cost breakdown."
tags: [how-to, analytics, cost, claude-code]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks claude usage [PROJECT] [options]"
---

`pks claude usage` turns your local Claude Code transcripts into a cost report. It parses `~/.claude/projects/**/*.jsonl` into billed-request rows, folds duplicates, prices what remains, and charts the result hourly and daily.

## Prerequisites

- **Prior Claude Code usage on this machine.** Without transcripts the command has nothing to price.
- **Outbound access to raw.githubusercontent.com.** Optional. Used once per day to refresh the pricing table; there is a fallback.

## How the numbers are produced

Three mechanics decide whether you trust the output.

- **Deduplication is global.** A single API response is written to the transcript as several JSONL rows, one per content block, all sharing the same `requestId` and `message.id`. The command folds on that pair across every file it read, not per file.
- **Pricing has three tiers.** The transcript's own `costUSD` wins when present. Otherwise a live-fetched LiteLLM pricing table is consulted. Otherwise a small hardcoded table covers haiku-4-5, sonnet-4-6, opus-4-6, opus-4-7, and opus-4-8.
- **Parsing is cached.** `~/.pks-cli/usage-cache/manifest.json` is keyed by file size and modification time, so unchanged transcripts are never re-parsed on later runs.

## 1. See total cost

```bash
pks claude usage
```

You get an hourly cost chart covering the last 24 hours, a daily cost chart, a cost summary, and a top-five-models-by-cost table across every project.

## 2. Narrow to one project

```bash
pks claude usage my-project
```

`PROJECT` is a folder name under `~/.claude/projects/`, matched as a substring — you do not need the full mangled path.

## 3. Narrow to specific sessions

```bash
pks claude usage -s a1b2c3d4 -s e5f6a7b8
```

A session id is the `.jsonl` filename, matched as a prefix or substring across all projects. The flag repeats. Given both a `PROJECT` and one or more `--session` values, the filters intersect.

## 4. Change the highlight window

```bash
pks claude usage --days 14
```

The most-recent N days are highlighted in red. The default is 7.

## Options

| Argument | Required | Description |
|---|---|---|
| `PROJECT` | no | Folder name under `~/.claude/projects/`, matched as a substring. Omit to scan every project. |

| Flag | Default | Description |
|---|---|---|
| `-s`, `--session <SESSION>` | — | Session id, matched as a prefix or substring across all projects. Repeatable. Intersects with `PROJECT`. |
| `-d`, `--days <N>` | `7` | Number of most-recent days highlighted in red. |

## Verify

```bash
pks claude usage --days 1
```

You should see the cost summary and top-models table with only the last day highlighted. A run that finds nothing prints that no Claude Code session files were found and returns `0`.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Prices look approximate for a new model | The live pricing fetch failed or timed out after 10 seconds, and the hardcoded fallback covers only five model families. Restore outbound network access and rerun. |
| Nothing found, but exit code is `0` | Unlike `stats`, an empty result is not an error here. Check the `PROJECT` substring or drop it. |
| A project-scoped run shows cost you attribute elsewhere | Deduplication folds globally, so a request whose rows appear in more than one transcript is counted once, under whichever scope survived the fold. Rare, but it is why scoped totals can differ from your expectation. |
| Results do not change after editing transcripts | The cache keys on size and modification time. A change that preserves both is not detected. |

## See also

- [pks claude stats](/tools/pks/claude/stats) — the latency and volume view over the same files
- [pks claude limits](/tools/pks/claude/limits) — remaining quota rather than spent cost
- [pks claude reference](/tools/pks/claude/reference) — every command and flag in the branch
