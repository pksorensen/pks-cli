---
title: "The pks mental model"
description: "How pks is organized: seven command families, the operator-console mental model, where state and credentials live, and the conventions every group repeats."
tags: [concept, cli, architecture]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
---

`pks` is a single-operator console for AI coding agents that run somewhere other than your laptop. It is a .NET 10 command-line application built on Spectre.Console.Cli, shipped as a .NET global tool and as self-contained npm binaries, and its 57 command groups all serve one loop: give an agent a runtime, put that runtime on a machine, hand it credentials, hand it work, watch what it does, and collect what it produces.

## What kind of tool pks is

The name and the in-product banner ("Poul's Killer Swarms — The Next Agentic CLI for .NET Developers") describe where the tool started: a .NET project scaffolder. That is now the smallest part of it. The shipped surface spans cloud authentication, virtual-machine and microVM provisioning, devcontainer orchestration, GitHub and Azure DevOps work tracking, code signing, telemetry queries, and content generation.

Read it as connective tissue rather than as one application. Most groups do not implement a capability — they wire an existing capability (Docker, `ssh`, `rsync`, `git`, `claude`, `codex`, `osslsigncode`, `ffmpeg`, Azure ARM) into a shape an agent or an operator can drive without holding raw credentials. That framing predicts most of the surface: if a group's name is a product, the group is almost always auth plus a local registry of connection details, not a reimplementation of that product.

## The command-group architecture

Every capability lives under a **branch** — a named group like `pks vm` or `pks writing` — and every branch holds leaf commands. Branches nest at most two levels deep (`pks github runner start`, `pks brain skill list`). Some branches have a default command, so the bare group name does something: `pks claude` spawns a devcontainer and attaches Claude Code, and `pks agent "<prompt>"` runs the one-shot coding-agent loop.

A handful of leaf commands sit at the root instead of in a branch, and two of them are promoted aliases rather than concepts of their own: `pks schedule` belongs to the virtual-machine family, and `pks transcribe` belongs to the voice family. Treat them as shortcuts into [pks vm](/tools/pks/vm) and [pks voice](/tools/pks/voice).

Four flags are parsed against the raw command line before Spectre sees it, so they work at any position: `--debug` (sets `PKS_DEBUG=1` for the process), `--no-logo`, `--json`, and `--version`.

## The seven families

The 57 groups sort into seven families that follow the operator loop.

- **Core lifecycle and plumbing.** [init](/tools/pks/init), [exec](/tools/pks/exec), [report](/tools/pks/report), [update](/tools/pks/update), `status`, `deploy`. Scaffolding a project, launching third-party tools through a capability handshake, and managing pks itself.
- **Agents, agent runtimes, and MCP.** [claude](/tools/pks/claude), [agent](/tools/pks/agent), [agentics](/tools/pks/agentics), [codex](/tools/pks/codex), [mcp](/tools/pks/mcp), [hooks](/tools/pks/hooks), [brain](/tools/pks/brain), [marketplace](/tools/pks/marketplace), [share](/tools/pks/share), [vibecast](/tools/pks/vibecast), `prd`. This is the center of gravity — everything else exists to feed it.
- **Machines and remote environments.** [devcontainer](/tools/pks/devcontainer), [vm](/tools/pks/vm), [ssh](/tools/pks/ssh), [firecracker](/tools/pks/firecracker), [rsync](/tools/pks/rsync), [tailscale](/tools/pks/tailscale), [scaleway](/tools/pks/scaleway), [schedule](/tools/pks/schedule). Where the agent actually runs.
- **Cloud identity, secrets, and signing.** [azure](/tools/pks/azure), [foundry](/tools/pks/foundry), [google](/tools/pks/google), [ms-graph](/tools/pks/ms-graph), [authenticator](/tools/pks/authenticator), [actions](/tools/pks/actions), [cert](/tools/pks/cert), [sign](/tools/pks/sign). Foundry is the credential backbone for most model-backed features.
- **Source control, work tracking, and delivery.** [github](/tools/pks/github), [ado](/tools/pks/ado), [jira](/tools/pks/jira), [confluence](/tools/pks/confluence), [git](/tools/pks/git), [registry](/tools/pks/registry), [tools](/tools/pks/tools), [coolify](/tools/pks/coolify). Where work items come from and where output ships.
- **Storage, data, and observability.** [storage](/tools/pks/storage), [fileshare](/tools/pks/fileshare), [appinsights](/tools/pks/appinsights), [otel](/tools/pks/otel), [email](/tools/pks/email). Note that `otel` reads telemetry out of Application Insights — it is a query tool, not an exporter.
- **Content, media, and writing.** [writing](/tools/pks/writing), [persona](/tools/pks/persona), [voice](/tools/pks/voice), [transcribe](/tools/pks/transcribe), [tts](/tools/pks/tts), [image](/tools/pks/image), [promptwall](/tools/pks/promptwall), [model](/tools/pks/model). What the agent and the operator produce once the work is done.

