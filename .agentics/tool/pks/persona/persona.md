---
title: "pks persona"
description: "Define reader personas, validate the persona library, and score markdown content against rubric-driven metrics with pks persona — the full command reference."
tags: [reference, cli, content-review, rubric-scoring]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks persona <command> [options]"
examples:
  - command: "pks persona lint"
    description: "Validate every persona file under personas/"
  - command: "pks persona list --locale da"
    description: "Enumerate personas defined for a locale"
  - command: "pks persona show solo-indie-builder"
    description: "Print one persona's raw markdown"
  - command: "pks persona prompt post.md --persona solo-indie-builder --rubric relevance"
    description: "Emit a scoring prompt bundle for your own LLM"
  - command: "pks persona accept post.md --persona solo-indie-builder --rubric relevance --from reply.json"
    description: "Validate a reply and persist the score"
  - command: "pks persona score post.md --persona solo-indie-builder --rubric relevance"
    description: "Score in-process with a built-in LLM call"
---

`pks persona` defines reusable reader-archetype personas and scores markdown content against them on rubric-driven 1–5 metrics. It exists so a blog post, landing page, or sales pitch can be checked against a specific reader's concerns before publishing, instead of relying on a single generic pass.

## Overview

The group has two parallel scoring modes that share one on-disk model. `lint`, `list`, and `show` validate and browse the persona library itself — the markdown files under `personas/<locale>/<slug>/<slug>.md` and the rubric files under `personas/_rubrics/<rubric-id>.md`. `prompt` and `accept` are the bring-your-own-LLM pipe: `prompt` builds a scoring bundle, an external agent runs it through its own model, and `accept` validates and persists the reply. `score` and `score-all` skip the pipe and call a model directly inside the CLI.

- **No network calls for `lint`, `list`, `show`, `prompt`, or `accept`.** They are pure filesystem operations.
- **`score` and `score-all` need live model credentials.** They resolve a `--model` id through the CLI's own provider factory — an Anthropic API key or a logged-in Azure Foundry session.
- **Every scoring path writes the same sidecar shape.** Results land in `_review/<locale>.PERSONA-SCORES.json` next to the scored file, regardless of which mode produced them.

## What you get

- **A validated persona and rubric library.** `pks persona lint` checks frontmatter, required sections, and bullet structure before a malformed persona file can silently break scoring.
- **A bring-your-own-model pipe.** `pks persona prompt` emits the system prompt, user prompt, JSON schema, and metadata as one bundle; `pks persona accept` validates whatever reply comes back and persists it, whether that reply came from Claude Code, another agent, or a human pasting into a chat window.
- **A one-shot in-process scorer.** `pks persona score` builds the prompt, calls a model, validates, and persists in a single command when the CLI's own model access is enough.
- **Batch scoring across the full matrix.** `pks persona score-all` scores one content file against every persona × rubric pair in a locale, with an optional cheap-model pre-screen to skip obvious mismatches before the expensive call.
- **Cost and token accounting for every LLM call.** Both `score` and `score-all` log tokens, estimated cost, and duration to a session sidecar, independent of whether the score itself validated.

## How it fits together

Personas live under `personas/<locale>/<slug>/<slug>.md`; rubrics live under `personas/_rubrics/<rubric-id>.md`. Every content-facing command resolves that `personas/` root by walking up from the content file's directory (falling back to the current directory), so any command works from inside a repo without an explicit path. Author or audit the library with `lint`, `list`, and `show` first — scoring commands assume the persona and rubric already exist and exit with an error if they don't.

For a single check, either pipe through your own model (`prompt` → your LLM → `accept`) or let the CLI call one directly (`score`). For a full sweep across a locale, `score-all` iterates the whole persona × rubric matrix in one run, optionally screening each pair with a cheap model first so the expensive deep pass only runs on candidates that clear a score of 3. All three scoring paths write to the same `_review/<locale>.PERSONA-SCORES.json` sidecar (upsert semantics — safe to re-run), and `score`/`score-all` additionally log every LLM call to `_review/<locale>.PERSONA-SESSION.json`.

- **Pipe mode (`prompt`/`accept`)** hands the model call to whatever LLM the calling agent already has — no credentials configured on the CLI itself.
- **In-process mode (`score`/`score-all`)** needs `pks foundry login` or an Anthropic API key, but collapses the loop into one command.

## Commands

`lint` · `list` · `show` · `prompt` · `accept` · `score` · `score-all`

