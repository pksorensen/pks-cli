---
title: "Naturalness: sentence-level rewrites"
description: "Flag awkward sentences, collect three labelled rewrites each from one or more critics, pick interactively, and apply them in place with a versioned archive."
tags: [how-to, writing, naturalness, agents]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks writing naturalness <prompt|accept|review|apply|merge|patterns>"
---

The naturalness loop works one sentence at a time. It flags phrasing that reads like a translation, asks a critic for three labelled alternatives per flagged sentence, lets you pick one, and rewrites the source markdown in place. Every accepted rewrite is remembered in a global patterns store, so later critiques see what you have already chosen.

This is separate from the rubric critique in [linting and rubric scoring](/tools/pks/writing/scoring) — different prompt, different schema, different sidecars — and the two run happily on the same file.

## 1. Prerequisites

- **`pks writing init` has been run**, so the global store exists.
- **An authored profile.** The naturalness prompt embeds it alongside the patterns store. See [the writer profile](/tools/pks/writing/profile).
- **Your own LLM.** pks calls no model here; it emits a bundle and validates the reply.
- **A terminal**, for step 4. `review` is interactive and not scriptable.

## 2. Emit the candidates bundle

```bash
pks writing naturalness prompt blog-posts/my-post/da.md
```

Stdout is a JSON bundle with `system`, `user`, the reply schema, and `meta`. It carries the candidate sentences extracted from the post, the writer profile, and the global naturalness patterns store. The banner is suppressed so the output stays clean JSON.

Cap how many sentences the critic may surface, and switch to a readable rendering:

```bash
pks writing naturalness prompt post.md --max-candidates 8
pks writing naturalness prompt post.md --format markdown
```

`--max-candidates` defaults to 15.

## 3. Accept one or more critics

Feed the bundle to a model, then submit its reply:

```bash
pks writing naturalness accept post.md --from reply.json --critic opus
```

The reply is validated against the candidates schema: a `post` field plus a `candidates` array where each entry needs an id, a line, the original sentence, the issue, and exactly three labelled alternatives — A, B, and C — each with an `authorlikeness` value between 0 and 1.

The validated reply lands as `_review/<stem>.NATURALNESS-CANDIDATES.<critic>.json`, then all per-critic sidecars are immediately merged into the canonical `_review/<stem>.NATURALNESS-CANDIDATES.json` that `review` reads.

Run several critics against the same post and compare their suggestions side by side:

```bash
pks writing naturalness accept post.md --from opus-reply.json --critic opus
pks writing naturalness accept post.md --from gpt5-reply.json --critic gpt5
```

`--critic` defaults to `opus`. `--model` records the model id in the sidecar. `--max-candidates` caps how many candidates are accepted and defaults to 15. Without `--from`, the command reads stdin.

Validation failures exit 1 with a `RESULT` hint line; a missing file argument exits 2 — the same convention as `pks writing accept`.

## 4. Review the candidates

```bash
pks writing naturalness review blog-posts/my-post/da.md
```

The interactive loop walks every candidate: the original sentence, the flagged issue, and a table of alternatives with rationale and authorlikeness scores. With one critic the labels are A, B, and C; with several they read A-opus, B-opus, A-gpt5, and so on.

At each sentence you can pick an alternative, skip it, type a free-form rewrite with the "other" choice, or quit. An empty custom answer degrades to a skip. Quitting saves partial progress and exits 0, so interrupting mid-review is safe.

Choices are saved incrementally to `_review/<stem>.NATURALNESS-PICKS.json` after each one. Picks already applied by a previous `naturalness apply` run are skipped silently when you re-enter.

## 5. Apply the picks

Preview the diff without touching anything:

```bash
pks writing naturalness apply post.md --dry-run
```

Then apply:

```bash
pks writing naturalness apply post.md
```

The command builds a unified diff of the sentence rewrites and asks for confirmation before writing. Pass `--yes` to skip the prompt in a script.

> **Do not commit.** Without `--dry-run`, `apply` overwrites the source markdown in place. Review the diff before confirming.

Before overwriting, the pre-apply post body is archived to `<locale>.v<N>.md` with version frontmatter injected, and matching sidecars are copied to the same `v<N>` suffix. Archiving is best-effort: if it fails, the apply still proceeds without a versioned snapshot rather than blocking accepted picks. When the canonical frontmatter already has a `version:` key, the injector leaves it untouched and assumes you curated it by hand.

After archiving, the canonical candidates and picks sidecars are deleted so the next cycle starts clean. The report, learn, patterns, and `v<N>` archive files are left alone. Running `review` again therefore needs a fresh `prompt` and `accept` pass.

Every applied rewrite is appended to the global naturalness patterns store as a durable pattern with a trigger summary and an accepted and rejected example. If every pick in the file is already marked applied, the command is a no-op and exits 0.

## 6. Inspect the learned patterns

```bash
pks writing naturalness patterns show
```

This renders the global store as a table: trigger summary, accepted example, and acceptance count. An empty store prints a grey hint rather than an error.

Export the raw markdown rendering to stdout or a file:

```bash
pks writing naturalness patterns export
pks writing naturalness patterns export ~/patterns.md
```

Writing to a file auto-creates missing parent directories. An empty store writes `(empty)` to stderr and exits 0.

## 7. Repair a broken merge

If a per-critic sidecar was hand-edited or deleted, regenerate the canonical file without re-running `accept`:

```bash
pks writing naturalness merge post.md
```

`--max-alternatives-per-line` caps per-line alternatives in the merged file and defaults to 6; when more arrive, the lowest authorlikeness entries are dropped.

```bash
pks writing naturalness merge post.md --max-alternatives-per-line 9
```

`accept` already calls this merge internally, so this command is only needed for repair.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `review` exits 1 with no candidates. | No candidates sidecar exists. Run `naturalness prompt` then `naturalness accept` first. |
| `accept` exits 1 with a `RESULT` line. | The reply failed the candidates schema — often not exactly three A, B, C alternatives. Act on `hint` and resubmit. |
| `accept` exits 2. | The file argument is missing or the file does not exist. |
| `merge` reports no per-critic sidecars. | `accept` was never run for any critic on this post. |
| `review` shows nothing after an apply. | The canonical sidecars were deleted by design. Re-run `prompt` and `accept`. |
| `apply` reports nothing to do. | Every pick in the picks file is already marked applied. |
| No `v<N>.md` archive appeared. | Archiving is best-effort and failed silently. The rewrites still landed. |
| The patterns table is empty. | No `naturalness apply` has run on this machine yet. |

## Next steps

- [Linting and rubric scoring](/tools/pks/writing/scoring) — the parallel rubric critique that runs on the same file.
- [Teaching the profile: learn, corpus, apply](/tools/pks/writing/learning) — the terminology-side learning loop.
- [The writer profile](/tools/pks/writing/profile) — the store the naturalness prompt embeds.
- [pks writing CLI reference](/tools/pks/writing/reference) — every flag, exit code, and sidecar path.
