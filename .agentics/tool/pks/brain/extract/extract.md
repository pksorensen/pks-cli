---
title: "pks brain extract"
description: "Summarize each ingested Claude session into a per-session markdown extract with a cost sidecar, planning and gating the spend before any model runs."
tags: [how-to, brain, extract, ai]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain extract [options]"
examples:
  - command: "pks brain extract --limit 1 --dry-run"
    description: "Plan a single session without calling a model"
  - command: "pks brain extract --since 7d --model haiku"
    description: "Extract the last week with the cheapest model"
  - command: "pks brain extract --force -y"
    description: "Re-extract everything without the cost prompt"
---

Extract is phase 2 of the pipeline and the first one that costs money. It reads the sessions ingested into the global firehose and writes one markdown summary per session into `./.pks/brain/extracts/<session-id>.md` — what was worked on, what struggled, the prompt techniques used, the user story, and tags — alongside a JSON sidecar recording the model, tokens, cost, and the hash of the prompt that produced it.

The system prompt is the editable `brain-extract` skill. Change it with [pks brain skill](/tools/pks/brain/skill) and every later extract picks up your version.

## 1. Prerequisites

- **A git repository.** Extracts always write to `./.pks/brain/extracts/`, so the command exits with code 1 outside a repo.
- **[pks brain ingest](/tools/pks/brain/ingest)** must have populated the global firehose. Without it there are no eligible sessions.
- **Claude billing or an API key** already configured on the machine, or an Azure AI Foundry login if you plan to use `--foundry`.

## 2. Plan before you spend

```bash
pks brain extract --limit 1 --dry-run
```

Extract always plans first: it enumerates eligible sessions and prints a cost and time estimate before invoking anything. `--dry-run` stops there. Start here on a new repository to see how many sessions are in scope.

Estimates are derived from previous `.meta.json` sidecars matching the same model. With no history yet, fallback heuristics are used — roughly 45 seconds per call for haiku, 90 for sonnet, 180 for opus — so the first estimate on a fresh machine is rough.

## 3. Extract a slice

```bash
pks brain extract --since 7d
```

Sessions are summarized in parallel, ten at a time by default, with a progress bar and a closing summary table of token and cost totals.

A confirmation prompt appears only when both conditions hold: at least 25 eligible sessions and at least $1.00 estimated. Smaller runs proceed without asking. Pass `-y` to skip the gate entirely.

## 4. Control cost

```bash
pks brain extract --model haiku --max-budget-usd 0.50 --parallel 4
```

`--model` is passed straight through to the backend and defaults to `haiku`, which is the cheapest model and adequate for this batch summarization. `--max-budget-usd` is a hard cap per invocation. `--parallel` bounds concurrency.

## 5. Choose a backend

```bash
pks brain extract --agent pks            # built-in in-process summarizer (default)
pks brain extract --agent claude         # shell out to the claude CLI
pks brain extract --agent claude --foundry
```

`--agent` takes exactly `pks` or `claude`; any other value exits with code 1. `--foundry` routes tokens through Azure AI Foundry using your Azure quota rather than the agent's default Anthropic billing, and it only takes effect together with `--agent claude`.

> **Note.** If `--foundry` is requested and the machine is not logged into Foundry, the run aborts without extracting rather than falling back to default billing.

## 6. Re-extract after changing the prompt

```bash
pks brain extract --force
```

By default a session is skipped when its extract is newer than the source. `--force` re-extracts regardless. This is what to run after editing the `brain-extract` skill — `pks brain status` flags extracts made with a different prompt hash as candidates for refresh.

To point at a prompt file directly without touching the resolution hierarchy:

```bash
pks brain extract --skill-path ./prompts/brain-extract/SKILL.md
```

## 7. Verify

```bash
pks brain status
```

The per-project section reports extract count, total and average cost, token totals, and models used. Refresh suggestions call out sessions with no extract, extracts older than their source, and extracts made with a stale prompt version.

The exit code is 1 only when the run produced zero successful extracts but had failures. A partially failed run that produced at least one extract exits 0.

## Options

| Flag | Default | Description |
|---|---|---|
| `-p, --project <slug>` | current directory slug | Project to extract from. Defaults to the encoded slug of the current working directory. |
| `--skill-path <path>` | — | Override the `brain-extract` SKILL.md location, skipping the search hierarchy. |
| `--since <window>` | — | Only extract sessions newer than this. Accepts `7d`, `24h`, `30m`, or an ISO date. |
| `--limit <n>` | — | Cap the number of sessions extracted in this run. |
| `--force` | `false` | Re-extract even when the existing extract is newer than the source. |
| `--parallel <n>` | `10` | Maximum parallel model invocations. |
| `--model <name>` | `haiku` | Model name passed to the backend. |
| `--max-budget-usd <amount>` | — | Hard dollar cap per invocation. |
| `--agent <name>` | `pks` | Summarizer backend: `pks` for the built-in in-process agent, `claude` to shell out to the CLI. |
| `--foundry` | `false` | Use Azure AI Foundry as the token provider instead of the agent's default billing. |
| `--dry-run` | `false` | Show which sessions would be extracted without calling a model. |
| `-y, --yes` | `false` | Skip the cost-confirmation prompt for large runs. |

## Troubleshooting

**Exit code 1 with no output written.** The working directory is not inside a git repository. Extracts require a project root.

**`--foundry` appeared to do nothing.** It only applies with `--agent claude`. With the default `pks` agent the flag has no effect.

**Nothing is eligible.** Either ingest has not run, or every session already has a newer extract. Run `pks brain status`, then `--force` if the extracts are stale relative to a changed prompt.

**The first cost estimate looks wrong.** With no prior sidecars for the chosen model, estimates fall back to per-call heuristics. Run a small `--limit` batch first to seed real numbers.

## See also

- [pks brain ingest](/tools/pks/brain/ingest) — phase 1, the input extract reads from
- [pks brain synth](/tools/pks/brain/synth) — phase 3, which clusters the extracts you just produced
- [pks brain skill](/tools/pks/brain/skill) — edit the prompt that shapes every extract
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