All seven are direct leaves on the `persona` branch — there is no further nesting.

## Prerequisites

- A `personas/` directory somewhere above the content file or current directory, containing at least one `<locale>/<slug>/<slug>.md` persona file and a `_rubrics/<rubric-id>.md` rubric file for the rubric being scored against.
- For `score` and `score-all` only: live model credentials for whichever `--model` id is used — an Anthropic API key (in settings or `ANTHROPIC_API_KEY`) or a logged-in Azure Foundry session (`pks foundry login`) for Foundry-served models.

## Synopsis

```text
pks persona <command> [options]
```

```text
lint [path]         Validate persona markdown files against the expected shape
list                Enumerate personas defined under personas/<locale>/
show <persona-id>   Print one persona's raw markdown source
prompt <content>    Build a scoring prompt bundle without calling any LLM
accept <content>    Validate an LLM reply and persist it into the sidecar
score <content>     Build, call a model in-process, validate, and persist
score-all <content> Score one file across the full persona × rubric matrix
```

## lint

Validates persona markdown files against the expected shape: YAML frontmatter, required sections, bullet structure, and referenced card assets. Run it before committing a new or edited persona file, or in CI, to catch a malformed persona before it is used for scoring. A folder argument recurses over `personas/<locale>/<slug>/<slug>.md`; slug directories starting with `_` (for example `_rubrics`, `_review`) are skipped when auto-enumerating.

| Argument | Required | Description |
|---|---|---|
| `path` | no | File or folder to lint. Defaults to `personas/` in the current tree, walked up from the current directory. |

| Flag | Description |
|---|---|
| `--locale <text>` | Lint a single locale when no path is given. Default: all locales under `personas/`. |
| `--json` | Emit machine-readable JSON instead of the human table. |

```bash
pks persona lint
```

```bash
pks persona lint personas/da/senior-ic-udvikler-brownfield/senior-ic-udvikler-brownfield.md
```

Exit code is `1` if any file has errors — warnings alone don't fail the exit code. With no path and no `personas/` directory found anywhere up the tree, it prints `! No persona files found.` and exits `0`. `--json` suppresses the pks startup banner, so it is safe to pipe into `jq` or CI.

## list

Enumerates the personas defined under `personas/<locale>/`, showing id, name, segment, and bucket (subfolder grouping). Use it to discover which persona ids exist before scoring content against them.

| Flag | Default | Description |
|---|---|---|
| `--locale <text>` | `da` | Locale to enumerate. |
| `--json` | — | Emit machine-readable JSON instead of the human table. |

```bash
pks persona list --locale da
```

Fails with exit code `1` and `error: no personas/ directory found walking up from cwd.` if no `personas/` folder exists anywhere above the current directory — run it from inside (or below) a repo that has one. `list --json` also suppresses the startup banner.

## show

Prints one persona's raw markdown source verbatim to stdout. It round-trips the file exactly rather than reformatting it, so it is meant to be piped to another tool or scanned quickly.

| Argument | Required | Description |
|---|---|---|
| `persona-id` | yes | Persona id (slug) to render. |

| Flag | Default | Description |
|---|---|---|
| `--locale <text>` | `da` | Locale to look up the persona under. |

```bash
pks persona show solo-indie-builder
```

Exits `1` with `error: persona '<id>' not found under personas/<locale>/.` if the id/locale pair doesn't resolve — check `pks persona list --locale <locale>` first. `show` always suppresses the startup banner.

## prompt

Builds and emits the persona × rubric × content scoring prompt bundle — system prompt, user prompt, JSON schema, and metadata — without calling any LLM. This is the bring-your-own-model path: an agent or human reads the bundle, runs it through whatever LLM it has access to, then submits the reply with `pks persona accept`. It complements `score`/`score-all`, which do the LLM call in-process instead.

| Argument | Required | Description |
|---|---|---|
| `content` | yes | Content file to score (markdown). |

| Flag | Default | Description |
|---|---|---|
| `--persona <text>` | — | Persona id to score against. Functionally required — omitting it exits `2`. |
| `--rubric <text>` | — | Rubric id (for example `relevance`, `resonance`, `quality`). Functionally required — omitting it exits `2`. |
| `--locale <text>` | `da` | Persona locale. |
| `--model <text>` | `claude-opus-4-7` | Model id hint embedded in the bundle's metadata. |
| `--format <text>` | `json` | Output format: `json` (`{ system, user, schema, meta }`) or `markdown`. |

