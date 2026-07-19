---
title: "Linting and rubric scoring"
description: "Run the deterministic anglicism lint, then drive a full rubric critique through your own LLM with the prompt and accept pair instead of score."
tags: [how-to, writing, linting, scoring, agents]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks writing lint <path> && pks writing prompt <file>"
---

Two passes evaluate a post. `pks writing lint` is deterministic, offline, and free — it matches text against your anglicism list. `pks writing prompt` plus `pks writing accept` is the rubric critique: pks emits a prompt bundle, your agent calls its own model, and pks validates the reply back into a report sidecar.

Both write to the same place, so a scored post carries lint findings and critique findings in one report. Flag details are on the [pks writing CLI reference](/tools/pks/writing/reference).

## 1. Prerequisites

- **`pks writing init` has been run.** `lint` exits 1 when the anglicism list is empty.
- **An authored profile**, so the linter has terms to match and the critic has a voice to compare against. See [the writer profile](/tools/pks/writing/profile).
- **Your own LLM**, for the critique in steps 3 and 4. The lint in step 2 needs nothing.
- **Reference samples for the channel**, optional. Without them the critic runs voice-blind.

## 2. Lint

```bash
pks writing lint blog-posts/my-post/da.md
```

A folder recurses over `*.md`, skipping `node_modules/`, `_review/`, and `.pks/`:

```bash
pks writing lint blog-posts/
```

Each file with findings gets `_review/<stem>.WRITING-REPORT.json` and a matching `.md`. Files with zero findings have any stale sidecar deleted, so the review folder stays clean. The terminal table renders at most 20 findings per file with an "and N more" notice; the sidecar holds all of them.

Use `--quiet` to suppress the per-finding table and print only the summary:

```bash
pks writing lint blog-posts/ --quiet
```

`lint` is informational and intentionally never breaks CI over findings — it exits 0 regardless of finding count, but exits 1 if the profile's anglicism list is empty (run `pks writing init` first).

## 3. Emit the critique bundle

```bash
pks writing prompt blog-posts/my-post/da.md
```

Stdout is plain — no banner, no Spectre markup — so it pipes safely. The default JSON format contains four parts: `system`, `user`, the reply JSON schema, and `meta`. The post body, writer profile, channel rubric, and reference samples are all embedded in the prompts, so the bundle is self-contained.

Two caps control size:

```bash
pks writing prompt post.md --max-references 5 --max-findings 8
```

`--max-references` limits how many reference samples are injected (default 10). `--max-findings` limits how many findings the model may return (default 12).

For a human-readable rendering instead of the JSON bundle:

```bash
pks writing prompt post.md --format markdown
```

## 4. Accept the reply

Feed the bundle to your model, then submit its reply:

```bash
pks writing accept blog-posts/my-post/da.md --from reply.json --model haiku
```

Or pipe it:

```bash
your-llm-call | pks writing accept blog-posts/my-post/da.md --model haiku
```

The reply is validated against the score schema: five dimension scores in the range 1 to 5 plus notes, with every finding's line number checked against the source file's actual line count. On success the critique merges with any existing lint findings and writes the report sidecar. `--model` records which model produced the reply in the report.

> **Note.** Without `--from`, `accept` blocks reading stdin. Always pipe or pass `--from` in agent and CI contexts.

On validation failure the command exits 1 and prints a final machine-readable line:

```text
RESULT: {"ok":false,"errors":["..."],"hint":"..."}
```

An agent reads `hint`, corrects the reply, and resubmits. A missing file argument exits with code 2 instead of 1, so the two failure classes are distinguishable.

## 5. Install the agent skill

To let Claude Code discover this workflow without being told about it:

```bash
pks writing skill install
```

This drops the bundled `pks-writing-score` skill file into `~/.claude/skills/pks-writing-score/`. Overwrite an existing install with `--force`, or choose a different location with `--target <dir>`.

## 6. Verify

```bash
pks writing profile show
```

Then open `blog-posts/my-post/_review/da.WRITING-REPORT.md`. It should contain the merged lint findings and the rubric critique with its per-dimension scores.

## The deprecated score command

`pks writing score` ran lint plus a critique in one shot by spawning a local `claude` process. Its registered description marks it deprecated, and `prompt` plus `accept` replaces it: model invocation moves out of pks entirely, so any agent and any model can drive the critique.

`score` still works and takes `--model` (`haiku`, `sonnet`, or `opus`; default `haiku`), `--budget` (max USD per critique, default `0.50`), and `--lint-only`. It requires a local `claude` CLI and a real subscription unless `--lint-only` is passed, accepts a single file rather than a folder, and falls back to a lint-only report when the critic fails.

```bash
pks writing score post.md --lint-only
```

Prefer `prompt` and `accept` for anything new.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `lint` exits 1 with "anglicism list is empty". | `pks writing init` was skipped, or the profile carries no anglicisms. |
| The lint table stops at 20 findings. | That is the render cap. Read the full set in `_review/<stem>.WRITING-REPORT.json`. |
| A sidecar disappeared after a lint run. | The file now has zero findings, so the stale sidecar was deleted deliberately. |
| `accept` hangs with no output. | It is blocking on stdin. Pipe a reply or pass `--from <file>`. |
| `accept` exits 1 with a `RESULT` line. | Schema validation failed. Act on the `hint` field and resubmit. |
| `accept` exits 2. | The file argument is missing or the file does not exist. |
| The critique ignores your voice. | No reference samples exist for the channel. Add `*.md` files under `~/.pks-cli/writing/reference/{channel}/`. |
| Prior findings vanished from the report. | Only findings whose rule id starts with `Writing.` are preserved across an `accept`. Everything else is replaced by the new critique. |

## Next steps

- [Teaching the profile: learn, corpus, apply](/tools/pks/writing/learning) — turn these findings into permanent profile entries.
- [Naturalness: sentence-level rewrites](/tools/pks/writing/naturalness) — the separate loop that rewrites individual sentences.
- [The writer profile](/tools/pks/writing/profile) — what the linter and critic read.
- [pks writing CLI reference](/tools/pks/writing/reference) — every flag, exit code, and sidecar path.
