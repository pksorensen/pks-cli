---
title: "pks claude reference"
description: "Complete command, flag, argument, and environment-variable reference for the pks claude branch — spawn, model launchers, analytics, backup, and settings."
tags: [reference, cli, claude-code]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks claude <command> [options]"
examples:
  - command: "pks claude --ssh-target my-vm"
    description: "Spawn and attach on a registered SSH target"
  - command: "pks claude stats --all-projects"
    description: "Response-time stats across every local project"
  - command: "pks claude usage -s a1b2c3d4"
    description: "Cost for one session id, prefix matched"
  - command: "pks claude limits --json"
    description: "Quota and pace as machine-readable JSON"
  - command: "pks claude codex --print-env"
    description: "Run the proxy and print ANTHROPIC_ exports only"
  - command: "pks claude managed-settings --output ./managed-settings.json"
    description: "Write the rendered policy file to a path"
---

`pks claude` is the Claude Code launcher and local-analytics branch of the pks CLI. It spawns devcontainer or inline sessions, routes Claude Code at Azure AI Foundry or Scaleway model backends through a local translating proxy, reports cost and quota from `~/.claude/projects/**/*.jsonl`, mirrors `~/.claude` to remote targets, and renders an enterprise settings file.

The branch is registered in `src/Program.cs` as a Spectre.Console.Cli branch with a default command, so bare `pks claude [PROJECT_PATH]` is the spawn command rather than a help screen. pks itself is a .NET 10 tool distributed as `pks-cli` on NuGet and `@pks-cli/cli` on npm.

## Synopsis

```text
pks claude <command> [options]
```

```text
(default)          Spawn a devcontainer and attach an interactive claude session
stats              Activity heatmap, streaks, tokens, response-time trends
usage              Deduplicated, priced cost analysis of billed requests
limits             Session and week usage limits as structured data
session-usage      Second registered name for the limits command
codex              Claude Code against an Azure AI Foundry Codex/GPT deployment
scaleway           Claude Code against the Scaleway serverless catalog
mistral            Scaleway launcher narrowed to the Mistral/Devstral family
qwen               Scaleway launcher narrowed to the Qwen family
anthropic          Claude Code against a first-party Anthropic model, no proxy
backup             rsync ~/.claude to every registered rsync target
managed-settings   Render a Claude Code managed-settings.json
```

### Branch environment variables

These are set by pks into the child `claude` process, or read from your shell by the launchers. They are not general pks configuration.

| Variable | Default | Purpose |
|---|---|---|
| `ANTHROPIC_BASE_URL` | (unset) | Read by the `claude` CLI. The `codex`, `scaleway`, `mistral`, and `qwen` launchers set it to the local proxy at `http://127.0.0.1:<port>`. The inline Foundry path skips its own wiring when this is already set. |
| `ANTHROPIC_AUTH_TOKEN` | (unset) | Set to a per-run random GUID that the local proxy validates on every inbound request. |
| `ANTHROPIC_API_KEY` | (unset) | Removed from the child process environment by `codex`, `scaleway`, `mistral`, and `qwen`, so a real Anthropic key cannot hijack the intended routing. |
| `ANTHROPIC_DEFAULT_OPUS_MODEL` | (unset) | Set by the proxy launchers and the inline Foundry path to aim Claude Code tier routing at the chosen deployment. |
| `ANTHROPIC_DEFAULT_SONNET_MODEL` | (unset) | As above, for the Sonnet tier. |
| `ANTHROPIC_DEFAULT_HAIKU_MODEL` | (unset) | As above, for the Haiku tier. |
| `CLAUDE_CODE_USE_FOUNDRY` | (unset) | Set to `1` by `pks claude --inline` when you opt into Foundry mode for the inline session. |
| `ANTHROPIC_FOUNDRY_RESOURCE` | (unset) | Set by the inline local MSI token server so Claude Code can authenticate against the Foundry resource. |
| `ANTHROPIC_FOUNDRY_API_KEY` | (unset) | Set by the inline local MSI token server. |
| `IDENTITY_ENDPOINT` | (unset) | Set by the inline local MSI token server. |
| `IDENTITY_HEADER` | (unset) | Set by the inline local MSI token server. |
| `AGENTIC_SERVER` | (unset) | Forwarded into the spawned container from `--server <URL>`. Not read by the branch itself. |
| `PKS_LIMITS_SINK` | (unset) | Internal path to the temp JSON file the MCP-enabled `claude` process writes parsed usage numbers to during `limits --llm`. |
| `COLUMNS` | (unset) | Fallback terminal width for the `stats` and `usage` chart renderers when the console size is not reliable. |
| `LINES` | (unset) | Fallback terminal height for the same renderers. |

## (default)

