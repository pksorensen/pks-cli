---
title: "pks"
description: "Single-operator toolbelt for running AI coding agents across devcontainers, cloud VMs, credentials, and issue trackers — one .NET global tool."
tags: [cli, agents, devcontainers, infrastructure]
category: infrastructure
platform: [linux, macos, windows]
icon: terminal
status: stable
type: cli
author: Poul Kjeldager
component: pks
usage: "pks <command> [options]"
examples:
  - command: "dotnet tool install -g pks-cli"
    description: "Install pks as a .NET global tool"
  - command: "pks claude"
    description: "Spawn a devcontainer and attach a Claude Code session"
  - command: "pks vm init"
    description: "Provision a cloud VM and register it as an SSH target"
  - command: "pks agentics runner start"
    description: "Run the self-hosted Agentics job runner"
  - command: "pks claude limits"
    description: "Report session and week usage limits as structured data"
  - command: "pks brain refresh"
    description: "Rebuild the personal brain from Claude session history"
---

`pks` is a .NET 10 command-line application, built on Spectre.Console.Cli and shipped as the `pks-cli` package. It is the connective tissue for running AI coding agents somewhere other than your laptop.

## Overview

`pks` is a single binary that spans the whole loop an operator runs: scaffold a project, give an agent a runtime, put that runtime on a machine, hand it credentials, feed it work items, watch what it did, and collect what it produced. The command surface is broad on purpose — 57 top-level groups — because each step of that loop otherwise needs a different vendor CLI.

- **Agent runtimes.** Launch Claude Code, Codex, or a provider-neutral in-process agent, locally or inside a container on a remote host.
- **Machines.** Provision Azure and Scaleway VMs, boot Firecracker microVMs, and drive devcontainers over SSH.
- **Credentials.** Sign in once to Azure, Azure AI Foundry, GitHub, Azure DevOps, Jira, Google, and Scaleway, then let every other command reuse that login.
- **Work and output.** Pull tickets, submit assembly-line tasks, query telemetry, and generate speech, images, and transcripts.

## Install

Two routes ship the same command surface. Route A needs the .NET 10 SDK; route B needs only Node 18 or newer and carries a self-contained binary per platform. The commands are identical on Linux, macOS, and Windows.

**A — .NET global tool (canonical):**

```bash
dotnet tool install -g pks-cli
dotnet tool update -g pks-cli               # stable channel
dotnet tool update -g pks-cli --prerelease  # daily channel
```

**B — npm, no .NET required:**

```bash
npm install -g @pks-cli/cli
```

The platform binary (`@pks-cli/cli-linux-x64`, `@pks-cli/cli-osx-arm64`, `@pks-cli/cli-win-x64`, and the rest) resolves through `optionalDependencies`, so the install line does not change per operating system.

Confirm the install:

```bash
pks --version
```

You should see the current version printed — 6.20.1 at the time of writing. After the first install, `pks update` handles upgrades and detects which of the two routes you used.

## How it fits together

Every command reads from one config root: `$HOME/.pks-cli` on Linux and macOS, `C:\Users\<user>\.pks-cli` on Windows. Sign-in commands such as `pks azure init`, `pks foundry init`, and `pks github init` write credentials there, and everything else reads them back. A second, repo-local folder, `.pks/`, holds per-project state such as the project identity and generated agent definitions. The two are separate and both load-bearing.

The agent-runtime commands build on the machine commands rather than duplicating them. `pks vm init` provisions a box and registers it as a named SSH target. `pks devcontainer spawn`, `pks claude`, and `pks vibecast` then address that target by name, ship the project's `.devcontainer` to it, and attach an interactive session inside the resulting container. Sensitive steps — remote spawn, VM power operations, outbound SSH, self-update — pass through a two-factor action guard you configure with `pks actions` after enrolling a factor with `pks authenticator init`.

- **On your machine:** login state, project scaffolding, and local analytics over Claude Code session transcripts.
- **On someone else's machine:** the containers, microVMs, and runners doing the work, reached over SSH.

## Command families

The 57 groups fall into seven families. Each group has its own page.

### Core lifecycle and CLI plumbing

Scaffolding a project and managing `pks` itself.

