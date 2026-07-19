---
title: "Quickstart: lint and score your first post"
description: "Bootstrap the writing store, author a writer profile, run a deterministic anglicism lint, and get a full rubric critique from your own LLM in one pass."
tags: [quickstart, writing, danish, agents]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks writing init && pks writing lint post.md"
---

Get a Danish blog post linted and scored in a few minutes: create the writing store, author a writer profile, run the free deterministic lint, then hand a critique prompt to your own model and validate the reply back into a report sidecar.

This page covers the first end-to-end pass. For the full flag surface, see the [pks writing CLI reference](/tools/pks/writing/reference).

## 1. Prerequisites

- **The pks CLI on your PATH.** Install it as a .NET global tool with `dotnet tool install -g pks-cli`, or without .NET via `npm install -g @pks-cli/cli`.
- **A markdown file to work on.** Any `.md` file. The examples below use a blog post.
- **An LLM you can call yourself.** Step 5 hands a prompt bundle to a model of your choosing. pks does not call one for you — Claude Code, an API script, or a chat window all work.
- **A git repository**, only if you want the per-project layer. The global layer works in any directory.

## 2. Create the writing store

```bash
pks writing init
```

This creates `~/.pks-cli/writing/` and seeds a `profile.md` template on first run. When the current directory is inside a git repository, it also creates `./.pks/writing/` and adds it to the nearest `.gitignore`. Outside a repository the project layer is skipped with a yellow warning, not an error.

Add `--dry-run` to see what would be created without touching the filesystem.

> **Note.** Re-running `init` never overwrites an existing `profile.md`. It is safe to run again.

## 3. Author the profile

The profile is what makes the linter and the critic sound like you rather than a generic style guide.

```bash
pks writing profile author
```

The interactive menu offers two paths: print a "cowork" authoring prompt to paste into a model that already knows your writing, or open `profile.md` in `$EDITOR` for manual authoring. If `$EDITOR` is unset, the editor path searches PATH for `code`, `nano`, `vim`, and `vi`.

If you took the cowork path, save the model's JSON reply and ingest it:

```bash
pks writing profile ingest ~/Downloads/cowork-reply.md
```

The bundle may be raw JSON or markdown containing a fenced `json` block. Confirm the result:

```bash
pks writing profile show
```

You should see the resolved `profile.md` plus counts and paths for anglicisms, allowlist terms, the active channel, and reference samples.

## 4. Run the deterministic lint

```bash
pks writing lint blog-posts/my-post/da.md
```

The lint is offline and calls no model. It scans against your anglicism list and allowlist and writes `blog-posts/my-post/_review/da.WRITING-REPORT.{json,md}`. Up to 20 findings render in the terminal table; the full set is in the sidecar. A file with zero findings has any stale sidecar deleted.

Pass a folder to recurse over `*.md`, skipping `node_modules/`, `_review/`, and `.pks/`:

```bash
pks writing lint blog-posts/
```

Lint is informational by design and exits 0 for any number of findings, so it will not break a pipeline — except it exits 1 if the profile's anglicism list is empty (run `pks writing init` first).

## 5. Score with your own LLM

Emit the critique bundle:

```bash
pks writing prompt blog-posts/my-post/da.md
```

Stdout is a self-contained JSON bundle: a system prompt, a user prompt, the reply JSON schema, and metadata. The post body, writer profile, channel rubric, and reference samples are all embedded. The pks banner is suppressed for this command so the output stays pipe-clean.

Feed that bundle to your model, save the reply as `reply.json`, and submit it:

```bash
pks writing accept blog-posts/my-post/da.md --from reply.json --model haiku
```

The reply is validated against the score schema — five dimension scores from 1 to 5 plus notes, with finding line numbers checked against the source file's actual line count. On success the critique is merged with existing lint findings and written to the report sidecar. You can pipe instead of using `--from`:

```bash
your-llm-call | pks writing accept blog-posts/my-post/da.md --model haiku
```

On a schema failure the command exits 1 and prints a machine-readable line whose `hint` field tells an agent what to correct:

```text
RESULT: {"ok":false,"errors":[...],"hint":"..."}
```

## 6. Verify

```bash
pks writing profile show
```

You should see your profile and non-zero anglicism and allowlist counts. Then open the report sidecar at `blog-posts/my-post/_review/da.WRITING-REPORT.md`. It contains the merged lint findings and the rubric critique with per-dimension scores.

## 7. Next steps

- [Teaching the profile: learn, corpus, apply](/tools/pks/writing/learning) — promote these findings into the profile so the next post starts smarter.
- [Naturalness: sentence-level rewrites](/tools/pks/writing/naturalness) — the separate loop that rewrites awkward sentences.
- [The writer profile](/tools/pks/writing/profile) — export the profile and restore it on another machine.
- [pks writing CLI reference](/tools/pks/writing/reference) — every flag, exit code, and sidecar path.
