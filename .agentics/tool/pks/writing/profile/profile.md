---
title: "The writer profile"
description: "Author, inspect, ingest, and move the writer profile that every pks writing command reads — including the cowork bootstrap and the portable tarball handoff."
tags: [how-to, writing, profile, portability]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks writing profile <show|author|prompt|ingest|export|import>"
---

The writer profile is the state every other `pks writing` command reads: your voice description, the anglicism list, the allowlist, per-channel reference samples, and accumulated lessons. Without it the linter has nothing to match and the critic runs voice-blind.

This page covers authoring the profile and moving it between machines. For the flag surface, see the [pks writing CLI reference](/tools/pks/writing/reference).

## 1. Prerequisites

- **`pks writing init` has been run**, so `~/.pks-cli/writing/` exists with a seeded `profile.md` template. `profile show` and `profile export` fail with a hint pointing at `init` when it has not.
- **A model that already knows your writing**, only if you take the cowork authoring path in step 3. The manual editor path needs no model.
- **`$EDITOR` set**, only if you take the editor path. The command falls back to searching PATH.

## 2. Inspect what is there

```bash
pks writing profile show
```

This prints the resolved global `profile.md` plus counts and paths for anglicisms, allowlist terms, the active channel, and reference samples. It is the fastest way to see what `lint`, `prompt`, and `score` are currently drawing from.

An unauthored profile exits 1 with a hint to run `pks writing init` followed by `pks writing profile author`.

## 3. Author the profile

```bash
pks writing profile author
```

The interactive menu offers two options.

### Option A — cowork bootstrap (recommended)

The first menu choice prints an authoring prompt designed to be pasted into a model session that has read a lot of your writing but has no filesystem access. The model replies with a JSON bundle describing your voice, anglicisms, calques, allowlist terms, reference samples, and lessons. You ingest that bundle in step 4.

For a non-interactive version of the same prompt, use the dedicated command:

```bash
pks writing profile prompt
```

That prints to plain stdout with no Spectre markup, so it pipes cleanly:

```bash
pks writing profile prompt | pbcopy
```

### Option B — edit by hand

The second menu choice opens `profile.md` directly in `$EDITOR`. When `$EDITOR` is unset the command searches PATH for `code`, `nano`, `vim`, and `vi`, and prints a message rather than an error if none are found.

> **Note.** `pks writing profile author` is an interactive Spectre.Console prompt and requires a terminal. Use `pks writing profile prompt` in scripts and agent contexts.

## 4. Ingest a cowork bundle

```bash
pks writing profile ingest ~/Downloads/cowork-reply.md
```

The bundle may be a raw `.json` file or markdown containing a fenced `json` block. The command writes into `~/.pks-cli/writing/`: `profile.md`, the anglicism and calque lists, allowlist terms, per-channel reference samples, and lessons. It reports added and skipped counts per category.

Paths support `~/` expansion. Without `--force`, an already-populated `profile.md` and existing reference samples are skipped rather than merged — only the untouched seeded template is silently overwritten.

```bash
pks writing profile ingest bundle.json --force
```

## 5. Add reference samples

The critic uses per-channel reference samples to learn your voice. Drop `*.md` files into `~/.pks-cli/writing/reference/{channel}/`. With no samples for a channel, `pks writing prompt` still works but the critic evaluates the post without a voice baseline.

## 6. Move the profile to another machine

Export the whole global store as a relocatable tarball:

```bash
pks writing profile export ~/pks-writing-profile.tgz
```

The archive excludes the platform-specific Vale binary cache directory so it stays portable. Entries are rooted under `writing/`.

On the target machine:

```bash
pks writing profile import ~/pks-writing-profile.tgz
```

Import extracts to a temporary staging directory first, then copies per file so skip semantics are honored. Existing files are skipped by default; pass `--force` to replace them:

```bash
pks writing profile import ~/pks-writing-profile.tgz --force
```

## 7. Verify

```bash
pks writing profile show
```

The counts for anglicisms, allowlist terms, and reference samples should match what you saw on the source machine.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `profile show` exits 1 with an init hint. | The global root does not exist. Run `pks writing init`. |
| Ingest reports everything skipped. | `profile.md` and the samples already exist. Re-run with `--force`. |
| Ingest fails with a parse error. | The bundle is not valid JSON and has no fenced `json` block. Re-save the model reply. |
| `profile export` fails. | The global root does not exist yet. Run `pks writing init` first. |
| `profile import` fails on the archive. | The tarball has no top-level `writing/` folder, so it was not produced by `profile export`. |
| Imported files did not replace local ones. | Import skips existing files by default. Re-run with `--force`. |
| `lint` reports an empty anglicism list. | The profile carries no anglicisms yet. Author or ingest one, or build the list with the learning loop. |

## Next steps

- [Quickstart: lint and score your first post](/tools/pks/writing/quickstart) — the full first pass, from install to a scored post.
- [Teaching the profile: learn, corpus, apply](/tools/pks/writing/learning) — grow the profile automatically from review findings.
- [Linting and rubric scoring](/tools/pks/writing/scoring) — how the profile feeds the linter and the critic.
- [pks writing CLI reference](/tools/pks/writing/reference) — every flag and file path.
