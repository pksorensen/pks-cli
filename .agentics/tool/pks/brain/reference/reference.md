---
title: "pks brain CLI reference"
description: "Complete command, flag, argument, and path reference for the pks brain group — the pipeline phases plus search, scan, conversation, commit-plan, and skills."
tags: [reference, brain, cli]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain <command> [options]"
examples:
  - command: "pks brain init"
    description: "Create the global and per-project brain layout"
  - command: "pks brain ingest --since 7d"
    description: "Parse the last week of session logs"
  - command: "pks brain extract --limit 1 --dry-run"
    description: "Plan a single extract without calling a model"
  - command: "pks brain refresh -y"
    description: "Run every phase with no confirmation prompt"
  - command: "pks brain skill show brain-extract"
    description: "Print the prompt the extract phase resolves to"
---

`pks brain` builds a personal knowledge base from the Claude Code session transcripts under `~/.claude/projects`. It has a five-phase pipeline — ingest, extract, synth, wiki, adr — plus ad-hoc tools for search, file-to-session scanning, conversation export, commit planning, and prompt management.

The group is part of the `pks` CLI, a .NET 10 global tool. See [pks](/tools/pks) for install paths and global behavior.

## Synopsis

```text
pks brain <command> [options]
```

```text
init          Create the global and per-project brain layout
ingest        Parse Claude session logs into the global firehoses   [phase 1]
extract       AI-summarize each session into a per-session extract  [phase 2]
synth         Cluster extracts and narrate cross-session themes     [phase 3]
wiki          Render one wiki page per qualifying cluster           [phase 4]
adr           Distil architectural clusters into decision records   [phase 5]
refresh       Run every phase with one combined cost gate
status        Show raw totals, extract stats, refresh suggestions
search        Full-text search the firehoses and project extracts
commit-plan   Group files by the session that produced them
scan filepath Find sessions whose tool calls touched a path
conversation  Export one session as readable markdown
skill         Manage the editable prompts behind the AI phases
```

The `brain` branch itself carries no options. Each leaf declares its own flags, so the form is `pks brain extract --dry-run`, not `pks brain --dry-run extract`.

### Storage paths

| Setting | Value |
|---|---|
| Global root | `~/.pks-cli/brain/` |
| Global index | `~/.pks-cli/brain/index.json` |
| Firehoses | `prompts.jsonl`, `tools.jsonl`, `files.jsonl`, `errors.jsonl` |
| Per-project root | `./.pks/brain/` |
| Per-project artifacts | `extracts/`, `synthesis/`, `wiki/`, `adr/`, `conversations/` |
| Session source | `~/.claude/projects` |

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `HOME` | (platform) | Resolves `~/.pks-cli/brain` and `~/.claude/projects` on Linux and macOS. |
| `USERPROFILE` | (platform) | The same resolution on Windows. |

There is no brain-specific configuration variable. The AI phases use whatever Claude authentication is already configured on the machine, unless `--foundry` routes them through Azure AI Foundry.

### Time windows

`ingest`, `extract`, `refresh`, and `search` accept a relative window for `--since`: `7d`, `24h`, `30m`, or an ISO date. `commit-plan` and `scan filepath` accept an ISO date only.

## init

Creates the global root at `~/.pks-cli/brain/`, and — inside a git repository — the per-project directory `./.pks/brain/`, which is appended to the nearest `.gitignore`. Idempotent. Outside a git repository the per-project step is skipped with a warning and the global step still succeeds.

| Flag | Default | Description |
|---|---|---|
| `--dry-run` | `false` | Show what would be created without touching the filesystem. |

## ingest

Phase 1. Deterministic walk of every session JSONL under `~/.claude/projects`, parsing prompts, tool calls, file operations, and errors into the four append-only firehoses. A per-session cursor based on file modification time means repeat runs only reprocess changed sessions. No model calls.