| Group | What it does |
|---|---|
| [pks init](/tools/pks/init) | Scaffold a new project from a NuGet devcontainer template and optionally spawn it. |
| [pks exec](/tools/pks/exec) | Launch any tool that speaks the `PKS_DISCOVERY` contract, injecting a chosen LLM provider. |
| [pks update](/tools/pks/update) | Update the CLI on the stable or daily channel, per detected install method. |
| [pks report](/tools/pks/report) | File a GitHub issue with version, environment, and local usage stats attached. |
| [pks status](/tools/pks/status) | Render the system-status dashboard. |
| [pks deploy](/tools/pks/deploy) | Render the deployment-flow demo. |

### AI agents, agent runtimes, and MCP

Getting a coding agent running, wiring it to a model, and connecting it to the Assembly Line Platform.

| Group | What it does |
|---|---|
| [pks claude](/tools/pks/claude) | Spawn Claude Code in a devcontainer or inline, point it at non-Anthropic backends, and analyse local usage. |
| [pks agent](/tools/pks/agent) | Run a one-shot, provider-neutral coding-agent loop, or register the session with Agent Share. |
| [pks agentics](/tools/pks/agentics) | Log in to agentics.dk, run the self-hosted job runner, and submit assembly-line tasks. |
| [pks codex](/tools/pks/codex) | Run the upstream Codex CLI against an Azure AI Foundry deployment with no request translation. |
| [pks mcp](/tools/pks/mcp) | Serve the CLI's own capabilities to a Model Context Protocol client over stdio or HTTP. |
| [pks hooks](/tools/pks/hooks) | Register `pks` as the handler for Claude Code lifecycle hooks, including a lint gate on stop. |
| [pks brain](/tools/pks/brain) | Build a personal knowledge base from your Claude Code session history. |
| [pks marketplace](/tools/pks/marketplace) | Register and curate Claude Code plugin marketplaces. |
| [pks share](/tools/pks/share) | Log this host into an Agent Share server over OIDC. |
| [pks vibecast](/tools/pks/vibecast) | Spawn a remote devcontainer and drop into a vibecast session inside it. |
| [pks prd](/tools/pks/prd) | Scaffold, validate, and template product requirements documents. |

### Machines, microVMs, and remote development environments

Where the agents actually run.

| Group | What it does |
|---|---|
| [pks devcontainer](/tools/pks/devcontainer) | Author, validate, spawn, connect to, and destroy devcontainers, locally or over SSH. |
| [pks vm](/tools/pks/vm) | Provision, start, stop, inspect, and destroy the Azure and Scaleway VMs that host containers. |
| [pks schedule](/tools/pks/schedule) | Configure a VM's daily start, daily shutdown, and idle-shutdown watchdog. |
| [pks firecracker](/tools/pks/firecracker) | Bootstrap and run a Firecracker microVM job runner for isolated execution. |
| [pks ssh](/tools/pks/ssh) | Manage named SSH targets and a pks-held encrypted key store behind the action guard. |
| [pks rsync](/tools/pks/rsync) | Register rsync backup targets such as a NAS or a remote host. |
| [pks tailscale](/tools/pks/tailscale) | Store a Tailscale auth key and join preferences for VM enrollment. |
| [pks scaleway](/tools/pks/scaleway) | Authenticate against Scaleway with a static API key pair. |

### Cloud identity, secrets, and signing

The credential backbone the rest of the tool draws on.

| Group | What it does |
|---|---|
| [pks azure](/tools/pks/azure) | Sign in to Azure, pick a subscription, and review Cost Management spend and credit balance. |
| [pks foundry](/tools/pks/foundry) | Authenticate to Azure AI Foundry, select deployments, mint tokens, and run the local token proxy. |
| [pks google](/tools/pks/google) | Register and validate a Google AI Studio API key for image generation. |
| [pks ms-graph](/tools/pks/ms-graph) | Authenticate to Microsoft Graph through the device-code flow for mailbox access. |
| [pks authenticator](/tools/pks/authenticator) | Enrol and inspect the local time-based one-time password second factor. |
| [pks actions](/tools/pks/actions) | Choose which sensitive actions demand that second factor. |
| [pks cert](/tools/pks/cert) | Create, inspect, export, and remove pks-held code-signing certificates. |
| [pks sign](/tools/pks/sign) | Sign a Windows artifact unattended, on a workstation or inside a CI job container. |

