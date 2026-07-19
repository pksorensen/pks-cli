---
title: "pks writing CLI reference"
description: "Complete command, flag, argument, exit-code, and file-path reference for the pks writing group — lint, score, learn, profile, and naturalness."
tags: [reference, writing, cli]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks writing <command> [options]"
examples:
  - command: "pks writing init"
    description: "Create the global and per-project writing layout"
  - command: "pks writing lint blog-posts/"
    description: "Deterministic anglicism pass over a folder"
  - command: "pks writing prompt post.md"
    description: "Emit a rubric-critique bundle for your own LLM"
  - command: "pks writing accept post.md --from reply.json"
    description: "Validate the reply and write the report sidecar"
  - command: "pks writing learn post.md"
    description: "Propose profile edits from the last report"
  - command: "pks writing apply post.LEARN.json"
    description: "Commit accepted proposal actions to the profile"
---

`pks writing` is the writing group of the pks CLI: a deterministic anglicism linter, an LLM-agnostic rubric critique loop, a sentence-level naturalness rewrite loop, a learning pipeline that promotes findings into a writer profile, and export and import for that profile.

The group is registered in `src/Program.cs` with the description "Lint and score your writing (Danish-first); maintain a portable writer profile". It requires no authentication and makes no network calls; all state is local. Only the deprecated `score` command invokes an external process.

## Synopsis

```text
pks writing <command> [options]
```

```text
init                            Create the global and per-project writing layout
lint <path>                     Deterministic anglicism pass over a file or folder
score <path>                    Lint plus a local claude critique                 [deprecated]
prompt <file>                   Emit a rubric-critique bundle for an agent's LLM
accept <file>                   Validate a critique reply and write the report
learn <path>                    Propose profile edits from existing reports
corpus <folder>                 Aggregate per-post proposals into one corpus proposal
apply <proposal>                Commit accepted proposal actions to the profile
skill install                   Install the bundled agent skill
profile show                    Print the resolved profile and its counts
profile author                  Interactive profile authoring menu
profile prompt                  Print the cowork authoring prompt to stdout
profile ingest <bundle>         Ingest a cowork JSON bundle into the profile
profile export <output>         Tarball the global profile for another machine
profile import <archive>        Restore a profile tarball on this machine
naturalness prompt <file>       Emit a sentence-candidates bundle
naturalness accept <file>       Validate a candidates reply and merge sidecars
naturalness review <file>       Pick rewrites interactively
naturalness apply <file>        Apply picks in place, with a versioned archive
naturalness merge <file>        Re-merge per-critic candidate sidecars
naturalness patterns show       Table of learned rewrite patterns
naturalness patterns export     Markdown dump of the patterns store
```

### Global environment variables

| Variable | Default | Purpose |
|---|---|---|
| `EDITOR` | (unset) | Editor opened by `writing profile author`. When unset, the command searches PATH for `code`, `nano`, `vim`, and `vi`. |

`pks writing prompt`, `pks writing accept`, `pks writing naturalness prompt`, `pks writing naturalness accept`, and `pks writing naturalness patterns` suppress the pks startup banner, because agents consume their stdout as JSON and a banner would corrupt the pipe.

### State and sidecar paths

| Path | Contents |
|---|---|
| `~/.pks-cli/writing/` | Global writer profile, anglicisms, allowlist, lessons, and the naturalness patterns store. |
| `~/.pks-cli/writing/reference/{channel}/` | Per-channel reference samples injected into critique prompts. |
| `./.pks/writing/` | Per-project overrides and report cache, added to the nearest `.gitignore` by `init`. |
| `<postdir>/_review/<stem>.WRITING-REPORT.{json,md}` | Lint findings merged with the rubric critique. |
| `<postdir>/_review/<stem>.LEARN.{json,md}` | Profile-edit proposal with per-action `accept` flags. |
| `<folder>/_corpus.LEARN.{json,md}` | Corpus-level proposal aggregated across posts. |
| `<postdir>/_review/<stem>.NATURALNESS-CANDIDATES.<critic>.json` | One critic's validated candidate sentences. |
| `<postdir>/_review/<stem>.NATURALNESS-CANDIDATES.json` | Canonical merge of every critic's candidates. |
| `<postdir>/_review/<stem>.NATURALNESS-PICKS.json` | Your incremental review choices. |
| `<postdir>/<locale>.v<N>.md` | Pre-apply archive written by `naturalness apply`. |
| `~/.claude/skills/pks-writing-score/` | Default install target for `skill install`. |

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. `lint` exits 0 regardless of finding count. |
| `1` | Operation failed, or a reply failed schema validation. Validation failures print a final `RESULT: {...}` line carrying `errors` and `hint`. `lint` also exits 1 if the profile's anglicism list is empty (run `pks writing init` first). |
| `2` | A required file argument is missing. Used by `accept` and `naturalness accept`. |