| Flag | Default | Description |
|---|---|---|
| `-p, --project <slug>` | — | Match against the encoded project-slug substring. |
| `--since <window>` | — | Only ingest sessions newer than this. |
| `--limit <n>` | — | Cap the number of session files processed, after filtering. |
| `--force` | `false` | Ignore the cursor and re-parse every matched file. |
| `--parallel <n>` | CPU count | Override the maximum degree of parallelism. |
| `--quiet` | `false` | Suppress the progress bar. |

An unparseable `--since` value exits with code 1 before any work starts. Full guide: [pks brain ingest](/tools/pks/brain/ingest).

## extract

Phase 2. Summarizes each eligible session into `./.pks/brain/extracts/<session-id>.md` plus a JSON sidecar recording model, tokens, cost, and skill hash. Plans first, then gates runs of at least 25 sessions estimated at $1.00 or more. Requires a git repository.

| Flag | Default | Description |
|---|---|---|
| `-p, --project <slug>` | current directory slug | Project to extract from. |
| `--skill-path <path>` | — | Override the `brain-extract` SKILL.md location. |
| `--since <window>` | — | Only extract sessions newer than this. |
| `--limit <n>` | — | Cap the number of sessions extracted. |
| `--force` | `false` | Re-extract even when the existing extract is newer than the source. |
| `--parallel <n>` | `10` | Maximum parallel model invocations. |
| `--model <name>` | `haiku` | Model name passed to the backend. |
| `--max-budget-usd <amount>` | — | Hard dollar cap per invocation. |
| `--agent <name>` | `pks` | Summarizer backend: `pks` or `claude`. |
| `--foundry` | `false` | Use Azure AI Foundry as the token provider. Applies with `--agent claude`. |
| `--dry-run` | `false` | Show which sessions would be extracted. |
| `-y, --yes` | `false` | Skip the cost-confirmation prompt. |

An `--agent` value other than `pks` or `claude` exits with code 1. The command exits 1 when the run produced zero successful extracts but had failures. Full guide: [pks brain extract](/tools/pks/brain/extract).

## synth

Phase 3. Clusters the extracts and writes `synthesis/themes.md`, `synthesis/bad-habits.md`, and `synthesis/clusters.json`. Requires a git repository. With no extracts present it prints a hint and exits 0.

| Flag | Default | Description |
|---|---|---|
| `--model <name>` | `haiku` | Model name passed to the backend. |
| `--parallel <n>` | `10` | Maximum parallel model invocations. |
| `--max-clusters <n>` | — | Cap how many clusters receive an AI narrative. |
| `--min-cluster-size <n>` | `2` | Minimum sessions per cluster before it is surfaced. |
| `--no-ai` | `false` | Write `clusters.json` and a deterministic `themes.md` only. |
| `--max-budget-usd <amount>` | — | Hard dollar cap per invocation. |
| `--dry-run` | `false` | Plan only. |
| `-y, --yes` | `false` | Skip the cost-confirmation prompt. |

The gate fires at 10 or more model calls and $1.00 or more estimated. Full guide: [pks brain synth](/tools/pks/brain/synth).

## wiki

Phase 4. Renders one page per qualifying cluster into `./.pks/brain/wiki/`, plus `wiki/index.md`. Requires `clusters.json`; without it, prints a hint and exits 0.

| Flag | Default | Description |
|---|---|---|
| `--model <name>` | `haiku` | Model name passed to the backend. |
| `--parallel <n>` | `10` | Maximum parallel model invocations. |
| `--max-clusters <n>` | — | Cap how many cluster pages are AI-rendered. |
| `--min-cluster-size <n>` | `3` | Minimum sessions per cluster before it becomes a page. |
| `--no-ai` | `false` | Write `wiki/index.md` from the existing clusters only. |
| `--max-budget-usd <amount>` | — | Hard dollar cap per invocation. |
| `--dry-run` | `false` | Plan only. |
| `-y, --yes` | `false` | Skip the cost-confirmation prompt. |

