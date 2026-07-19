---
title: "pks brain adr"
description: "Distil architectural clusters into standard decision records — status, context, decision, alternatives, consequences, and the sessions that evidence them."
tags: [how-to, brain, adr, ai]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain adr [options]"
examples:
  - command: "pks brain adr --dry-run"
    description: "List ADR candidates and the estimated cost"
  - command: "pks brain adr --max-adrs 5"
    description: "Render only five decision records this run"
  - command: "pks brain adr --include-tag rsc --include-tag monorepo"
    description: "Widen the architectural tag allowlist"
---

Adr is phase 5. It reads `./.pks/brain/synthesis/clusters.json`, selects the clusters tagged as architectural, and writes each one as a standard architecture decision record under `./.pks/brain/adr/` — Status, Context, Decision, Alternatives, Consequences, Evidence — plus `adr/index.md`.

The evidence bar is deliberately higher than the wiki's: `--min-cluster-size` defaults to 5, because a decision needs more supporting sessions than a topic page does.

## 1. Prerequisites

- **A git repository.** Output goes to `./.pks/brain/adr/`, so the command exits with code 1 outside a repo.
- **[pks brain synth](/tools/pks/brain/synth)** must have produced `clusters.json`. `pks brain synth --no-ai` is enough — adr only needs the deterministic cluster index.

## 2. Plan the run

```bash
pks brain adr --dry-run
```

Candidates and the estimated cost are printed with no model calls. On a small repository this is often where you discover that nothing qualifies yet.

## 3. Render decision records

```bash
pks brain adr
```

Records are rendered ten at a time by default. The confirmation gate fires at 10 or more renders and $1.00 or more estimated, unless `--no-ai` or `-y` is passed.

## 4. Control which clusters count as architectural

Two flags shape the tag filter, and they behave differently:

```bash
pks brain adr --include-tag rsc --include-tag monorepo   # add to the built-in allowlist
pks brain adr --tags auth,storage,deploy                 # replace the built-in allowlist
```

`--include-tag` is repeatable and additive. `--tags` is a comma-separated full replacement of the *default* built-in allowlist. Passing both is legal, and there is no priority between them: `--include-tag` values are always added on top, whether or not `--tags` is also supplied. The final allowlist is the union — `--tags` (or the default allowlist if `--tags` is omitted) plus every `--include-tag` value.

## 5. Lower the bar on a young repository

```bash
pks brain adr --min-cluster-size 3 --max-adrs 5
```

`--min-cluster-size` is the most common reason a run produces nothing. `--max-adrs` caps how many records are AI-rendered in one pass.

## 6. Verify

```bash
cat ./.pks/brain/adr/index.md
```

Each entry links to a record in the same directory. The Evidence section of each record names the sessions it was built from — treat these as drafts to review, not as ratified decisions.

## Options

| Flag | Default | Description |
|---|---|---|
| `--model <name>` | `haiku` | Model name passed to the backend. |
| `--parallel <n>` | `10` | Maximum parallel model invocations. |
| `--max-adrs <n>` | — | Cap how many records are AI-rendered in this run. |
| `--min-cluster-size <n>` | `5` | Minimum sessions per cluster before it is considered for a record. |
| `--include-tag <tag>` | — | Extra tag to count as architectural. Repeatable, additive to the built-in allowlist. |
| `--tags <a,b,c>` | — | Comma-separated tags that replace the built-in architectural allowlist. |
| `--no-ai` | `false` | Skip model calls. Writes the deterministic `adr/index.md` only. |
| `--max-budget-usd <amount>` | — | Hard dollar cap per invocation. |
| `--dry-run` | `false` | Plan only — list candidates and the estimate. |
| `-y, --yes` | `false` | Skip the cost-confirmation prompt. |

## Troubleshooting

**Clusters exist but nothing was rendered, exit 0.** No cluster passed both the tag filter and the size threshold. Lower `--min-cluster-size` or widen the filter with `--include-tag`. This is the most common surprise on smaller repositories.

**"Run pks brain synth --no-ai first" and exit 0.** `clusters.json` is missing.

**Exit code 1 immediately.** The working directory is not inside a git repository.

## See also

- [pks brain synth](/tools/pks/brain/synth) — phase 3, which produces the clusters and their tags
- [pks brain wiki](/tools/pks/brain/wiki) — phase 4, the lower-threshold narrative counterpart
- [pks brain skill](/tools/pks/brain/skill) — edit the `brain-adr` prompt that shapes each record
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
