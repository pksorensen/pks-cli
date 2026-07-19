---
title: "Agent"
description: "Run a one-shot LLM coding agent from the pks CLI, or enroll the current session as a shareable agent that people and other agents can send work to."
tags: [cli, agents, llm, agent-share]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks agent <prompt> [options]"
examples:
  - command: "pks agent \"summarise this repo\""
    description: "Run the one-shot coding agent in the current directory"
  - command: "pks agent \"fix the lint errors\" --read-only"
    description: "Inspect-only run with mutating tools disabled"
  - command: "pks agent run \"apply the migration\" --cwd ./src"
    description: "Scope the tool sandbox to a subdirectory"
  - command: "pks agent register"
    description: "Enroll this session as a shareable agent"
---

`pks agent` covers two capabilities that share a command branch: a provider-neutral coding agent that runs a prompt to completion inside the CLI process, and a registration command that makes the current session visible as a person-like agent in Agent Share.

## Overview

`pks agent "<prompt>"` runs an in-process tool-use loop against Azure OpenAI or Anthropic. The model gets a fixed tool set — `read`, `write`, `edit`, `bash`, `grep`, `find`, `ls` — all rooted at a single sandbox directory, and the loop runs until the model stops, the turn cap is hit, or the provider ends the response.

`pks agent register` does something entirely different. It enrolls the current terminal session against a configured Agent Share provider so that other people and agents can share content to it. No LLM is involved.

- **One-shot, non-interactive.** There is no chat session to attach to. You give one prompt, the agent works, the process exits with a status code.
- **Sandboxed by directory.** Every tool call is rooted at `--cwd`, which defaults to the current working directory.
- **Provider-neutral.** The default model is `gpt-5.5` on Azure OpenAI; Anthropic models are selected with `--model`.
- **Separate from Claude Code.** `pks agent` does not spawn Claude Code or Codex. For a full remote coding session, use `pks claude` or `pks vibecast` instead.

## What you get

- **A scriptable agent loop.** `pks agent "<prompt>"` returns an exit code you can branch on in CI: `0` for a clean stop, `2` when the turn cap is exceeded, `3` when the provider ends on max tokens, a content filter, or an error.
- **A read-only mode.** `--read-only` removes `write`, `edit`, and `bash` from the tool registry, leaving inspection tools only.
- **Custom system prompts.** `--skill <name>` loads `~/.pks-cli/agent-skills/<name>.md` and uses it as the system prompt in place of the default.
- **Turn and scope caps.** `--max-turns` bounds the tool-call iterations; `--cwd` bounds what the agent can touch.
- **Session enrollment.** `pks agent register` mints a per-user agent inbox on the Agent Share server, so the session shows up in the Share panel as somewhere to send screenshots, links, and decisions.

## How it fits together

The coding agent runs entirely inside the `pks` process. It resolves a model, resolves a credential for that model, builds a tool registry rooted at the sandbox directory, and then loops: send the conversation, execute any tool calls the model returns, send the results back. Nothing is persisted between runs — each invocation starts from an empty conversation.

Registration goes the other way. It reads the Agent Share credential stored by `pks share init`, refreshes the stored OIDC token, and posts an enrollment to the share server. The server resolves the owner from the bearer token and creates the inbox. The registered agent appears in the Share panel within about 30 seconds.

- **`agent run`** is compute: a model, a sandbox, and a bounded loop.
- **`agent register`** is presence: an identity on a share server, with no model behind it.

A third surface lives on the same command: the `-a create|list|status|start|stop|remove` action flags. These generate persona and knowledge markdown under `<cwd>/.pks/agents/` and track status in memory only. They start no process. They are documented on the reference page so you recognize them, not so you build on them.

## Commands

`run` · `register`

Bare `pks agent "<prompt>"` is rewritten to `pks agent run "<prompt>"` before the argument parser sees it, so the two forms are the same command. See the reference page for the full flag surface.

## Next steps

- [Run a one-shot coding agent](/tools/pks/agent/run) — install prerequisites, pick a model, and run your first prompt end to end
- [Register this session as a shareable agent](/tools/pks/agent/register) — connect to an Agent Share server and appear in the Share panel
- [pks agent CLI reference](/tools/pks/agent/reference) — every flag, argument, environment variable, and exit code
- [pks](/tools/pks) — the full command surface this branch belongs to