Full guide: [pks brain wiki](/tools/pks/brain/wiki).

## adr

Phase 5. Distils clusters that pass the architectural tag filter into decision records under `./.pks/brain/adr/`, plus `adr/index.md`. Requires `clusters.json`, which `pks brain synth --no-ai` is enough to produce.

| Flag | Default | Description |
|---|---|---|
| `--model <name>` | `haiku` | Model name passed to the backend. |
| `--parallel <n>` | `10` | Maximum parallel model invocations. |
| `--max-adrs <n>` | — | Cap how many records are AI-rendered. |
| `--min-cluster-size <n>` | `5` | Minimum sessions per cluster to be considered. |
| `--include-tag <tag>` | — | Extra architectural tag. Repeatable, additive to the built-in allowlist. |
| `--tags <a,b,c>` | — | Comma-separated tags that replace the built-in allowlist. |
| `--no-ai` | `false` | Write the deterministic `adr/index.md` only. |
| `--max-budget-usd <amount>` | — | Hard dollar cap per invocation. |
| `--dry-run` | `false` | Plan only. |
| `-y, --yes` | `false` | Skip the cost-confirmation prompt. |

Full guide: [pks brain adr](/tools/pks/brain/adr).

## refresh

Runs ingest, extract, synth, wiki, and adr in sequence with one combined estimate and one gate at $1.00 total. Ingest always executes, including under `--dry-run`, because its real output count feeds the downstream estimate. Synth, wiki, and adr are skipped when extract produced no new output, unless `--force` is passed.

| Flag | Default | Description |
|---|---|---|
| `--model <name>` | `haiku` | Model name passed to every AI phase. |
| `--parallel <n>` | `10` | Maximum parallel model invocations per phase. |
| `--since <window>` | — | Only ingest and extract sessions newer than this. |
| `--max-budget-usd <amount>` | — | Hard dollar cap forwarded to each invocation. |
| `--no-ai` | `false` | Skip every model call. Ingest still runs. |
| `--dry-run` | `false` | Plan every phase and print the combined estimate. |
| `--force` | `false` | Run synth, wiki, and adr even with no new extracts. |
| `--skip-ingest` | `false` | Skip the ingest pass. |
| `--skip-extract` | `false` | Skip the extract pass. |
| `--skip-synth` | `false` | Skip the synthesis pass. |
| `--skip-wiki` | `false` | Skip the wiki render pass. |
| `--skip-adr` | `false` | Skip the ADR render pass. |
| `-y, --yes` | `false` | Skip the combined cost-confirmation prompt. |

Full guide: [pks brain refresh](/tools/pks/brain/refresh).

## status

Prints global raw-layer totals and the last ingest time, and — inside a project — extract count, total and average cost, token totals, models used, and refresh suggestions. Takes no options. With no `~/.pks-cli/brain/index.json` it prints an initialization note and exits 0. Full guide: [pks brain status](/tools/pks/brain/status).

## search

Full-text search across the firehoses and the current project's extracts. Prints a table of source, timestamp, session, and snippet.

```text
pks brain search <query> [options]
```

| Flag | Default | Description |
|---|---|---|
| `--in <source>` | `all` | `prompts`, `tools`, `files`, `errors`, `extracts`, or `all`. |
| `-n, --limit <n>` | `20` | Maximum number of results. |
| `--since <window>` | — | Only search rows newer than this. |
| `--project <slug>` | all | Restrict results to one project slug. |
| `--regex` | `false` | Treat the query as a regular expression. |
| `--case-sensitive` | `false` | Match case. Substring mode only. |

An empty query exits with code 1. Scanning stops at `--limit`, and sources are scanned in the order prompts, tools, files, errors, extracts. Full guide: [pks brain search](/tools/pks/brain/search).

## commit-plan

Groups files by the Claude sessions whose tool calls touched them. Status: beta.

