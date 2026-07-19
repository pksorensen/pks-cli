---
title: "pks brain synth"
description: "Cluster per-session extracts by theme and narrate the cross-session story into themes.md, bad-habits.md, and the clusters.json that wiki and adr read."
tags: [how-to, brain, synthesis, ai]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain synth [options]"
examples:
  - command: "pks brain synth --dry-run"
    description: "List eligible clusters and the estimated cost"
  - command: "pks brain synth --no-ai"
    description: "Write clusters.json and a skeleton without spending"
  - command: "pks brain synth --max-clusters 10"
    description: "Narrate only the ten largest themes"
---

Synth is phase 3. It reads every per-session extract in the current repository, groups them into thematic clusters, and writes the cross-session story: `./.pks/brain/synthesis/themes.md`, `bad-habits.md`, and the machine-readable `clusters.json` that both [wiki](/tools/pks/brain/wiki) and [adr](/tools/pks/brain/adr) consume.

One session is a memory. A cluster is a pattern — which is why the default `--min-cluster-size` is 2 and singletons are left out.

## 1. Prerequisites

- **A git repository.** Output goes to `./.pks/brain/synthesis/`, so the command exits with code 1 outside a repo.
- **[pks brain extract](/tools/pks/brain/extract)** must have written at least one extract. With none, synth tells you to run extract and exits 0 — this is not an error.

## 2. Plan the run

```bash
pks brain synth --dry-run
```

Eligible clusters and an estimated cost are printed, and no model is called.

## 3. Synthesize

```bash
pks brain synth
```

Each qualifying cluster gets an AI-written narrative; the remainder still appear in `clusters.json`. Clusters are narrated ten at a time by default.

The confirmation gate fires when the run needs at least 10 model calls and is estimated at $1.00 or more. `--no-ai` and `-y` both bypass it.

## 4. Get the cluster index without spending

```bash
pks brain synth --no-ai
```

This writes `clusters.json` and a deterministic `themes.md` skeleton with no model calls. It is enough to unblock [pks brain adr](/tools/pks/brain/adr), which only needs the cluster index.

## 5. Tune the clustering

```bash
pks brain synth --min-cluster-size 3 --max-clusters 10
```

Raise `--min-cluster-size` on a busy repository to surface only substantial themes; lower it on a small one when nothing qualifies. `--max-clusters` caps how many clusters get a narrative, which is the cheapest lever on cost.

## 6. Verify

```bash
cat ./.pks/brain/synthesis/themes.md
```

Each section corresponds to a cluster in `clusters.json`. `bad-habits.md` holds the recurring anti-patterns found across sessions.

## Options

| Flag | Default | Description |
|---|---|---|
| `--model <name>` | `haiku` | Model name passed to the backend. |
| `--parallel <n>` | `10` | Maximum parallel model invocations. |
| `--max-clusters <n>` | — | Cap how many clusters receive an AI narrative. Others remain in `clusters.json`. |
| `--min-cluster-size <n>` | `2` | Minimum sessions per cluster before it is surfaced. |
| `--no-ai` | `false` | Skip model calls. Writes `clusters.json` and a deterministic `themes.md` only. |
| `--max-budget-usd <amount>` | — | Hard dollar cap per invocation. |
| `--dry-run` | `false` | Plan only — print eligible clusters and the estimate. |
| `-y, --yes` | `false` | Skip the cost-confirmation prompt for large runs. |

## Troubleshooting

**"Run pks brain extract first" and exit 0.** No extracts exist in this repository yet.

**No cluster met the threshold.** The exit code is 0 and a hint is printed. Lower `--min-cluster-size`, or extract more sessions first.

**Exit code 1 immediately.** The working directory is not inside a git repository.

**Wiki and adr say clusters.json is missing.** Synth did not complete. `pks brain synth --no-ai` is the cheapest way to produce that file.

## See also

- [pks brain extract](/tools/pks/brain/extract) — phase 2, the input synth clusters
- [pks brain wiki](/tools/pks/brain/wiki) — phase 4, which renders a page per cluster
- [pks brain adr](/tools/pks/brain/adr) — phase 5, which distils architectural clusters into decision records
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
