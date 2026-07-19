---
title: "Run a one-shot coding agent"
description: "Give pks a prompt and let it work: pick a model, supply a credential, bound the sandbox and turn count, and read the exit code from a scripted run."
tags: [how-to, agents, llm, sandbox]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks agent \"<prompt>\" [options]"
examples:
  - command: "pks agent \"summarise this repo\""
    description: "First run with the default model and sandbox"
  - command: "pks agent \"fix the lint errors\" --model claude-sonnet-4-6 --read-only"
    description: "Inspect-only run against Anthropic Claude"
  - command: "pks agent \"apply the migration\" --cwd ./src --max-turns 20"
    description: "Narrow the sandbox and cap iterations"
---

Get a prompt executed by a real tool-use agent in a few minutes: supply a model credential, run the prompt, watch the tool calls, and read the exit code. The agent runs to completion and exits — there is no interactive session.

This page covers the coding-agent path only. For the unrelated enrollment command on the same branch, see [Register this session as a shareable agent](/tools/pks/agent/register).

## 1. Prerequisites

- **A pks installation.** Either the .NET global tool or the npm package; both expose the same `pks` binary.
- **A model credential.** Azure OpenAI models need an endpoint or a logged-in Foundry session; Anthropic models need an API key — a Foundry session alone does not supply one for the built-in ids. Step 2 covers both.
- **A working directory you are willing to let the agent write to.** Tools are rooted there unless you pass `--cwd`. If you are not sure, start with `--read-only`.

## 2. Supply a credential

The agent resolves a credential per model. Pick whichever route matches the model you intend to use.

### Option A — Azure AI Foundry (recommended for `gpt-5.5`)

Sign in once and the default `gpt-5.5` model resolves against the Foundry-selected resource.

```bash
pks foundry init
```

This does **not** cover the built-in Anthropic models. `claude-opus-4-7` and `claude-sonnet-4-6` are hardcoded to call the real `https://api.anthropic.com` endpoint, so a `pks foundry init` session alone leaves them without a credential — use Option B or C below. A Foundry-served Claude endpoint only resolves if you hand-configure a custom `agent.models.<id>` entry whose `endpoint` is a `*.services.ai.azure.com` host.

### Option B — environment variable

```bash
export ANTHROPIC_API_KEY=sk-ant-...
```

For Azure OpenAI models, `AZURE_OPENAI_API_KEY` plays the same role.

### Option C — settings file

Set `agent.models.<id>.apiKey` in `~/.pks-cli/settings.json`. Values there take precedence over the environment variables.

With any one of these in place, the agent has what it needs to start.

## 3. Run your first prompt

```bash
pks agent "summarise this repo"
```

The agent works in the current directory, calling `read`, `grep`, `find`, and `ls` as it explores, and prints its answer when it stops. The process exits `0` on a clean stop.

## 4. Constrain the run

Three flags do the bounding, and they compose.

```bash
pks agent "fix the lint errors" --model claude-sonnet-4-6 --read-only
```

`--read-only` removes `write`, `edit`, and `bash` from the tool registry, so the agent can look but not change anything. Use it whenever you want a diagnosis rather than a fix.

```bash
pks agent "apply the migration" --cwd ./src --max-turns 20
```

`--cwd` moves the sandbox root, so every tool call is confined to `./src`. `--max-turns` caps the tool-call iterations at 20 instead of the default 50.

## 5. Use a custom system prompt

Put a markdown file at `~/.pks-cli/agent-skills/db-migrations.md` and load it by name.

```bash
pks agent "apply the migration" --skill db-migrations
```

The file's contents replace the default system prompt body. If the file does not exist, the command prints a red error and exits `1`.

## 6. Verify

```bash
pks agent "list the files in this directory" --read-only
```

The agent calls the `ls` tool and prints the listing. Check the exit code:

```bash
echo $?
```

`0` means the loop stopped cleanly. See the exit-code table below before wiring this into a pipeline.

## 7. Next steps

- [pks agent CLI reference](/tools/pks/agent/reference) — the complete flag, argument, and exit-code surface
- [Register this session as a shareable agent](/tools/pks/agent/register) — the other command on this branch
- [Agent](/tools/pks/agent) — how the coding agent and registration fit together

## Options

| Flag | Default | Description |
|---|---|---|
| `-m <id>`, `--model <id>` | `gpt-5.5` | Model id for the coding agent. Recognized ids are `gpt-5.5`, `claude-opus-4-7`, and `claude-sonnet-4-6`, plus any id configured under `agent.models.<id>` in settings. |
| `--cwd <dir>` | current directory | Sandbox root. Every `read`, `write`, `edit`, `bash`, `grep`, `find`, and `ls` call is rooted here. |
| `--skill <name>` | — | Loads `~/.pks-cli/agent-skills/<name>.md` as the system prompt, replacing the default body. |
| `--max-turns <n>` | `50` | Maximum tool-call iterations. A value of `0` or below falls back to `50`. |
| `--read-only` | `false` | Disables the mutating tools `write`, `edit`, and `bash`. Inspection tools stay enabled. |

The positional `[prompt]` argument is optional. When it is present, every legacy action option on this command is ignored.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | The agent loop stopped cleanly. |
| `1` | The command failed before or during the run — missing skill file, unknown model, missing credential, or an unhandled error. |
| `2` | The maximum turn count was exceeded. |
| `3` | The provider ended the response on max tokens, a content filter, or an error. |

These codes are not shown in `--help`. They are the reason this command is usable as a CI step.

## Troubleshooting

**`CodingAgentService not registered — DI wiring is incomplete.`** The agent service failed to register at startup, so a prompt run cannot proceed. The command exits `1` rather than crashing. Reinstall or update pks.

**`Unknown model …`** The `--model` value matched neither the built-in table nor an `agent.models.<id>` entry in `~/.pks-cli/settings.json`. Use `gpt-5.5`, `claude-opus-4-7`, or `claude-sonnet-4-6`, or add the model to settings.

**A red `Error:` line with no tool calls.** No credential source resolved for the chosen model. For Azure OpenAI models, confirm `agent.models.<id>.apiKey`, `AZURE_OPENAI_API_KEY`, or a logged-in `pks foundry init` session is in place. For Anthropic models — including the built-in `claude-opus-4-7`/`claude-sonnet-4-6` — a `pks foundry init` session is not enough; set `agent.models.<id>.apiKey` or `ANTHROPIC_API_KEY` instead. Then rerun.

**The agent stops with exit code `2`.** It hit the turn cap. Raise `--max-turns`, or narrow the prompt so fewer tool calls are needed.

**The agent touched a file you did not expect.** The sandbox root defaults to the current directory. Pass `--cwd` to narrow it, and `--read-only` when you want no writes at all.

## See also

- [pks agent CLI reference](/tools/pks/agent/reference) — full flag surface, including the legacy scaffolding actions
- [Agent](/tools/pks/agent) — the mental model for this branch
- [Register this session as a shareable agent](/tools/pks/agent/register) — presence rather than compute
