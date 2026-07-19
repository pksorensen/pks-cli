---
title: "pks brain search"
description: "Grep across every prompt, tool call, file operation, and error you ever ran through Claude Code, plus the current project's session extracts."
tags: [how-to, brain, search, cli]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain search <query> [options]"
examples:
  - command: "pks brain search streamId"
    description: "Case-insensitive substring across all sources"
  - command: "pks brain search auth --in extracts --limit 5"
    description: "Search only the current project's extracts"
  - command: "pks brain search Keycloak --since 7d"
    description: "Restrict results to the last seven days"
---

Search is a grep over the ingested firehoses: prompts, tool calls, file operations, and errors from every Claude Code session on the machine, plus the current repository's extracts. It is deterministic, costs nothing, and answers questions like *"when did I last touch this token refresh, and what did I say about it?"*

## 1. Prerequisites

- **[pks brain ingest](/tools/pks/brain/ingest)** must have populated the firehoses. Searching before ingest returns nothing.
- **Run from inside the repository** if you want the `extracts` source included — that source is scoped to `./.pks/brain/extracts/`.

## 2. Search everything

```bash
pks brain search streamId
```

The default is a case-insensitive substring match across all sources. Results print as a table of source, timestamp, session, and a snippet.

## 3. Narrow the source

```bash
pks brain search "connection refused" --in errors
pks brain search auth --in extracts --limit 5
```

`--in` accepts `prompts`, `tools`, `files`, `errors`, `extracts`, or `all`. Narrowing the source is also the fix for skewed results — see the note in step 5.

## 4. Filter by time and project

```bash
pks brain search Keycloak --since 7d
pks brain search runner --project agentic-live
```

`--since` accepts `7d`, `24h`, or an ISO date. `--project` restricts to one project slug.

> **Note.** Project filtering is a raw string match against `"projectSlug":"<slug>` inside each JSON line. It is an approximation rather than an exact field comparison, so an unusually short slug can over-match.

## 5. Match precisely

```bash
pks brain search "session_?id" --regex
pks brain search Keycloak --case-sensitive
```

`--regex` treats the query as a regular expression. `--case-sensitive` applies to substring mode only.

Scanning stops as soon as `--limit` results are found, and sources are scanned in a fixed order: prompts, tools, files, errors, extracts. A low limit combined with `--in all` therefore returns results skewed toward prompts. Raise `-n` or set `--in` explicitly when you need coverage of a later source.

## 6. Verify

```bash
pks brain search "" --in prompts
```

An empty query exits with code 1 — that is the expected behavior, and a quick way to confirm the command is wired up. A real query against `prompts` should return rows whose timestamps match sessions you remember.

## Options

| Flag | Default | Description |
|---|---|---|
| `--in <source>` | `all` | Sources to search: `prompts`, `tools`, `files`, `errors`, `extracts`, or `all`. |
| `-n, --limit <n>` | `20` | Maximum number of results. |
| `--since <window>` | — | Only search rows newer than this. Accepts `7d`, `24h`, or an ISO date. |
| `--project <slug>` | all projects | Restrict results to one project slug. |
| `--regex` | `false` | Treat the query as a regular expression. |
| `--case-sensitive` | `false` | Match case. Applies to substring mode only. |

The positional argument `<query>` is required and must be non-empty.

## Troubleshooting

**Exit code 1 with no results.** The query was empty.

**`--in extracts` returns nothing.** That source resolves to the current git repository's `./.pks/brain/extracts/`. From outside a repository it yields nothing without an error.

**Results all come from one source.** The scan stops at `--limit`. Raise it, or pass `--in` to target the source you want.

**Nothing matches a phrase you know you typed.** Confirm the firehose covers that period with `pks brain status`, then re-run ingest with `--since` covering the session.

## See also

- [pks brain ingest](/tools/pks/brain/ingest) — the phase that populates the firehoses search reads
- [pks brain scan filepath](/tools/pks/brain/scan) — the file-oriented counterpart that needs no ingest
- [pks brain conversation](/tools/pks/brain/conversation) — read a whole session once search has found it
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
