---
title: "pks brain refresh"
description: "Run ingest, extract, synth, wiki, and adr in one pass with a single combined cost estimate, one confirmation gate, and automatic downstream skipping."
tags: [how-to, brain, pipeline, ai]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain refresh [options]"
examples:
  - command: "pks brain refresh --dry-run"
    description: "Plan every phase and show the combined estimate"
  - command: "pks brain refresh -y"
    description: "Run the full pipeline without the cost prompt"
  - command: "pks brain refresh --no-ai"
    description: "Ingest and emit skeletons with no model calls"
---

Refresh runs the whole brain pipeline — ingest, extract, synth, wiki, adr — in sequence for the current project. Instead of one confirmation prompt per phase, it produces a single combined cost estimate up front and asks once.

It is the command to put on a habit: run it at the end of a working week and the repository's brain catches up with everything you did.

## 1. Prerequisites

- **A git repository.** Every AI phase writes to `./.pks/brain/`, so refresh exits with code 1 outside a repo.
- **Claude Code session history** at `~/.claude/projects`.
- **Claude billing or an API key**, unless you run with `--no-ai`.

## 2. Plan the whole pipeline

```bash
pks brain refresh --dry-run
```

Every phase is planned and one combined estimate is printed.

> **Note.** Ingest actually executes during a dry run and writes to the global `~/.pks-cli/brain/` layer. That is deliberate — ingest is free and deterministic, and its real output count is what makes the estimate for the AI phases accurate. Only the AI phases are simulated.

## 3. Run it

```bash
pks brain refresh
```

The combined gate fires once, when the total estimated cost across all non-skipped AI phases reaches $1.00. Pass `-y` to skip it.

Downstream phases are skipped automatically when extract produced no new output, which avoids re-synthesizing identical inputs. That decision is re-evaluated after the real extract run against the actual count, so a run that planned to synthesize can still skip synth, wiki, and adr if every extract failed.

## 4. Narrow the window

```bash
pks brain refresh --since 7d -y
```

`--since` applies to both ingest and extract. It accepts `7d`, `24h`, `30m`, or an ISO date, and it is the main lever for keeping a routine refresh cheap.

## 5. Skip phases

```bash
pks brain refresh --skip-ingest --skip-adr
```

Each phase has its own skip flag: `--skip-ingest`, `--skip-extract`, `--skip-synth`, `--skip-wiki`, `--skip-adr`. Use them to re-render one part of the tree without re-running the rest.

To force the downstream phases even when extract produced nothing new:

```bash
pks brain refresh --force
```

## 6. Run without spending

```bash
pks brain refresh --no-ai
```

Ingest still runs in full; synth, wiki, and adr emit their deterministic skeletons. This is a reasonable weekly baseline when you only want the raw layer and the cluster index kept current.

## 7. Verify

```bash
pks brain status
```

The refresh suggestions section is the fastest check: it names sessions with no extract, extracts older than their source, and extracts made with a stale prompt version. An empty list means the brain is current.

## Options

| Flag | Default | Description |
|---|---|---|
| `--model <name>` | `haiku` | Model name passed to the backend for every AI phase. |
| `--parallel <n>` | `10` | Maximum parallel model invocations per phase. |
| `--since <window>` | — | Only ingest and extract sessions newer than this. Accepts `7d`, `24h`, `30m`, or an ISO date. |
| `--max-budget-usd <amount>` | — | Hard dollar cap forwarded to each invocation. |
| `--no-ai` | `false` | Skip every model call. Ingest still runs; later phases emit skeletons. |
| `--dry-run` | `false` | Plan every phase and print the combined estimate. |
| `--force` | `false` | Run synth, wiki, and adr even when no new extracts were produced. |
| `--skip-ingest` | `false` | Skip the deterministic ingest pass. |
| `--skip-extract` | `false` | Skip the per-session extract pass. |
| `--skip-synth` | `false` | Skip the cross-session synthesis pass. |
| `--skip-wiki` | `false` | Skip the wiki render pass. |
| `--skip-adr` | `false` | Skip the ADR render pass. |
| `-y, --yes` | `false` | Skip the combined cost-confirmation prompt. |

## Troubleshooting

**A dry run modified the global brain directory.** Expected. Ingest is never simulated. Add `--skip-ingest` if you want a plan that touches nothing.

**Synth, wiki, and adr were skipped without asking.** Extract produced no new output. Pass `--force` to run them anyway.

**Exit code 1 immediately.** The working directory is not inside a git repository.

**The estimate is much higher than expected.** The first refresh on a machine with long history plans every session. Add `--since 7d` and build up in slices.

## See also

- [Quickstart: build your first brain](/tools/pks/brain/quickstart) — the same pipeline run one phase at a time
- [pks brain extract](/tools/pks/brain/extract) — the phase that dominates the cost estimate
- [pks brain status](/tools/pks/brain/status) — the dashboard that tells you when a refresh is due
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
