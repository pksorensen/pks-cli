---
title: "pks brain wiki"
description: "Render one wiki page per synthesized cluster — overview, user stories, what is built, open threads, and hot files — plus an index linking them all."
tags: [how-to, brain, wiki, ai]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain wiki [options]"
examples:
  - command: "pks brain wiki --dry-run"
    description: "List the pages that would be rendered"
  - command: "pks brain wiki --max-clusters 5"
    description: "Render only the five largest clusters"
  - command: "pks brain wiki --no-ai"
    description: "Write the index from existing clusters only"
---

Wiki is phase 4. It reads `./.pks/brain/synthesis/clusters.json` and renders one markdown page per qualifying cluster into `./.pks/brain/wiki/` — overview, user stories, what has been built, open threads, and hot files — together with `wiki/index.md` linking every page.

The default `--min-cluster-size` is 3. A theme backed by fewer than three sessions is usually too thin to read as a page.

## 1. Prerequisites

- **A git repository.** Output goes to `./.pks/brain/wiki/`, so the command exits with code 1 outside a repo.
- **[pks brain synth](/tools/pks/brain/synth)** must have produced `clusters.json`. Without it, wiki tells you to run synth and exits 0.

## 2. Plan the render

```bash
pks brain wiki --dry-run
```

Eligible pages and the estimated cost are printed with no model calls.

## 3. Render

```bash
pks brain wiki
```

Pages are rendered ten at a time by default. The confirmation gate fires at 10 or more pages and $1.00 or more estimated, unless `--no-ai` or `-y` is passed.

## 4. Render a subset

```bash
pks brain wiki --max-clusters 5 --min-cluster-size 4
```

`--max-clusters` bounds how many pages get an AI rendering; the remaining clusters stay in the index only. `--min-cluster-size` controls which clusters qualify as a page at all.

## 5. Rebuild the index without spending

```bash
pks brain wiki --no-ai
```

Writes `wiki/index.md` from the existing `clusters.json` and skips every model call.

## 6. Verify

```bash
cat ./.pks/brain/wiki/index.md
```

Every entry links to a rendered page in the same directory.

## Options

| Flag | Default | Description |
|---|---|---|
| `--model <name>` | `haiku` | Model name passed to the backend. |
| `--parallel <n>` | `10` | Maximum parallel model invocations. |
| `--max-clusters <n>` | — | Cap how many cluster pages receive an AI rendering. Others stay in the index. |
| `--min-cluster-size <n>` | `3` | Minimum sessions per cluster before it becomes a page. |
| `--no-ai` | `false` | Skip model calls. Writes `wiki/index.md` from the existing `clusters.json` only. |
| `--max-budget-usd <amount>` | — | Hard dollar cap per invocation. |
| `--dry-run` | `false` | Plan only — print eligible pages and the estimate. |
| `-y, --yes` | `false` | Skip the cost-confirmation prompt. |

## Troubleshooting

**"Run pks brain synth first" and exit 0.** `clusters.json` does not exist. `pks brain synth --no-ai` produces it without cost.

**Fewer pages than clusters.** Clusters below `--min-cluster-size` are indexed but not rendered. Lower the threshold to widen the set.

**Exit code 1 immediately.** The working directory is not inside a git repository.

## See also

- [pks brain synth](/tools/pks/brain/synth) — phase 3, which produces the clusters wiki renders
- [pks brain adr](/tools/pks/brain/adr) — phase 5, the architectural counterpart with a higher evidence bar
- [pks brain refresh](/tools/pks/brain/refresh) — run every phase with a single cost gate
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