### Source control, work tracking, and delivery targets

Where work comes from and where it ships.

| Group | What it does |
|---|---|
| [pks github](/tools/pks/github) | Authenticate to GitHub and run the devcontainer-backed self-hosted Actions runner. |
| [pks ado](/tools/pks/ado) | Authenticate to Azure DevOps and run the git credential proxy for containers. |
| [pks jira](/tools/pks/jira) | Browse Jira issue trees and export selected tickets to markdown and JSON. |
| [pks confluence](/tools/pks/confluence) | Sync Confluence pages to local markdown in a git-tracked workspace and push edits back. |
| [pks git](/tools/pks/git) | Answer Git's askpass prompts with a fresh Azure DevOps token. |
| [pks registry](/tools/pks/registry) | Store container-registry credentials for the job containers a runner spawns. |
| [pks coolify](/tools/pks/coolify) | Register Coolify instances so the runner can match repos to applications and inject deploy variables. |
| [pks tools](/tools/pks/tools) | Generate the tool-registry pages that publish commands to agentics.dk. |

### Storage, data, and observability

Moving bytes and reading back what happened.

| Group | What it does |
|---|---|
| [pks storage](/tools/pks/storage) | List, browse, and sync files against authenticated share providers, with a consent gate on writes. |
| [pks fileshare](/tools/pks/fileshare) | Authenticate a file-share provider and report its connection state. |
| [pks appinsights](/tools/pks/appinsights) | Choose the Application Insights resource that telemetry queries run against. |
| [pks otel](/tools/pks/otel) | Query exceptions, requests, logs, and dependency spans from that resource. |
| [pks email](/tools/pks/email) | Export Microsoft Graph mail to a dated tree of markdown files with attachments. |

### Content, media, and writing

Producing artifacts once the work is done.

| Group | What it does |
|---|---|
| [pks writing](/tools/pks/writing) | Danish-first terminology lint, rubric scoring, and a portable writer profile. |
| [pks persona](/tools/pks/persona) | Score content against reader-archetype personas on rubric-driven metrics. |
| [pks voice](/tools/pks/voice) | Push-to-talk dictation backed by Azure AI Foundry Speech. |
| [pks transcribe](/tools/pks/transcribe) | Transcribe an audio or video file with a cloud or an on-device engine. |
| [pks tts](/tools/pks/tts) | Generate speech from text or SSML, optionally rendering an audio-reactive video. |
| [pks image](/tools/pks/image) | Generate or edit an image through a Google AI or Azure AI Foundry model. |
| [pks promptwall](/tools/pks/promptwall) | Render a prompt from your Claude Code session history as a shareable card. |
| [pks model](/tools/pks/model) | Download, update, and remove the on-device models the voice commands use. |

## Defaults

| Setting | Value |
|---|---|
| Config root (Linux, macOS) | `$HOME/.pks-cli` |
| Config root (Windows) | `C:\Users\<user>\.pks-cli` |
| Global settings and most tokens | `~/.pks-cli/settings.json` |
| Per-project state | `<repo>/.pks/` |
| Console log level | `Warning` |
| Update channel | prompted on the first `pks update`, then stored |

> **Note.** Access and refresh tokens for GitHub, Azure, Azure DevOps, Foundry, Microsoft Graph, Scaleway, Tailscale, Google, and Jira are written to `settings.json` as plaintext. Only the SSH-key, certificate, and Agent Share stores are encrypted at rest.

No environment variable moves the config root. Individual features read their own variables — `AGENTICS_SERVER`, `ANTHROPIC_BASE_URL`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `PKS_DEBUG`, and others — each documented on the configuration page.

## Next steps

- [Quickstart: install pks and run your first agent](/tools/pks/quickstart) — the shortest path from an empty machine to a running agent session
- [Installing pks](/tools/pks/install) — every install route, prerequisite, and upgrade path in detail
- [Concepts](/tools/pks/concepts) — the operator model, the action guard, and how targets, containers, and runners relate
- [Configuration and credentials](/tools/pks/configuration) — the config root, every stored file, and the environment variables each feature reads
- [pks CLI reference](/tools/pks/cli-reference) — complete command, flag, and environment-variable reference