```text
pks brain commit-plan (--files <paths> | --files-from <path> | --uncommitted) [options]
```

| Flag | Default | Description |
|---|---|---|
| `--files <paths>` | — | Explicit list of file paths. |
| `--files-from <path>` | — | Read file paths, one per line, from the given file. |
| `--uncommitted` | `false` | Detect changed and untracked files from `git status --porcelain`. |
| `--since <date>` | — | Filter sessions by first-entry timestamp, ISO date. |
| `--min-files <n>` | `2` | Minimum files per qualifying group. |
| `--include-bash` | `false` | Also match Bash tool-use entries. |
| `--projects-dir <path>` | `~/.claude/projects` | Override the Claude projects directory. |
| `--format <name>` | `text` | Output format: `text`, `json`, or `jsonl`. |
| `--include-prompts` | `false` | Include up to ten user prompts per group. |
| `--no-refresh` | `false` | Skip the automatic ingest pass before planning. |
| `--force-scan` | `false` | Bypass the firehose graph and use the per-file scanner. |

Exactly one of `--files`, `--files-from`, `--uncommitted` is required; zero or several exits with code 1, as does an unknown `--format`. Full guide: [pks brain commit-plan](/tools/pks/brain/commit-plan).

## scan filepath

Deterministic scan of every session JSONL for tool-use entries touching a file or directory. No ingest required.

```text
pks brain scan filepath <path> [options]
```

| Flag | Default | Description |
|---|---|---|
| `--include-bash` | `false` | Also match Bash tool-use entries whose command contains the path. |
| `--since <date>` | — | Skip sessions whose first entry predates this ISO date. |
| `--projects-dir <path>` | `~/.claude/projects` | Override the Claude projects directory. |
| `--format <name>` | `text` | Output format: `text`, `json`, or `jsonl`. |

Relative windows such as `7d` are not accepted here. Full guide: [pks brain scan filepath](/tools/pks/brain/scan).

## conversation

Deterministic export of one session's human prompts and assistant replies as markdown, with tool traffic collapsed into source references.

```text
pks brain conversation <session> [options]
```

| Flag | Default | Description |
|---|---|---|
| `-o, --output <path>` | `./.pks/brain/conversations/<session-id>.md` | Output markdown path. |
| `--max-message-chars <n>` | `12000` | Maximum characters kept inline per visible text block. |
| `--include-intermediate` | `false` | Keep assistant progress narration between tool calls. |

`<session>` accepts a session ID or a path to a raw JSONL file. A non-positive `--max-message-chars` exits with code 1. Full guide: [pks brain conversation](/tools/pks/brain/conversation).

## skill

Manages the five editable prompts behind the AI phases: `brain-extract`, `brain-synth-cluster`, `brain-synth-habits`, `brain-wiki-page`, and `brain-adr`.

### skill list

Lists every skill with its resolution source, a short content hash, and the command that uses it. Takes no options.

### skill init

Copies an embedded default out to an editable file.

```text
pks brain skill init <name> [options]
```

| Flag | Default | Description |
|---|---|---|
| `--target <dir>` | `~/.claude/skills/<name>/` | Destination directory. |
| `--agents` | `false` | Install into the repository's `.agents/skills/<name>/`. |
| `--force` | `false` | Overwrite an existing file. |

`--target` and `--agents` are mutually exclusive; passing both exits with code 1, as does an unknown skill name.

### skill show

Prints the currently-resolved skill body to stdout with a header naming the source and content hash. Takes no options; the skill name is required. Full guide: [pks brain skill](/tools/pks/brain/skill).

## See also

- [pks brain](/tools/pks/brain) — what the group is and how the phases fit together
- [Quickstart: build your first brain](/tools/pks/brain/quickstart) — the shortest working path
- [pks brain refresh](/tools/pks/brain/refresh) — the one command that drives the whole pipeline
- [pks](/tools/pks) — the parent CLI, its install paths, and global behavior
