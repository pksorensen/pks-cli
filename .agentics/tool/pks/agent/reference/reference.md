---
title: "pks agent CLI reference"
description: "Complete command, argument, flag, environment-variable, and exit-code reference for the pks agent branch — the coding agent and session registration."
tags: [reference, cli, agents, llm]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks agent <command> [options]"
examples:
  - command: "pks agent \"summarise this repo\""
    description: "Run the coding agent with the default model"
  - command: "pks agent run \"fix the lint errors\" --read-only"
    description: "Explicit run form with mutating tools disabled"
  - command: "pks agent \"apply the migration\" --cwd ./src --max-turns 20"
    description: "Narrow the sandbox and cap tool iterations"
  - command: "pks agent register \"my-session\" --role \"coding session\""
    description: "Non-interactive session enrollment"
---

The `pks agent` branch has two leaves: `run`, an in-process LLM tool-use loop, and `register`, an Agent Share enrollment. The branch's default command is `run`, and the argument vector is rewritten before parsing so that `pks agent "<prompt>"` and `pks agent run "<prompt>"` behave identically.

A third surface — the `-a create|list|status|start|stop|remove` action flags — sits on the same command class as `run`. It generates markdown scaffolding under `<cwd>/.pks/agents/` and tracks status in memory. It is documented here for recognition, not for use.

## Synopsis

```text
pks agent <command> [options]
```

```text
run         Run a one-shot LLM tool-use agent against a prompt   [default command]
register    Enroll this session as a shareable Agent Share agent
```

### Global environment variables

| Variable | Default | Purpose |
|---|---|---|
| `ANTHROPIC_API_KEY` | `(unset)` | Anthropic API key used by `run` when no `agent.models.<id>.apiKey` is configured. Checked before Foundry. |
| `ANTHROPIC_BASE_URL` | `(unset)` | Overrides the Anthropic endpoint for `run`. Used only when no explicit `agent.models.<id>.endpoint` is configured. |
| `AZURE_OPENAI_API_KEY` | `(unset)` | Azure OpenAI API key used by `run` when `agent.models.<id>.apiKey` is unset. |

Model configuration is read from `agent.models.<id>.*` keys in `~/.pks-cli/settings.json` and takes precedence over the environment variables above. Skill files are read from `~/.pks-cli/agent-skills/<name>.md`.

## run

Runs a bounded tool-use loop against a single prompt and exits. The command resolves the model, resolves a credential for that model, constructs the tool registry rooted at the sandbox directory, and then iterates: send the conversation, execute returned tool calls, send results back. Nothing persists between invocations.

The tool set is `read`, `write`, `edit`, `bash`, `grep`, `find`, and `ls`. All seven are rooted at `--cwd`, which is the sandbox boundary for the run.

When the `[prompt]` argument is empty, the command falls through to the legacy action switch documented below.

| Argument | Required | Description |
|---|---|---|
| `[prompt]` | no | Prompt for the agent. When present, every legacy action option is ignored. |

| Flag | Default | Description |
|---|---|---|
| `-m <id>`, `--model <id>` | `gpt-5.5` | Model id. Built-in ids are `gpt-5.5`, `claude-opus-4-7`, and `claude-sonnet-4-6`; other ids resolve through `agent.models.<id>.*` settings keys. |
| `--cwd <dir>` | current directory | Sandbox root for every tool call. |
| `--skill <name>` | — | Loads `~/.pks-cli/agent-skills/<name>.md` as the system prompt, replacing the default body. |
| `--max-turns <n>` | `50` | Maximum tool-call iterations. Values of `0` or below fall back to `50`. |
| `--read-only` | `false` | Removes `write`, `edit`, and `bash` from the tool registry. |

### Model credential resolution

Anthropic models resolve a key from `agent.models.<id>.apiKey`, then `ANTHROPIC_API_KEY`. The built-in `claude-opus-4-7` and `claude-sonnet-4-6` ids are hardcoded to `https://api.anthropic.com`, so neither ever falls back to a Foundry session — a `pks foundry init` login alone leaves them without a credential. A Foundry-served Claude endpoint only comes into play for a custom `agent.models.<id>` entry you configure yourself with an `endpoint` on a `*.services.ai.azure.com` host.

Azure OpenAI models — including the built-in default `gpt-5.5` — need an explicit endpoint, or they fall back to the Foundry-selected resource and the default Azure credential chain.

When no source resolves, the run throws and surfaces as a red `Error:` line with exit code `1`.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | The loop stopped cleanly. |
| `1` | The command failed — missing skill file, unknown model, unresolved credential, or an unhandled error. |
| `2` | The maximum turn count was exceeded. |
| `3` | The provider finished on max tokens, a content filter, or an error. |

