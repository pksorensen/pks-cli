---
title: "Quickstart: install pks and run your first commands"
description: "Install the pks CLI, verify it runs, then get three real results locally — Claude Code cost analysis, a searchable session brain, and a writing lint pass."
tags: [quickstart, cli, installation]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "dotnet tool install -g pks-cli && pks claude usage"
---

Get pks installed and producing real output in under ten minutes: install the binary, confirm the version, then run three self-contained commands that need no cloud account and no API key. Each scenario below reads data already on your machine and writes its results locally.

## 1. Prerequisites

Pick one install route. The routes differ only in what they require up front — the `pks` command is identical afterwards.

- **.NET 10 SDK** — needed for the .NET global-tool route. pks targets `net10.0` and is packaged with `PackAsTool`.
- **Node.js 18 or newer** — needed for the npm route, which ships self-contained binaries and needs no .NET at all.
- **A Claude Code history** — steps 4 and 5 read `~/.claude/projects/**/*.jsonl`. Without at least one past Claude Code session there is nothing to analyse.
- **git** — step 5 writes per-project artifacts into a repository's `.pks/` folder.

### Option A — .NET global tool (recommended)

```bash
dotnet tool install -g pks-cli
```

### Option B — npm

```bash
npm install -g @pks-cli/cli
```

The npm package resolves a platform-specific binary through `optionalDependencies`, so the same command works on Linux, macOS, and Windows (x64 and arm64).

## 2. Verify the install

```bash
pks --version
```

You should see the installed version printed, for example `6.20.1`. If the shell reports that `pks` is not found, open a new terminal so the tool directory is picked up by `PATH`.

pks prints an ASCII banner before most commands. Suppress it with `--no-logo`, and add `--debug` to any command for verbose output.

```bash
pks --no-logo --version
```

## 3. See what Claude Code is costing you

This is the fastest genuine result pks gives you, and it never leaves your machine. It parses your Claude Code session transcripts, deduplicates billed requests, prices them, and charts the outcome.

```bash
pks claude usage
```

You get an hourly cost chart for the last 24 hours, a daily cost chart, and a cost summary with the five most expensive models. Parsed files are cached in `~/.pks-cli/usage-cache/manifest.json`, so a second run is much faster.

For the performance view instead of the cost view:

```bash
pks claude stats
```

That renders an activity heatmap, session streaks, total token counts, and a response-time-per-output-token chart split into a recent window versus the prior period — the honest way to check whether Claude Code has slowed down.

> **Note.** Both commands are read-only and offline. Neither sends your transcripts anywhere.

## 4. Build a searchable brain from your session history

The brain turns the same transcripts into a queryable knowledge base. Ingest is deterministic and free — no model is called, so nothing is billed.

```bash
cd /path/to/your/repo
pks brain init
pks brain ingest
```

`init` creates the global root at `~/.pks-cli/brain/` and, inside a git repository, a `.pks/brain/` folder that it also appends to the nearest `.gitignore`. `ingest` walks every Claude session file and writes four append-only firehose files: prompts, tool calls, file operations, and errors. Repeat runs only reprocess sessions whose file changed.

Now search everything you have ever asked:

```bash
pks brain search "keycloak"
pks brain status
```

`search` prints a table of source, timestamp, session, and matching snippet. `status` reports what the brain knows: projects, sessions, prompts, tool calls, file operations, and the last ingest time.

The later phases — `pks brain extract`, `synth`, `wiki`, `adr` — call an LLM and cost money. Each one plans first and shows an estimate before spending anything.

## 5. Lint a markdown file

The writing toolchain runs a deterministic, no-LLM terminology pass. Initialize it once, then lint anything.

```bash
pks writing init
pks writing lint README.md
```

`init` seeds `~/.pks-cli/writing/` with a profile template. Skipping it makes `lint` fail with an empty-anglicism-list error. `lint` writes a `WRITING-REPORT.json` and `.md` sidecar next to each file with findings, deletes stale sidecars for files that are now clean, and always exits 0 — it never breaks a build.

Check what the linter is drawing from:

```bash
pks writing profile show
```

That prints the resolved profile plus counts and paths for anglicisms, allowlist terms, the active channel, and reference samples.

## 6. Where your data lives

Two directories matter, and they are different things.

| Setting | Value |
|---|---|
| Global config and state | `~/.pks-cli/` |
| Per-project state | `<repo>/.pks/` |

Global settings live in `~/.pks-cli/settings.json`. Credentials for each integration are stored per feature in the same directory. On Windows the root is `%USERPROFILE%\.pks-cli`, not `%APPDATA%`.

## 7. Next steps

- [pks](/tools/pks) — the full command surface and where each family fits
- [pks brain](/tools/pks/brain) — the five-stage pipeline from raw sessions to wiki pages and ADRs
- [pks writing](/tools/pks/writing) — the scoring loop, naturalness review, and the portable writer profile
- [pks devcontainer](/tools/pks/devcontainer) — spawn a devcontainer locally or on a remote SSH target
- [pks github](/tools/pks/github) — authenticate with GitHub and run the self-hosted Actions runner
- [pks agent](/tools/pks/agent) — the one-shot coding-agent loop and Agent Share registration