## init

Creates the global layout at `~/.pks-cli/writing/`, seeding a `profile.md` template on first run. When the working directory is inside a git repository it also creates `./.pks/writing/` and adds it to the nearest `.gitignore`. Outside a repository the project layer is skipped with a warning, not an error. Re-running never overwrites an existing profile.

| Flag | Description |
|---|---|
| `--dry-run` | Show what would be created without touching the filesystem. |

## lint

Scans a markdown file, or every `*.md` under a folder, against the profile's anglicism list and allowlist, skipping `node_modules/`, `_review/`, and `.pks/`. Writes a report sidecar per file with findings and deletes stale sidecars for files that now have none. At most 20 findings render per file in the terminal; the full set is in the sidecar. Fails with exit 1 when the anglicism list is empty, which means `init` was skipped.

| Argument | Required | Description |
|---|---|---|
| `path` | yes | File or folder to lint. Folders recurse over `*.md`. |

| Flag | Description |
|---|---|
| `--quiet` | Suppress the per-finding table and print only the summary. |

## score

Deprecated. Runs lint plus a full rubric critique in one shot by spawning a local `claude` process with the profile, channel rubric, and reference samples injected, producing a score across five dimensions. Superseded by `prompt` and `accept`, which keep model invocation out of pks. Requires a local `claude` CLI unless `--lint-only` is passed, accepts a single file rather than a folder, and falls back to a lint-only report when the critic fails.

| Argument | Required | Description |
|---|---|---|
| `path` | yes | Markdown file to score. Folders are not supported. |

| Flag | Default | Description |
|---|---|---|
| `--model <name>` | `haiku` | Claude model: `haiku`, `sonnet`, or `opus`. |
| `--budget <usd>` | `0.50` | Maximum spend per critique in USD. |
| `--lint-only` | — | Skip the critic and lint deterministically only. |

## prompt

Emits a self-contained bundle for an agent to feed to its own LLM: a system prompt, a user prompt, the reply JSON schema, and metadata, with the post body, writer profile, channel rubric, and reference samples embedded. Output is plain stdout with no markup or banner. With no reference samples for the channel, the critic runs without a voice baseline.

| Argument | Required | Description |
|---|---|---|
| `file` | yes | Markdown file to score. |

| Flag | Default | Description |
|---|---|---|
| `--format <fmt>` | `json` | Output format: `json` or `markdown`. |
| `--max-references <n>` | `10` | Maximum reference samples injected into the prompt. |
| `--max-findings <n>` | `12` | Maximum findings the model may return. |

## accept

Validates an LLM reply against the score schema — five dimension scores from 1 to 5 plus notes, with finding line numbers checked against the source file's actual line count — then merges it with existing lint findings and writes the report sidecar. Only findings whose rule id starts with `Writing.` are preserved from the existing report; anything else is replaced.

| Argument | Required | Description |
|---|---|---|
| `file` | yes | The source markdown file the reply is about. |

| Flag | Description |
|---|---|
| `--from <path>` | Path to the reply, as raw JSON or markdown with a fenced `json` block. Reads stdin when omitted. |
| `--model <id>` | Model id that produced the reply, recorded in the report. |

## learn

Non-interactive. Reads each target's last report sidecar, groups and dedupes findings heuristically, and writes a `<stem>.LEARN.json` proposal with per-action `accept` flags plus a `<stem>.LEARN.md` summary under `_review/`. Files with no prior report are counted as skipped. Files with zero actions have stale proposals deleted rather than getting an empty one. Prints a `RESULT` summary line with file, skipped, and action counts.

| Argument | Required | Description |
|---|---|---|
| `path` | yes | Markdown file or folder. Folders recurse over `*.md`, skipping `_review/`, `.pks/`, and `node_modules/`. |

| Flag | Description |
|---|---|
| `--filter <glob>` | When `path` is a folder, only learn files matching this glob, such as `da.md`. |

## corpus