## How pks relates to the wider Agentics platform

pks is the operator-side client for several services that run elsewhere. It does not host any of them.

**agentics.dk and the Assembly Line Platform.** The Assembly Line Platform (ALP) is the task board on agentics.dk. `pks agentics init` logs you in through Keycloak, `pks agentics runner start` turns your machine into a self-hosted runner that polls a project for jobs, and `pks agentics task submit` files a task onto an assembly-line stage from a continuous-integration pipeline. See [pks agentics](/tools/pks/agentics).

**Agent Share.** A hosted directory of coding sessions. `pks share init` performs the one-time OIDC (OpenID Connect) browser login for a host, and `pks agent register` then mints an agent inbox so other people can hand that session work.

**vibecast.** The broadcast terminal user interface for a live coding session. `pks vibecast` spawns or reattaches a devcontainer on a remote SSH target and drops you into a vibecast session inside it, so the machine doing the work is not the machine you are sitting at.

**The tool registry.** The page you are reading is generated infrastructure: `pks tools publish` reflects over commands tagged for export and writes their Markdown docs into a `tools-registry/` tree that agentics.dk renders.

**Devcontainers and runners.** Two runner families exist and they are not interchangeable. [pks github](/tools/pks/github) runs a self-hosted GitHub Actions runner that builds a devcontainer per queued workflow job; [pks agentics](/tools/pks/agentics) runs an ALP runner that executes assembly-line jobs; [pks firecracker](/tools/pks/firecracker) runs the same idea inside hardware-isolated microVMs when container isolation is not enough.

## Where state and credentials live

There is one configuration root on every operating system: `~/.pks-cli` (`%USERPROFILE%\.pks-cli` on Windows, not `%APPDATA%`). No environment variable relocates it. Inside it, each feature area owns its own file — `settings.json` for global key/value config, `ssh-targets.json`, `runners.json`, `registries.json`, `vms.json`, `models.json`, and so on — plus directories for encrypted stores (`certs/`, `ssh-keys/`, `share/`) and for accumulated data (`brain/`, `writing/`, `models/`).

Per-project state is separate and lives in a `.pks/` folder inside the repository: project identity, agent definitions, per-project writing overrides, and brain artifacts. The two are load-bearing in different ways — deleting `~/.pks-cli` costs you every credential, deleting `.pks/` costs you one project's derived state.

Secrets sit in two tiers. Code-signing certificates, pks-held SSH private keys, and Agent Share refresh tokens are AES-GCM encrypted at rest, with the key file stored beside the ciphertext in the same home directory. Everything else — OAuth tokens for GitHub, Azure, Azure DevOps, Foundry, Microsoft Graph, and the static API keys for Scaleway, Google, Tailscale, and Jira — is plaintext JSON in `settings.json`.

