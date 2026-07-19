---
title: "pks mcp"
description: "Run pks-cli as a stdio or HTTP Model Context Protocol server so an AI client can call its deploy, agent, swarm, and reporting tools directly."
tags: [reference, cli, mcp-server, ai-agent]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks mcp [options]"
examples:
  - command: "pks mcp"
    description: "Start with stdio transport, the form an MCP client launches"
  - command: "pks mcp --transport stdio"
    description: "Explicit stdio transport, equivalent to the bare form"
  - command: "pks mcp --transport http --port 3000"
    description: "Start an HTTP-reachable endpoint for manual testing"
---

`pks mcp` runs pks-cli itself as a Model Context Protocol (MCP) server, exposing the CLI's own capabilities as typed tools an AI client can call directly instead of shelling out to `pks <command>`. It is a single leaf command with no subcommands — every behavior is controlled by flags on this one invocation.

## Overview

Tool discovery is automatic: the MCP SDK's `WithToolsFromAssembly()` scans the pks-cli assembly for every class carrying `[McpServerToolType]` and registers each `[McpServerTool]`-attributed method. On the current build that discovery attribute is only present on the project-init, agent/swarm, deployment, status/health, and reporting tool service classes, so a connected client's live `tools/list` surface is limited to those areas (26 tools as of this writing) — each one internally delegates to the same service pks's own equivalent command uses. Tool services for devcontainer management, GitHub, PRD generation, Git hooks, and templates exist in source with `[McpServerTool]`-attributed methods, but their containing classes are missing `[McpServerToolType]`, so the SDK never discovers them and they do not appear on a running server today.

The command itself only stands up the transport and blocks until interrupted. There is no daemon mode and no background flag.

## When to use it

Add `pks mcp` to an MCP host's server configuration — Claude Code, Claude Desktop, or any other stdio-based MCP client — so the connected model can call tools like `create_agent`, `deploy_application`, `initialize_project`, and `create_report` directly. `stdio` (the default) is the transport for this durable, editor/agent-integration use case.

Use `--transport http` only for manual testing or inspection with an HTTP-capable MCP client, such as the MCP Inspector. It binds to `localhost` and is not meant for interactive human use or for reaching the server from outside the host machine.

Do not reach for `pks mcp` to run one CLI command a single time — invoke `pks <command>` directly for that.

## Prerequisites

- **pks-cli installed** and reachable as `pks` on the machine or container that will run the server. See [pks](/tools/pks) for install paths.
- **Credentials for whatever subsystem the AI client will call through.** There is no MCP-level login step — a deployment tool needs its target's credentials already stored under `~/.pks-cli/` before a connected client can call it.
- **An MCP host that speaks stdio** (Claude Code, Claude Desktop) if you're wiring this in for normal use, or an HTTP-capable MCP client if you're using `--transport http` for manual testing. `--transport sse` is accepted but currently binds no listener — see Troubleshooting.

## Synopsis

```text
pks mcp [options]
```

`pks mcp` has no subcommands — the synopsis above is the complete command surface.

## Options

| Flag | Default | Description |
|---|---|---|
| `-t, --transport <text>` | `stdio` | Transport mode: `stdio`, `http`, or `sse`. |
| `-p, --port <int>` | `3000` | Port number for HTTP/SSE transport. |
| `-d, --debug` | `false` | Toggles the command's own start/stop banner messages for HTTP/SSE transports. No effect under `stdio`, and does not raise the server's internal log verbosity. |
| `-c, --config <path>` | — | Path to a configuration file. Parsed and stored on the server config object but never read anywhere — currently a dead flag with no effect on server behavior. |

## Authentication and tool permissions

There is no additional handshake or API key at the MCP layer itself. Every exposed tool inherits whatever auth its underlying subsystem already requires — for example a deployment tool needs its target's stored credentials.

> **Note.** The exposed tool surface is mutating: it includes tools that deploy applications, scale or roll back deployments, spawn or stop agents, initialize swarms, create tasks/feature requests/bug reports, and initialize projects. Any connected client can invoke these, subject only to that subsystem's own stored credentials — there is no MCP-specific confirmation gate in this command.

## Examples

```bash
pks mcp
```

Starts the server on stdio transport, the form used when an MCP host launches pks-cli as a server.

```bash
pks mcp --transport http --port 3000
```

Starts an HTTP-reachable MCP endpoint on port `3000`, bound to `localhost`, for manual or remote testing.

## Troubleshooting

- **Nothing appears on screen under `stdio`.** This is by design — stdout is reserved for the JSON-RPC MCP protocol stream, so all console output (banner, start/stop messages, errors) is suppressed. Verify the server is working by connecting an actual MCP client rather than watching the terminal; internal logging is routed to stderr.
- **`--config` has no effect.** The flag is accepted and stored but never consumed — passing a config file path does not change server behavior on this version.
- **No way to stop or check status from another terminal.** There is no `pks mcp stop` or `pks mcp status` subcommand. The running process must be interrupted with Ctrl+C, which triggers a graceful shutdown, or killed at the OS level. Equivalent stop/status/restart operations exist as MCP tools callable by a connected client, not as `pks` subcommands.
- **`Unsupported transport: <value>` and exit code 1.** Only `stdio`, `http`, and `sse` (case-insensitive) are valid values for `--transport`.
- **Log/metrics tools show implausible numbers.** The MCP tools that report server logs and performance metrics return partly sample data on this version — do not treat their output as real telemetry.
- **HTTP endpoint unreachable from another machine.** `--transport http` binds only to `localhost`, so it is not reachable from outside the host or devcontainer without separate port forwarding.
- **`--transport sse` accepts the flag, logs a success message, but nothing ever connects.** SSE mode is currently non-functional: it builds a plain background host instead of a web host, so it never calls `UseUrls` or maps an endpoint — no TCP listener is opened at all, on `localhost` or otherwise. The process stays up and reports success, but any client attempting to connect (even from `localhost`) gets connection refused. Use `--transport http` instead until this is fixed.

## See also

- [pks](/tools/pks) — the CLI's full command surface and install paths
- [pks agentics](/tools/pks/agentics) — runner registration and Assembly Line task submission, one of the tool areas exposed over MCP
- [pks github](/tools/pks/github) — GitHub auth and repository operations; the CLI has source for GitHub MCP tools, but they are not currently discovered by the running server (see Overview)
- [pks agent](/tools/pks/agent) — spawn and manage the agents that MCP's agent tools control
- [pks hooks](/tools/pks/hooks) — Git hook install/uninstall behavior; MCP tool methods exist in source but are not currently discovered by the running server (see Overview)
- [pks prd](/tools/pks/prd) — PRD generation; an MCP tool method exists in source but is not currently discovered by the running server (see Overview)