Aggregates every per-post `_review/<stem>.LEARN.json` under a folder into one corpus-level proposal at `<folder>/_corpus.LEARN.json` and `.md`, keeping only terms that recur across at least `--min-posts` posts. This filters one-off false positives before they reach the global profile. It reads `LEARN.json` sidecars, not report sidecars, so run `learn` first.

| Argument | Required | Description |
|---|---|---|
| `folder` | yes | Folder containing posts with `_review/<stem>.LEARN.json` sidecars. |

| Flag | Default | Description |
|---|---|---|
| `--min-posts <n>` | `2` | Minimum number of posts a term must appear in to be proposed. |
| `--channel <name>` | `blog` | Channel label written into the corpus proposal. |

## apply

Consumes a `.LEARN.json` proposal and applies every action flagged `accept: true` to the global profile store, adding allowlist terms, anglicism entries with Danish alternatives, or dimension-tagged lessons. The store dedupes, so the operation is idempotent. Actions flagged `accept: false` are counted as rejected and skipped. Malformed actions missing a term, lesson, or dimension are recorded as warnings and skipped rather than aborting. Prints a `RESULT` summary with per-kind counts.

This mutates `~/.pks-cli/writing/`, affecting every future lint and critique on this machine.

| Argument | Required | Description |
|---|---|---|
| `proposal` | yes | Path to a `.LEARN.json` produced by `learn` or `corpus`. |

| Flag | Description |
|---|---|
| `--dry-run` | Print what would be applied without touching the profile. |

## skill install

Writes the embedded `pks-writing-score` skill file into `~/.claude/skills/pks-writing-score/` so Claude Code and other skill-aware agents discover the prompt-and-accept workflow without being told about it. Exits 1 when the target file already exists and `--force` is absent, and exits 1 with an embedded-skill error when the resource was not compiled into the assembly.

| Flag | Description |
|---|---|
| `--force` | Overwrite an existing skill file. |
| `--target <dir>` | Install directory, overriding the default. |

## profile show

Prints the resolved global `profile.md` plus counts and paths for anglicisms, allowlist terms, the active channel, and reference samples. Exits 1 with a hint to run `init` and `profile author` when no profile exists.

## profile author

Interactive menu for building the profile: print the cowork authoring prompt for a filesystem-less model session, or open `profile.md` in `$EDITOR`. The editor path falls back to searching PATH for `code`, `nano`, `vim`, and `vi`, and prints a message rather than an error when none are found. Requires a terminal.

## profile prompt

Prints the cowork authoring prompt to plain stdout with no markup, the non-interactive counterpart to `profile author`'s first menu option.

## profile ingest

Ingests a cowork-produced bundle and writes it into `~/.pks-cli/writing/`: `profile.md`, anglicisms, calques, allowlist terms, per-channel reference samples, and lessons, reporting added and skipped counts per category. The path supports `~/` expansion. Fails with a parse error when the bundle is neither valid JSON nor markdown containing a fenced `json` block.

| Argument | Required | Description |
|---|---|---|
| `bundle` | yes | Bundle path. Raw JSON, or markdown containing a fenced `json` block. |

| Flag | Description |
|---|---|
| `--force` | Overwrite an existing `profile.md` and reference samples. By default these are skipped unless the profile is still the untouched template. |

## profile export

Tarballs the entire global profile into a relocatable `.tgz`, excluding the platform-specific Vale binary cache so the archive stays portable. Entries are rooted under `writing/`. Fails with a hint to run `init` when the global root does not exist.

| Argument | Required | Description |
|---|---|---|
| `output` | yes | Output `.tgz` path. |

## profile import

Restores an archive produced by `profile export` into `~/.pks-cli/writing/`. Extraction goes to a temporary staging directory first, then copies per file so skip semantics are honored. Fails when the archive has no top-level `writing/` folder.

| Argument | Required | Description |
|---|---|---|
| `archive` | yes | Input `.tgz` path produced by `profile export`. |

| Flag | Description |
|---|---|
| `--force` | Overwrite existing files. By default files that already exist are skipped. |

## naturalness prompt

Emits a bundle for the naturalness critic — a sentence-level review distinct from `writing prompt`'s dimension scoring. Carries the candidate sentences extracted from the post, the writer profile, and the global patterns store. Output is clean JSON on stdout.

| Argument | Required | Description |
|---|---|---|
| `file` | yes | Markdown file to extract naturalness candidates from. |