These codes are absent from `--help` output.

```bash
pks agent "fix the lint errors" --model claude-sonnet-4-6 --read-only
pks agent run "apply the migration" --cwd ./src --max-turns 20 --skill db-migrations
```

## register

Enrolls the current session as a person-like agent against a configured Agent Share provider. Each invocation refreshes the stored OIDC access token with a refresh-token grant against the credential's issuer, persists a rotated refresh token when the issuer returns one, and posts the enrollment. The share server resolves the owner from the bearer token and mints a per-user agent inbox.

**Endpoint:** `POST {shareHost}/api/agents/enroll`.

The command does not wire any local MCP server. The `share-agent` MCP wiring is a separate, once-per-machine `agent-share install` step.

| Argument | Required | Description |
|---|---|---|
| `NAME` | no | Agent name as shown when sharing. Prompted in an interactive terminal; defaults to the current directory's basename when stdin is redirected. |

| Flag | Default | Description |
|---|---|---|
| `-r <text>`, `--role <text>` | `coding session` | One-line role or description. Prompted interactively when omitted. |
| `-p <name>`, `--provider <name>` | the sole configured provider | Provider to register against. `share` is the only built-in provider. A selection prompt appears when several are configured and the terminal is interactive. |

With no provider configured, the command prints `No agent provider is configured. Set one up first, e.g.: pks share init` and exits `1`. A token or network failure prints `Registration failed: <message>` and exits `1`.

```bash
pks agent register
pks agent register "my-session" --role "coding session" --provider share
```

## Legacy scaffolding actions

These options live on the same settings class as `run` and are used only when no `[prompt]` argument is given. They read and write `<cwd>/.pks/agents/`, generating `knowledge.md` and `persona.md` per agent, and they track Active or Inactive status in an in-process dictionary. No process, port, or job exists behind them.

| Flag | Default | Description |
|---|---|---|
| `-a <action>`, `--action <action>` | `list` | Action to perform: `create`, `list`, `status`, `start`, `stop`, or `remove`. |
| `-n <name>`, `--name <name>` | — | Agent name, used by `create`. |
| `-t <type>`, `--type <type>` | `automation` | Free-text agent type written into the generated persona and knowledge files. |
| `-i <id>`, `--id <id>` | — | Agent id for `status`, `start`, `stop`, and `remove`. |
| `-c <path>`, `--config <path>` | — | Path to a JSON agent configuration file, used by `create` in place of `-n`, `-t`, and `-s`. |
| `-s <k=v>`, `--settings <k=v>` | — | Additional settings appended to the generated agent's settings dictionary, used by `create`. |

Behavior worth knowing before you rely on any of it:

- Agent ids have the shape `<name-lower>-<namehash>-<unixtimestamp>` and are regenerated by the filesystem scan on every invocation, so they are not stable across process restarts.
- `create` writes `<cwd>/.pks/agents/<name>/{knowledge.md,persona.md}` to disk and prints the freshly generated id, but that id is only ever stored in the process's in-memory dictionaries.
- `status`, `start`, `stop`, and `remove` never scan the filesystem — they look the `-i <id>` you pass up only in those same in-memory dictionaries. Every `pks agent` invocation is a fresh process, so the dictionaries are always empty: **all four unconditionally print `❌ Agent not found: Agent '<id>' not found` and exit `1`, even immediately after `create` printed that exact id.**
- Because `remove` fails at that same lookup, it never reaches a delete step — the on-disk `<cwd>/.pks/agents/<name>` directory is never removed through this command. Delete it by hand (`rm -rf`) if you want it gone.
- `list` scans `<cwd>/.pks/agents`, so running it from a different directory than where `create` ran shows nothing.

For real remote sessions, use `pks claude` or `pks vibecast`, which spawn a devcontainer session over SSH.

```bash
pks agent -a create -n my-agent --type automation
pks agent -a list
```

## Argument-vector rewriting

The branch's default command cannot bind a positional argument directly, so the argument vector `agent <token>` is rewritten to `agent run <token>` before parsing whenever `<token>` is not `run`, `register`, `-h`, `--help`, or `--version`. This is why `pks agent "summarise this repo"` works, and why a mistyped subcommand is treated as a prompt rather than reported as an unknown command.

## See also

- [Run a one-shot coding agent](/tools/pks/agent/run) — step-by-step setup, credentials, and troubleshooting
- [Register this session as a shareable agent](/tools/pks/agent/register) — the enrollment flow end to end
- [Agent](/tools/pks/agent) — branch overview and mental model
- [pks](/tools/pks) — the full CLI surface