```bash
pks persona prompt blog-posts/x/da.md --persona solo-indie-builder --rubric relevance
```

Resolves the `personas/` root by walking up from the content file's directory first, falling back to the current directory. The rubric must exist under `personas/_rubrics/<rubric>.md` or the command exits `2`. `--format markdown` prints a human-readable `# SYSTEM PROMPT` / `# USER PROMPT` dump instead of the JSON bundle — the schema and metadata fields are only in the JSON form. Always suppresses the startup banner.

## accept

Validates an externally produced LLM reply — from `pks persona prompt`, run through some agent's own model — against the rubric's JSON schema, and on success persists it into the persona-scores sidecar next to the content file. This closes the loop that `prompt` opens.

| Argument | Required | Description |
|---|---|---|
| `content` | yes | The content file the reply is about. Anchors the sidecar. |

| Flag | Default | Description |
|---|---|---|
| `--persona <text>` | — | Persona id the reply is scored against. Functionally required — omitting it exits `2`. |
| `--rubric <text>` | — | Rubric id the reply is scored against. Functionally required — omitting it exits `2`. |
| `--locale <text>` | `da` | Locale. |
| `--from <path>` | — | Path to the LLM reply (JSON, or markdown containing a `json` fence). Reads stdin when omitted. |
| `--model <text>` | `unknown` | Model id that produced the reply. Recorded in the sidecar. |
| `--per-model` | — | Scope the sidecar to the producing model: `_review/<locale>.PERSONA-SCORES.<model>.json`, so replies from different `--model` values coexist instead of overwriting each other. |

```bash
pks persona accept blog-posts/x/da.md --persona solo-indie-builder --rubric relevance --from reply.json
```

> **Note.** Without `--from`, `accept` blocks reading stdin until EOF — don't run it interactively without piping input.

On schema validation failure it prints `RESULT: {ok:false, errors:[...], hint:...}` to stdout and exits `1`. The required reply shape is `score` (1–5), `rationale`, `evidence[]`, plus any subscores the rubric declares — designed so an agent can read the hint and resubmit a corrected reply. On success it writes or upserts `_review/<locale>.PERSONA-SCORES.json` (or the `.<model>.json` variant with `--per-model`) and prints `RESULT: {ok:true, sidecarPath, score, ...}`. Always suppresses the startup banner.

## score

Builds the prompt, calls an LLM in-process, validates the reply, and persists the score — the single-call equivalent of `prompt` + `accept`. Use it when the CLI's own model access (a Foundry-served Claude model, or a direct Anthropic API key) is enough and there's no need to route through an external agent's own model.

| Argument | Required | Description |
|---|---|---|
| `content` | yes | Content file to score (markdown). |

| Flag | Default | Description |
|---|---|---|
| `--persona <text>` | — | Persona id to score against. Functionally required — omitting it exits `2`. |
| `--rubric <text>` | — | Rubric id. Functionally required — omitting it exits `2`. |
| `--locale <text>` | `da` | Persona locale. |
| `--model <text>` | `claude-opus-4-7` | Model id to call. |
| `--max-output-tokens <int>` | `1500` | Cap on output tokens. |
| `--per-model` | — | Scope the sidecar to the scoring model, so scores from different `--model` values coexist instead of overwriting each other. |

```bash
pks persona score blog-posts/x/da.md --persona solo-indie-builder --rubric relevance
```

It resolves the model in-process through the CLI's provider factory to either an Anthropic provider or an Azure Foundry provider and streams the completion directly — despite the built-in description text, it does not shell out to a separate `pks agent` subprocess. An Anthropic-family model id needs an API key in settings, `ANTHROPIC_API_KEY`, or a Foundry-served Claude endpoint with a logged-in Foundry session (`pks foundry login`); an Azure Foundry model needs an endpoint plus `AZURE_OPENAI_API_KEY` or a Foundry OAuth token. A dead or expired token surfaces as an exception here rather than a clean CLI error — `score-all`'s preflight probe is the command that catches and explains auth failures specifically.

Every call is appended to a session sidecar (`_review/<locale>.PERSONA-SESSION.json`, or the per-model variant) recording input/output tokens, estimated cost, and duration, independent of whether the score itself validated. On reply validation failure it prints `RESULT: {ok:false, errors, rawReplyHead}` (the first 500 characters of the raw reply) and exits `1` without writing to the scores sidecar. On success it writes or upserts the scores sidecar and prints `RESULT: {ok:true, score, sidecarPath, session:{...}}`.