Bare `pks claude [PROJECT_PATH]` spawns a devcontainer for the project and attaches an interactive `claude --dangerously-skip-permissions` session inside it over `docker exec`. With no `--ssh-target` and no `--inline`, it prompts for the location — local Docker, a registered SSH target, or a new VM. It detects an existing container for the project and offers reuse, comparing the host `.devcontainer`, the container build-time label, and the live volume. With `--inline` it skips containers entirely and runs `claude --dangerously-skip-permissions` in the current shell, optionally standing up a local Azure AI Foundry MSI token server first.

On a clean remote-session exit it prompts for a post-quit action: reattach VS Code, keep the container running, stop it, or remove it and its volumes. Remote exits may additionally offer to deallocate the backing Azure VM, gated by a second factor. When ADO git repos are registered and you are ADO-authenticated, it offers to deploy a local ADO git-credential proxy into the container and rewrite `git` URL `insteadOf` rules.

| Argument | Required | Description |
|---|---|---|
| `PROJECT_PATH` | no | Path to the project directory. Defaults to the current directory. |

| Flag | Description |
|---|---|
| `-o`, `--output-path <PATH>` | Output directory for devcontainer files. Defaults to the current working directory. |
| `-v`, `--verbose` | Enable verbose output. |
| `-f`, `--force` | Force overwrite of existing files, and skip the existing-container detection prompt. |
| `--dry-run` | Show what would be done without making changes. |
| `--volume-name <NAME>` | Custom volume name for the devcontainer. |
| `--no-launch-vscode` | Do not launch VS Code after spawning. |
| `--no-copy-source` | Copy only the `.devcontainer` configuration, not the source files. |
| `--no-bootstrap` | Use direct execution instead of a bootstrap container. Advanced. |
| `--forward-docker-config` | Forward Docker credentials from the host into the devcontainer. Enabled by default, matching VS Code behavior. |
| `--no-forward-docker-config` | Disable Docker credential forwarding. |
| `--docker-config-path <PATH>` | Path to the `config.json` to forward. Defaults to `~/.docker/config.json`. |
| `--rebuild` | Force a rebuild even when no configuration change is detected. |
| `--no-rebuild` | Skip the rebuild even when a configuration change is detected. |
| `--auto-rebuild` | Rebuild without prompting when a configuration change is detected. |
| `--ssh-target <TARGET>` | SSH target label or host to spawn on remotely. Skips the interactive location prompt. |
| `--env <ENV>` | Extra environment variable as `KEY=VALUE`, forwarded into the container. Repeatable. Also honored by `--inline`, where it is exported into the local process. |
| `--server <URL>` | Agentic server URL forwarded into the container as `AGENTIC_SERVER`. |
| `--inline` | Run in the current shell with no devcontainer. |

## stats

Parses every `~/.claude/projects/**/*.jsonl` transcript in scope and renders an activity heatmap, session streaks and durations, total token counts, a response-time-over-time chart in milliseconds per output token, a per-model breakdown table, and an all-time percentile table. The chart splits a recent window against the prior period. Nothing leaves the machine.

| Flag | Default | Description |
|---|---|---|
| `-d`, `--days <N>` | `7` | Number of recent days treated as the recent window. |
| `-p`, `--project <PATH>` | current directory | Project directory to analyse. |
| `--all-projects` | — | Analyse every project under `~/.claude/projects/` instead of only the current one. |

## usage

Parses transcripts into billed-request rows, deduplicates globally by `requestId` plus `message.id`, prices each surviving row, and renders an hourly cost chart for the last 24 hours, a daily cost chart, and a cost summary with a top-five-models-by-cost table. Pricing prefers the transcript's own `costUSD`, then a live-fetched LiteLLM pricing table, then a small hardcoded fallback. An incremental cache at `~/.pks-cli/usage-cache/manifest.json`, keyed by file size and modification time, keeps unchanged transcripts from being re-parsed.

| Argument | Required | Description |
|---|---|---|
| `PROJECT` | no | Folder name under `~/.claude/projects/`, matched as a substring. Omit to scan every project. |

| Flag | Default | Description |
|---|---|---|
| `-s`, `--session <SESSION>` | — | Session id — the `.jsonl` filename — matched as a prefix or substring across all projects. Repeatable. Intersects with `PROJECT` when both are given. |
| `-d`, `--days <N>` | `7` | Number of most-recent days highlighted in red. |

## limits

Reports SESSION and WEEK usage-limit percentages, reset times, and pace as structured data. It spawns a fresh detached tmux session running `claude --dangerously-skip-permissions`, types `/usage`, captures the rendered panel, and parses it deterministically. When the deterministic parse finds no blocks, or when `--llm` is passed, it falls back to spawning a `claude -p` call configured with `--mcp-config` pointing at `pks mcp --transport stdio` and restricted to the `report_session_limits` tool, which writes a JSON sink file that is read back into the same block structure.