| Flag | Default | Description |
|---|---|---|
| `--format <fmt>` | `json` | Output format: `json` or `markdown`. |
| `--max-candidates <n>` | `15` | Maximum candidate sentences the critic may surface. |

## naturalness accept

Validates a critic reply against the candidates schema — a `post` field plus `candidates` entries each carrying an id, line, original sentence, issue, and exactly three labelled A, B, and C alternatives with an `authorlikeness` between 0 and 1 — then persists it as a per-critic sidecar and immediately re-merges every per-critic sidecar into the canonical candidates file that `review` reads. Running several named critics against the same post is supported; alternatives are capped per line by the merge.

| Argument | Required | Description |
|---|---|---|
| `file` | yes | The source markdown file the reply is about. |

| Flag | Default | Description |
|---|---|---|
| `--from <path>` | — | Path to the reply, as raw JSON or markdown with a fenced `json` block. Reads stdin when omitted. |
| `--model <id>` | — | Model id that produced the reply, recorded in the sidecar. |
| `--max-candidates <n>` | `15` | Maximum candidates accepted from the reply. |
| `--critic <name>` | `opus` | Critic name determining the per-critic sidecar filename. |

## naturalness review

Interactive loop over every candidate in the merged sidecar, showing the original sentence, the flagged issues, and a table of alternatives with rationale and authorlikeness. Labels read A, B, and C for one critic, or A-opus, B-opus, A-gpt5 and so on when several contributed. At each sentence you pick, skip, supply a free-form rewrite with the other choice, or quit. An empty custom answer degrades to a skip. Quitting saves partial progress and exits 0. Choices save incrementally after each one, and picks already applied are skipped on re-entry. Exits 1 when no candidates sidecar exists. Requires a terminal.

| Argument | Required | Description |
|---|---|---|
| `file` | yes | Markdown file whose candidates sidecar to review. |

## naturalness apply

Reads the picks, builds a unified diff of the sentence rewrites, and applies them in place to the source markdown after confirmation. Before overwriting, the pre-apply body is archived to `<locale>.v<N>.md` with version frontmatter injected and matching sidecars copied to the same suffix; when the frontmatter already carries a `version:` key the injector leaves it untouched. Archiving is best-effort and never blocks accepted picks from landing. After archiving, the canonical candidates and picks sidecars are deleted, so a further review needs a fresh prompt and accept pass. Every applied rewrite is appended to the global patterns store. Exits 0 as a no-op when all picks are already applied.

| Argument | Required | Description |
|---|---|---|
| `file` | yes | Markdown file whose picks to apply. |

| Flag | Description |
|---|---|
| `--yes` | Skip the confirmation prompt and apply the diff immediately. |
| `--dry-run` | Print the diff without touching the source file or the pattern store. |

## naturalness merge

Re-merges per-critic candidate sidecars into the canonical candidates file. `naturalness accept` already performs this merge, so this command is a repair tool for when a per-critic file was hand-edited or deleted. Exits 1 when no per-critic sidecars exist for the post.

| Argument | Required | Description |
|---|---|---|
| `file` | yes | Source markdown file whose per-critic sidecars to merge. |

| Flag | Default | Description |
|---|---|---|
| `--max-alternatives-per-line <n>` | `6` | Cap per-line alternatives in the merged file, dropping the lowest authorlikeness entries when more arrive. |

## naturalness patterns show

Renders the global naturalness learning store as a table of trigger summary, accepted example, and acceptance count, accumulated across every `naturalness apply` run on this machine. An empty store prints a hint rather than erroring.

## naturalness patterns export

Exports the raw markdown rendering of the patterns store, to stdout when no path is given or to a file otherwise. Writing to a path auto-creates missing parent directories. An empty store writes `(empty)` to stderr and exits 0.

| Argument | Required | Description |
|---|---|---|
| `outfile` | no | Destination markdown path. Prints to stdout when omitted. |

## See also

- [pks writing](/tools/pks/writing) — the group landing page and mental model.
- [Quickstart: lint and score your first post](/tools/pks/writing/quickstart) — the first end-to-end pass.
- [Linting and rubric scoring](/tools/pks/writing/scoring) — the lint, prompt, and accept cycle in context.
- [Teaching the profile: learn, corpus, apply](/tools/pks/writing/learning) — promoting findings into the profile.
- [Naturalness: sentence-level rewrites](/tools/pks/writing/naturalness) — the sentence-level loop end to end.
- [The writer profile](/tools/pks/writing/profile) — authoring and moving the shared state.
