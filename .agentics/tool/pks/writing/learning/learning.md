---
title: "Teaching the profile: learn, corpus, apply"
description: "Turn accumulated lint and critique findings into permanent writer-profile entries, with a corpus step that filters out one-off false positives first."
tags: [how-to, writing, profile, learning]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks writing learn <path> && pks writing apply <proposal>"
---

A review that ends at the report teaches nothing. `learn`, `corpus`, and `apply` are the bridge between "the linter found problems" and "the profile permanently knows about them", so the next post starts from a smarter baseline.

The step is deliberately two-phase: `learn` proposes, a human or agent reviews the proposal, and `apply` commits. Flag details are on the [pks writing CLI reference](/tools/pks/writing/reference).

## 1. Prerequisites

- **At least one existing report sidecar.** `learn` reads `_review/<stem>.WRITING-REPORT.json`, which comes from [linting and rubric scoring](/tools/pks/writing/scoring). Files without one are skipped, not errored.
- **An authored profile**, since `apply` writes into it. See [the writer profile](/tools/pks/writing/profile).
- **Several posts already learned**, only if you want the `corpus` step in step 3.

## 2. Propose profile edits

```bash
pks writing learn blog-posts/my-post/da.md
```

This is non-interactive. It reads the file's last report, groups and dedupes the findings heuristically, and writes two sidecars under `_review/`: a machine-readable `<stem>.LEARN.json` where every proposed action carries an `accept` flag, and a human-readable `<stem>.LEARN.md` summary.

A folder recurses over `*.md`, skipping `_review/`, `.pks/`, and `node_modules/`:

```bash
pks writing learn blog-posts/
```

Restrict a folder run with a glob:

```bash
pks writing learn blog-posts/ --filter "da.md"
```

Files that produce zero actions have any stale `LEARN.json` and `LEARN.md` deleted rather than getting an empty proposal. Every run prints a machine-readable summary line:

```text
RESULT: {"files":3,"skipped":1,"actions":11}
```

## 3. Filter across many posts

A term that appears in one post is often a false positive. `corpus` aggregates every per-post `LEARN.json` under a folder and keeps only the terms that recur:

```bash
pks writing corpus blog-posts/
```

The output is `blog-posts/_corpus.LEARN.json` plus a matching `.md`. Raise the threshold when you want stronger evidence:

```bash
pks writing corpus blog-posts/ --min-posts 3
```

`--min-posts` defaults to 2. `--channel` sets the channel label written into the proposal and defaults to `blog`.

> **Note.** `corpus` aggregates existing `LEARN.json` sidecars. It does not read report sidecars itself, so run `pks writing learn` across the posts first.

## 4. Review the proposal

Open the `.LEARN.md` for a readable summary, then edit the `accept` flags in the `.LEARN.json`. Actions left at `accept: true` are applied; actions set to `false` are counted as rejected and skipped. This is the only gate before a permanent profile change, so read it.

## 5. Apply

Preview first:

```bash
pks writing apply blog-posts/my-post/_review/da.LEARN.json --dry-run
```

Then commit:

```bash
pks writing apply blog-posts/my-post/_review/da.LEARN.json
```

Applying a corpus proposal works identically:

```bash
pks writing apply blog-posts/_corpus.LEARN.json
```

Each accepted action adds an allowlist term, an anglicism entry with its Danish alternative, or a dimension-tagged lesson. The store dedupes, so the operation is idempotent. Malformed actions missing a term, lesson, or dimension are recorded as warnings and skipped rather than aborting the run.

> **Do not commit.** `apply` mutates the global profile store at `~/.pks-cli/writing/`, not just the current project. Every future lint and critique on this machine is affected.

## 6. Verify

```bash
pks writing profile show
```

The anglicism and allowlist counts should have grown by the number of accepted actions reported by `apply`. Re-lint the post to confirm the new terms take effect:

```bash
pks writing lint blog-posts/my-post/da.md
```

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `learn` skips every file. | No `_review/<stem>.WRITING-REPORT.json` exists. Run `pks writing lint` or the prompt-and-accept pass first. |
| No `LEARN.json` was written. | The file produced zero actions, so any stale proposal was deleted instead. |
| `corpus` produces nothing. | No per-post `LEARN.json` sidecars exist under the folder, or no term reached `--min-posts`. |
| `apply` reports many rejected actions. | Those actions carry `accept: false` in the JSON. Flip the ones you want. |
| `apply` prints warnings and skips actions. | Those actions are missing a term, lesson, or dimension. The rest still applied. |
| A change on one machine did not follow you. | The profile is machine-local. Move it with `pks writing profile export` and `import`. |

## Next steps

- [Linting and rubric scoring](/tools/pks/writing/scoring) — where the findings that feed `learn` come from.
- [The writer profile](/tools/pks/writing/profile) — inspect the store `apply` writes into, and move it between machines.
- [Naturalness: sentence-level rewrites](/tools/pks/writing/naturalness) — the parallel loop with its own learned-patterns store.
- [pks writing CLI reference](/tools/pks/writing/reference) — every flag and sidecar path.