| Flag | Default | Description |
|---|---|---|
| `--json` | auto | Emit structured JSON to stdout. Enabled automatically when stdout is not a TTY. |
| `--llm` | — | Force the MCP round-trip fallback instead of the deterministic parser. |
| `--timeout <SECONDS>` | `60` | Whole-capture timeout covering boot, `/usage` render, and kill. |
| `--model <ID>` | `claude-haiku-4-5-20251001` | Model id used for the fallback round trip. A cheap model is recommended. |
| `--debug` | — | Dump the raw captured tmux pane to stderr before parsing. |

## session-usage

A second registered name for the same command class and settings as `limits`. Behavior, flags, and failure modes are identical. It is a separate `AddCommand` registration, not a Spectre alias.

## codex

Runs the real `claude` CLI locally against a Codex or GPT-5.x deployment on Azure AI Foundry. pks hosts an in-process ASP.NET Core proxy on `127.0.0.1` that accepts the Anthropic Messages API inbound and translates every request into an Azure OpenAI Responses API call, streaming the reply back as Anthropic server-sent events. Codex reasoning summaries surface as Claude thinking blocks unless `--no-thinking` is passed. Before launching, it preflights the chosen deployment with a small real request.

| Argument | Required | Description |
|---|---|---|
| `claudeArgs` | no | Extra arguments passed through to the `claude` CLI. |

| Flag | Default | Description |
|---|---|---|
| `-m`, `--model <NAME>` | stored Foundry default (if codex-like), else `gpt-5.5` | Foundry deployment name. Falls back to the configured Foundry default model when it looks codex-like, then to `gpt-5.5`. |
| `-e`, `--reasoning-effort <LEVEL>` | `medium` | One of `none`, `low`, `medium`, `high`, `xhigh`. |
| `-p`, `--port <PORT>` | random free port | Port for the local proxy. |
| `--print-env` | — | Run the proxy in the foreground and print `ANTHROPIC_` exports instead of launching Claude Code. |
| `--no-thinking` | — | Do not surface Codex reasoning summaries as Claude thinking blocks. |

## scaleway

Runs Claude Code against any model in the Scaleway serverless catalog. A local proxy translates the Anthropic Messages API to the OpenAI Chat Completions API that Scaleway exposes and streams the reply back as Anthropic server-sent events, with the same preflight-before-launch pattern as `codex`.

| Argument | Required | Description |
|---|---|---|
| `model` | no | Model id to use. Omit to choose interactively. |
| `claudeArgs` | no | Extra arguments passed through to the `claude` CLI. |

| Flag | Default | Description |
|---|---|---|
| `-p`, `--port <PORT>` | random free port | Port for the local proxy. |
| `--print-env` | — | Run the proxy in the foreground and print `ANTHROPIC_` exports instead of launching Claude Code. |
| `--no-thinking` | — | Do not surface model reasoning as Claude thinking blocks. |

### mistral

The `scaleway` command with the picker and default narrowed to the Mistral and Devstral family. Same arguments, flags, proxy, and preflight.

### qwen

The `scaleway` command with the picker and default narrowed to the Qwen family. Same arguments, flags, proxy, and preflight.

## anthropic

Launches `claude --model <id>` against a first-party Anthropic model using whatever real Anthropic authentication the `claude` CLI already has. There is no local proxy and no translation layer. The picker offers `claude-opus-4-8`, `claude-sonnet-4-6`, and `claude-haiku-4-5` from a hardcoded list. This command does not strip `ANTHROPIC_API_KEY`.

| Argument | Required | Description |
|---|---|---|
| `model` | no | Claude model id. Omit to choose interactively. |
| `claudeArgs` | no | Extra arguments passed through to the `claude` CLI. |

## backup

Backs up the entire `~/.claude` directory — sessions, projects, settings — via `rsync -avz --delete` over SSH to every registered rsync target in sequence, with a spinner per target and a final results table showing success or failure, duration, and the rsync stats line. Exit code is `0` only when every target succeeded. It takes no arguments and no flags.

SSH runs with `BatchMode=yes` and `StrictHostKeyChecking=accept-new`.

## managed-settings

Renders a Claude Code `managed-settings.json` from the marketplaces registered in `~/.pks-cli/claude-marketplace.json`. That registry is populated elsewhere, not by any `pks claude` subcommand. Output goes to stdout by default. The canonical file target is `/etc/claude-code/managed-settings.json`, the OS-level managed-policy location Claude Code reads on devcontainer and enterprise images.

| Flag | Description |
|---|---|
| `--output <PATH>` | Write to a file instead of stdout, creating parent directories as needed. |

## See also

- [pks claude](/tools/pks/claude) — the group landing page and mental model
- [Devcontainer and inline sessions](/tools/pks/claude/devcontainer-sessions) — the default command end to end
- [pks claude limits](/tools/pks/claude/limits) — quota polling and its failure modes
- [pks claude codex](/tools/pks/claude/codex) — the Foundry proxy path
- [pks claude backup](/tools/pks/claude/backup) — mirroring `~/.claude` to remote targets