> **Note.** The encrypted tier resists casual reading and tampering, not a local attacker: the key and the ciphertext share one unprotected directory, with no passphrase and no operating-system keystore. Treat `~/.pks-cli` as sensitive as an unencrypted `~/.ssh`.

## The second factor, and why it exists

Some actions cost money or reach outside the machine. `pks actions` lets you mark those actions as requiring a time-based one-time password (TOTP) before they run — provisioning or destroying a virtual machine, spawning a remote devcontainer, connecting over SSH, writing cloud credentials, creating a signing certificate, and `pks update` itself. `pks authenticator init` enrolls the factor.

The design point is agent containment, not password hygiene. An agent running as your user can execute any pks subcommand; the gate is the choke-point where that stops being enough. Enrolling nothing leaves every gate open, which is why the enrollment command and the policy editor are themselves gated once a factor exists. See [pks actions](/tools/pks/actions) and [pks authenticator](/tools/pks/authenticator).

## Conventions that repeat across groups

Learning five patterns lets you predict how an unfamiliar group behaves.

- **`init` means one-time setup, and it is interactive.** Almost every group that talks to an external service has an `init` that prompts, authenticates, verifies with a live call, and persists. `--force` re-runs it; running it twice without `--force` is a no-op that prints what is already stored.
- **`status` is a local read.** It reports what was stored, sometimes with one connectivity probe. It rarely re-authenticates and never changes anything.
- **Registries, then consumers.** A group that manages targets — [ssh](/tools/pks/ssh), [rsync](/tools/pks/rsync), [registry](/tools/pks/registry), [coolify](/tools/pks/coolify) — only maintains a list. Another command consumes it: `pks claude backup` rsyncs to every registered rsync target, and the GitHub runner serves registered container-registry credentials to job containers over a local socket.
- **Auth is per-provider, not global.** There is no single login. `pks auth` is GitHub-only despite the generic name, and Azure, Foundry, Jira, Scaleway, and Agent Share each have their own command area and their own store.
- **Prompt, then accept.** The newer model-backed flows do not call a model themselves. [pks writing](/tools/pks/writing) and [pks persona](/tools/pks/persona) emit a JSON prompt bundle with a reply schema, your agent runs it through whatever model it has, and an `accept` command validates the reply and persists it. That keeps model choice out of pks and makes the flow work with any agent.

Two behaviors surprise people. The banner is suppressed by an allowlist, not by a rule: pipeable commands such as `pks mcp` over stdio, the git askpass shim, every `hooks` event, `claude limits`, and `ssh run` print clean output, while other commands decorate theirs — pass `--no-logo` when in doubt. And the structured-logging and telemetry services are in-memory only, with no network client anywhere; pks does not report usage home.

## Where it fits in your day

A typical loop touches four families in order. Provision or wake a box with [pks vm](/tools/pks/vm), spawn the project's devcontainer on it with [pks devcontainer](/tools/pks/devcontainer) or [pks claude](/tools/pks/claude), let the agent work against tasks that arrived from an assembly line or an issue tracker, and then inspect the result — response-time and cost analytics from `pks claude stats` and `pks claude usage`, production traces from [pks otel](/tools/pks/otel), and accumulated session knowledge from [pks brain](/tools/pks/brain).

The commands you run most are rarely the ones you set up first. Setup is a handful of `init` calls that you do once per machine; the daily surface is three or four commands from two families.

## See also

- [pks](/tools/pks) — the tool's landing page, install paths, and the full command index
- [pks claude](/tools/pks/claude) — the Claude Code launcher, alternate model backends, and local usage analytics
- [pks agentics](/tools/pks/agentics) — login, self-hosted runners, and Assembly Line task submission
- [pks devcontainer](/tools/pks/devcontainer) — authoring, spawning, and destroying devcontainer environments
- [pks actions](/tools/pks/actions) — choosing which actions require a second factor