> **Note.** `score` is not in the banner-suppression allowlist that covers `prompt`/`accept`/`show` — the startup banner prints before the `RESULT:` line.

## score-all

Bulk-scores one content file across the full persona × rubric matrix, or a filtered subset, with an optional cheap-model pre-screen pass to skip obviously mismatched pairs before spending the expensive deep-model call on them. This is the batch or CI workhorse for building out a content item's full `PERSONA-SCORES` sidecar in one run.

| Argument | Required | Description |
|---|---|---|
| `content` | yes | Content file to score. |

| Flag | Default | Description |
|---|---|---|
| `--locale <text>` | `da` | Persona locale to iterate. |
| `--rubric <text>` | — | Limit to a single rubric id. Default: iterate every rubric in `personas/_rubrics/`. |
| `--persona <text>` | — | Limit to a single persona id. Default: iterate every persona in the locale. |
| `--model <text>` | `claude-opus-4-7` | Model id for the deep pass. |
| `--screen-with <text>` | — | Cheap pre-pass model (for example `claude-haiku-4-5`). When set, each persona/rubric pair is screened first; only candidates scoring ≥ 3 get the deep `--model` pass. |
| `--max-output-tokens <int>` | `1500` | Cap on output tokens per call. |
| `--only-missing` | — | Skip persona/rubric cells already present in the sidecar. Useful for resuming after an aborted batch. |
| `--per-model` | — | Scope the sidecar to the scoring model, letting scores from different `--model` values coexist (per-post model A/B). |

```bash
pks persona score-all blog-posts/x/da.md --locale da
```

```bash
pks persona score-all blog-posts/x/da.md --rubric relevance --screen-with claude-haiku-4-5
```

> **Availability.** `score-all` runs a preflight probe against the first persona/rubric pair specifically to validate auth before burning a whole batch. If that probe throws an auth-family error, it prints an auth-failure summary, points at `pks foundry login`, and exits `2` immediately without touching the rest of the matrix. The same short-circuit fires mid-batch too, so a token that expires partway through a long run stops cleanly with partial results saved rather than burning through every remaining cell.

Cost and time scale as personas × rubrics calls, plus one extra call per cell when `--screen-with` is set and the cell isn't skipped — a full locale can mean dozens of paid LLM calls. `--only-missing`, combined with re-running after an auth failure, is the intended resume pattern: sidecar writes are upserts, not overwrites. With `--screen-with`, any screened score below 3 is persisted as the final score (tagged `(screen-only)`) and the expensive `--model` pass is skipped for that cell; a screen result ≥ 3 falls through to the full deep-model call.

If zero personas or zero rubrics match the `--persona`/`--rubric` filters, it prints a warning and exits `0` without writing anything. On completion it prints a per-cell `RESULT:` summary — counts of ok/fail/skipped/total, per-cell results, and session totals for calls, tokens, and cost — and exits `1` if any cell failed schema validation, even when some cells succeeded and were saved. Every call, screen or deep, is logged to the session sidecar regardless of outcome. `score-all` is not in the banner-suppression allowlist — the startup banner still prints.

## Troubleshooting

**`error: no personas/ directory found walking up from cwd.`** — run the command from inside (or below) a repository that has a `personas/` directory, or pass an explicit `path` to `lint`.

**`error: persona '<id>' not found under personas/<locale>/.`** — the id or locale doesn't resolve. Run `pks persona list --locale <locale>` to see valid ids.

**`error: --persona <id> required.` / `error: --rubric <name> required.`** — `prompt`, `accept`, `score`, and `score-all` all treat `--persona` and `--rubric` as functionally required even though they're declared as options; supply both.

**An auth-family exception from `score`, not a clean error message** — `score` does not catch and explain auth failures the way `score-all`'s preflight probe does. Run `pks foundry login` first, or set `ANTHROPIC_API_KEY`, then retry.

**`RESULT: {ok:false, ...}` after `accept` or `score`** — the reply failed schema validation. The required shape is `score` (1–5), `rationale`, `evidence[]`, plus any subscores the rubric declares; read the `hint` field and resubmit a corrected reply.

## Next steps

- [pks writing](/tools/pks/writing) — the equivalent Danish-first linting and rubric-critique loop for terminology and naturalness
- [pks foundry](/tools/pks/foundry) — sign in to Azure AI Foundry so `score` and `score-all` can resolve Foundry-served models
- [pks](/tools/pks) — the full command surface `persona` is one branch of
