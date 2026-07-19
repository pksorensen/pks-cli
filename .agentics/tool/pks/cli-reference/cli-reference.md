---
title: "pks CLI reference"
description: "Every command in the pks CLI in one index — 57 groups spanning agents, machines, cloud identity, delivery, storage, observability, and media."
tags: [reference, cli, index]
category: infrastructure
platform: [linux, macos, windows]
status: stable
author: Poul Kjeldager
component: pks
usage: "pks <command> [options]"
examples:
  - command: "pks claude"
    description: "Spawn a devcontainer and attach a Claude Code session"
  - command: "pks agentics runner start"
    description: "Run the self-hosted Assembly Line runner daemon"
  - command: "pks vm list"
    description: "List every tracked Azure and Scaleway VM"
  - command: "pks writing lint post.md"
    description: "Run the Danish terminology pass over a post"
  - command: "pks update"
    description: "Update the CLI on its detected install channel"
---

`pks` is a .NET 10 command-line application — an operator's console for AI coding agents that run somewhere other than your laptop. This page indexes every command group and every command path in one place; each group's own page carries the flags, defaults, and behavior detail.

The command surface is organized here into seven families that follow the operator loop: scaffold a project, give an agent a runtime, put that runtime on a machine, give it credentials, give it work items and somewhere to ship, observe it, and let it produce artifacts.

## Synopsis

```text
pks <command> [options]
```

Command families, in the order they appear below:

```text
core           Project scaffolding and CLI self-management
agents         AI agents, agent runtimes, and MCP
machines       Machines, microVMs, and remote dev environments
identity       Cloud identity, secrets, and signing
delivery       Source control, work tracking, and delivery targets
data           Storage, data, and observability
media          Content, media, and writing
```

### Global options

These four flags are parsed against the raw command line before argument binding, so they work at any position.

| Flag | Description |
| --- | --- |
| `--debug` | Sets `PKS_DEBUG=1` for the process, enabling verbose diagnostics in the commands that read it. |
| `--no-logo` | Suppresses the ASCII banner. |
| `--json` | Requests machine-readable output. Support varies per command; it always suppresses the banner for `pks hooks`. |
| `-j` | Short form of `--json`. |
| `--version` | Prints the CLI version from the assembly informational version. |

`--verbose` is not global — it is a per-command option on the commands that declare it.

Banner suppression is an allowlist, not a blanket rule. The banner is suppressed automatically for the pipeable surfaces: `pks mcp` on the stdio transport, `pks git askpass`, every `pks hooks` event, `pks foundry proxy`, `pks ado git-proxy`, `pks claude limits`, `pks claude session-usage`, several `pks writing` and `pks persona` subcommands, and `pks ssh run` / `pks ssh copy`.

### Configuration root

One hardcoded root on every operating system: `$HOME/.pks-cli` on Linux and macOS, `C:\Users\<user>\.pks-cli` on Windows. There is no environment-variable override for it. Per-project state lives in a `.pks/` directory inside the repository. See the individual group pages for which files each command writes.

## Core lifecycle and CLI plumbing

Project scaffolding, tool execution, and management of the CLI itself.

### pks init

Scaffolds a new project from a NuGet-published devcontainer template. Detail: [pks init](/tools/pks/init).

| Command | Purpose |
| --- | --- |
| `pks init [PROJECT_NAME]` | Discovers a devcontainer template package, extracts it into a new project directory, and optionally spawns the resulting devcontainer locally or over SSH. |

### pks exec

Universal launcher for third-party tools that implement the `PKS_DISCOVERY=1` contract. Detail: [pks exec](/tools/pks/exec).

| Command | Purpose |
| --- | --- |
| `pks exec` | Reads a tool's capability manifest, prompts for an LLM provider and per-role models, resolves the manifest's environment templates, then re-executes the tool with those variables injected. |

### pks update

Self-update, aware of how the running binary was installed. Detail: [pks update](/tools/pks/update).

| Command | Purpose |
| --- | --- |
| `pks update` | Picks or reuses an update channel, checks NuGet for a newer `pks-cli`, and applies the update through the matching install mechanism. Gated by the two-factor action guard. |

### pks report

Turns feedback into a maintainer-ready GitHub issue. Detail: [pks report](/tools/pks/report).

| Command | Purpose |
| --- | --- |
| `pks report` | Assembles a message, version info, environment diagnostics, and local telemetry stats into a GitHub issue, or previews it with a dry run. |

### pks deploy

Detail: [pks deploy](/tools/pks/deploy).

| Command | Purpose |
| --- | --- |
| `pks deploy` | Renders a scripted, fully simulated deployment sequence. It builds nothing, pushes nothing, and contacts no cluster — a rendering demo, not a deployment tool. |

### pks status

Detail: [pks status](/tools/pks/status).

| Command | Purpose |
| --- | --- |
| `pks status` | Prints a mocked environment-health grid and services table. Every figure is hardcoded or generated; it reflects no real infrastructure. |

> **Note.** `pks deploy` and `pks status` are visual scaffolds retained from the CLI's early days. Use the group pages before relying on either in a workflow.

## AI agents, agent runtimes, and MCP

Launching coding agents, pointing them at alternate model backends, and wiring them into hosts and marketplaces.

### pks claude

Claude Code launcher, alternate-backend proxies, and local session analytics. Detail: [pks claude](/tools/pks/claude).

| Command | Purpose |
| --- | --- |
| `pks claude` | Spawns a devcontainer locally or on a registered SSH target and attaches an interactive Claude Code session, or runs Claude inline in the current shell. |
| `pks claude stats` | Renders an activity heatmap, streaks, token totals, and response-time trends from local session transcripts. |
| `pks claude usage` | Prices deduplicated billed requests from session transcripts and charts hourly and daily cost with a top-models breakdown. |
| `pks claude limits` | Reports session and week usage-limit percentages, reset times, and pace as structured data. |
| `pks claude session-usage` | Alias of `pks claude limits` — same command type, same options. |
| `pks claude codex` | Runs Claude Code against an Azure AI Foundry Codex deployment through a local Anthropic-to-Responses translating proxy. |
| `pks claude scaleway` | Runs Claude Code against any model in the Scaleway serverless catalog through a local translating proxy. |
| `pks claude mistral` | The Scaleway proxy narrowed to the Mistral and Devstral model family. |
| `pks claude qwen` | The Scaleway proxy narrowed to the Qwen model family. |
| `pks claude anthropic` | Launches Claude Code directly against a first-party Anthropic model, with no proxy and no translation. |
| `pks claude backup` | Rsyncs the whole `~/.claude/` directory to every registered rsync target and reports per-target results. |
| `pks claude managed-settings` | Renders a Claude Code `managed-settings.json` from the registered plugin marketplaces. |

### pks agent

Provider-neutral one-shot coding agent, plus session enrollment into Agent Share. Detail: [pks agent](/tools/pks/agent).

| Command | Purpose |
| --- | --- |
| `pks agent` | Default command: runs a one-shot tool-use coding agent on the given prompt, backed by Azure OpenAI or Anthropic. Argument rewriting makes it equivalent to `pks agent run`. |
| `pks agent register` | Enrolls the current session as a shareable agent against a configured Agent Share provider, minting a per-user agent inbox. |

### pks agentics

Login, the self-hosted runner daemon, and Assembly Line task submission. Detail: [pks agentics](/tools/pks/agentics).

| Command | Purpose |
| --- | --- |
| `pks agentics init` | One-time device-code login to agentics.dk, persisting tokens for later runner and task commands. |
| `pks agentics runner register` | Registers this machine as a named runner for an owner and project, and verifies GitHub access to the project repository. |
| `pks agentics runner start` | Runs the runner daemon, polling for jobs and dispatching git, chat, and devcontainer-session work until interrupted. |
| `pks agentics runner status` | Shows the remote tmux session state for a project handed off to an SSH target, with the last lines of output. |
| `pks agentics runner logs` | Prints the full remote tmux pane output for a project handed off to an SSH target. |
| `pks agentics runner stop` | Kills the remote tmux session running a handed-off runner. |
| `pks agentics runner cleanup` | Removes Docker containers orphaned by a previous runner-process instance. |
| `pks agentics runner claude-login` | Opens an interactive Claude Code login on an SSH target to populate its credentials volume for headless spawns. |
| `pks agentics task submit` | Files a task onto an Assembly Line stage, auto-enriching the description with failure metadata when run inside GitHub Actions. |

### pks codex

Runs the upstream Codex CLI against Azure AI Foundry with no request translation. Detail: [pks codex](/tools/pks/codex).

| Command | Purpose |
| --- | --- |
| `pks codex` | Default command: launches the real Codex CLI through the loopback Foundry passthrough. |
| `pks codex run` | Explicit form of the default command, unambiguous when mixing pks flags with native Codex arguments. |
| `pks codex resume` | Runs Codex's own session resume through the passthrough. |
| `pks codex exec` | Runs Codex's non-interactive execution mode through the passthrough. |
| `pks codex fork` | Runs Codex's branch-a-session feature through the passthrough. |
| `pks codex archive` | Archives a Codex session. |
| `pks codex unarchive` | Restores a previously archived Codex session. |
| `pks codex delete` | Permanently deletes a Codex session. |
| `pks codex init` | Preflights a Foundry deployment, writes the managed provider block into the Codex config, and saves pks-side defaults. |

### pks mcp

Detail: [pks mcp](/tools/pks/mcp).

| Command | Purpose |
| --- | --- |
| `pks mcp` | Runs pks itself as a Model Context Protocol server over stdio or HTTP, exposing its own capabilities to an AI host as typed tools. |

### pks hooks

Claude Code lifecycle hook handlers and their registration. Detail: [pks hooks](/tools/pks/hooks).

| Command | Purpose |
| --- | --- |
| `pks hooks` | Interactive menu for configuring the lint command the Stop hook runs, registering the Stop hook if needed. |
| `pks hooks init` | Merges the pks hook commands into a Claude Code `settings.json`, migrating legacy key casing. |
| `pks hooks list` | Prints the seven hook events pks supplies handlers for. |
| `pks hooks stop` | Stop-event handler: runs the configured lint command and blocks the agent from finishing when it fails. |
| `pks hooks pre-tool-use` | PreToolUse handler. Reads the payload and always proceeds. |
| `pks hooks post-tool-use` | PostToolUse handler. Reads the payload and always proceeds. |
| `pks hooks user-prompt-submit` | UserPromptSubmit handler. Reads the prompt and always proceeds. |
| `pks hooks notification` | Notification handler. Dumps environment, arguments, and stdin for debugging. |
| `pks hooks subagent-stop` | SubagentStop handler. Dumps environment, arguments, and stdin for debugging. |
| `pks hooks pre-compact` | PreCompact handler. Dumps environment, arguments, and stdin for debugging. |

### pks prd

Product-requirements-document scaffolding and inspection. Detail: [pks prd](/tools/pks/prd).

| Command | Purpose |
| --- | --- |
| `pks prd generate` | Scaffolds a PRD skeleton from a one-line idea using keyword matching, not a language model. |
| `pks prd template` | Writes a static Markdown PRD skeleton for one of seven project archetypes. |
| `pks prd load` | Parses an existing PRD file and prints a summary panel. |
| `pks prd status` | Displays completion statistics for one PRD, or scans a tree for PRD-like files. |
| `pks prd validate` | Runs structural completeness checks and exits non-zero on errors, so it works as a CI gate. |
| `pks prd requirements` | Lists and exports requirements. Returns placeholder rows today because the Markdown load path does not populate them. |

### pks brain

A personal knowledge base distilled from Claude Code session history. Detail: [pks brain](/tools/pks/brain).

| Command | Purpose |
| --- | --- |
| `pks brain init` | Creates the global and per-project brain directories and adds the project one to `.gitignore`. |
| `pks brain ingest` | Deterministic pass over every session transcript into four append-only firehose files. |
| `pks brain extract` | Summarizes each eligible session into a per-session Markdown extract with a cost sidecar. |
| `pks brain synth` | Clusters extracts by theme and writes cross-session narratives plus a machine-readable cluster file. |
| `pks brain wiki` | Renders one wiki page per qualifying cluster, plus an index. |
| `pks brain adr` | Distils architectural clusters into standard ADRs, plus an index. |
| `pks brain refresh` | Runs ingest, extract, synth, wiki, and adr in sequence behind a single cost estimate and confirmation. |
| `pks brain status` | Shows what the brain knows and which extracts are missing, stale, or built from an outdated prompt. |
| `pks brain search` | Full-text or regex search across the ingested firehoses and the project's extracts. |
| `pks brain conversation` | Exports one session's human-readable conversation without calling a model. |
| `pks brain commit-plan` | Groups uncommitted files by the session that produced them, so a large diff splits into coherent commits. |
| `pks brain scan filepath` | Scans raw transcripts for every tool call that touched a given file or directory. |
| `pks brain skill list` | Lists the five brain prompts and whether each resolves from the embedded default or a local override. |
| `pks brain skill init` | Copies a brain prompt out to an editable file so the corresponding phase uses the customized version. |
| `pks brain skill show` | Prints the currently resolved body of a named brain prompt. |

### pks marketplace

Claude Code plugin marketplaces registered locally. Detail: [pks marketplace](/tools/pks/marketplace).

| Command | Purpose |
| --- | --- |
| `pks marketplace add` | Fetches a marketplace document from a URL or GitHub shorthand, applies any policy document, and registers it locally. |
| `pks marketplace list` | Prints every registered marketplace with its source and enabled-plugin count. |
| `pks marketplace show` | Prints one marketplace's detail and its per-plugin table. |
| `pks marketplace enable` | Enables some or all plugins in a registered marketplace. |
| `pks marketplace disable` | Disables some or all plugins in a registered marketplace. |
| `pks marketplace refresh` | Re-fetches each marketplace's source, preserving the enabled state of plugins that still exist. |
| `pks marketplace remove` | Deletes a marketplace entry and its plugin state from local storage. |

### pks share

Detail: [pks share](/tools/pks/share).

| Command | Purpose |
| --- | --- |
| `pks share init` | Logs this host into an Agent Share server over an OIDC loopback flow and stores the encrypted refresh token. |

### pks vibecast

Remote devcontainer spawn plus an interactive broadcast session. Detail: [pks vibecast](/tools/pks/vibecast).

| Command | Purpose |
| --- | --- |
| `pks vibecast` | Spawns or reattaches to a devcontainer on a registered SSH target and drops into an interactive vibecast session inside it. |
| `pks vibecast game` | The same remote spawn flow in tournament mode, with a per-player container, a game identifier, and a seeded briefing. |

## Machines, microVMs, and remote dev environments

Where agents actually run: containers, virtual machines, microVMs, and the transports that reach them.

### pks devcontainer

Devcontainer authoring and Docker-volume-backed lifecycle. Detail: [pks devcontainer](/tools/pks/devcontainer).

| Command | Purpose |
| --- | --- |
| `pks devcontainer init` | Generates a `.devcontainer/devcontainer.json` from flags, resolving feature dependencies and conflicts first. |
| `pks devcontainer wizard` | Interactive builder covering template, features, environment variables, extensions, and advanced settings. |
| `pks devcontainer validate` | Lints an existing configuration across structure, features, extensions, ports, base image, and file paths. |
| `pks devcontainer spawn` | Runs a project's devcontainer in a named Docker volume locally, on an SSH target, or on a freshly provisioned VM, then opens it in the editor. |
| `pks devcontainer list` | Lists managed devcontainers with status, volumes, creation time, and image, locally or on an SSH target. |
| `pks devcontainer connect` | Opens an already-spawned devcontainer in the editor by constructing a remote URI. |
| `pks devcontainer destroy` | Removes a managed container, its volumes, and any staged remote project copy. |

### pks vm

Cloud VMs used as remote devcontainer hosts. Detail: [pks vm](/tools/pks/vm).

| Command | Purpose |
| --- | --- |
| `pks vm init` | Interactive wizard that provisions an Azure or Scaleway VM, generates a key, and registers it as an SSH target. |
| `pks vm list` | Lists every tracked VM with live power status, disk usage, address, and shutdown settings. |
| `pks vm status` | Shows one VM's detail and live stats, then offers an inline action menu. |
| `pks vm start` | Starts a VM, waits for SSH, refreshes its SSH target, and prints connection details. |
| `pks vm stop` | Deallocates a VM while preserving its disks. |
| `pks vm destroy` | Permanently deletes a VM and its associated cloud resources, and removes the local records. |
| `pks vm autoshutdown` | Configures or disables idle-based and fixed-time daily shutdown for one Azure VM. |
| `pks vm tailscale` | Starts a VM if needed and joins it to the tailnet using the stored Tailscale configuration. |
| `pks vm add-ssh-key` | Registers a private key for a VM pks did not provision, so the SSH commands can reach it. |
| `pks vm export-ssh-key` | Prints a heredoc that installs a known key on another machine and connects immediately. |

### pks schedule

A VM command promoted to the top level, not a `pks vm` subcommand. Detail: [pks schedule](/tools/pks/schedule).

| Command | Purpose |
| --- | --- |
| `pks schedule` | Interactive configurator for a tracked VM's daily auto-start, daily auto-shutdown, and idle-shutdown threshold, applied in one batch. |

### pks firecracker

MicroVM job runners for isolated execution. Detail: [pks firecracker](/tools/pks/firecracker).

| Command | Purpose |
| --- | --- |
| `pks firecracker init` | Host bootstrap: verifies KVM and the binary, generates a keypair, downloads the kernel, and builds the root filesystem image. |
| `pks firecracker test` | Boots a throwaway microVM end to end and prints a pass or fail table for six smoke checks. |
| `pks firecracker runner start` | Runs the microVM runner daemon, executing each claimed job inside a fresh microVM and tearing it down afterwards. |

### pks ssh

Registered SSH targets, gated connections, and a pks-held key vault. Detail: [pks ssh](/tools/pks/ssh).

| Command | Purpose |
| --- | --- |
| `pks ssh register` | Adds a host to the local target registry and optionally tests connectivity. |
| `pks ssh list` | Prints every registered SSH target. |
| `pks ssh remove` | Deletes a registered target without touching the remote host. |
| `pks ssh connect` | Opens an interactive session to a target, auto-starting a tracked Azure VM when it is stopped. |
| `pks ssh run` | Runs a single non-interactive command on a target with streams forwarded untouched, so pipelines work. |
| `pks ssh copy` | Copies a file or directory to or from a target, addressing the remote side by target name. |
| `pks ssh key import` | Imports a private key into the encrypted pks key store, optionally registering a target bound to it. |
| `pks ssh key list` | Lists pks-held keys with fingerprints and import timestamps. |
| `pks ssh key remove` | Permanently deletes a pks-held key and its index entry. |

### pks rsync

Detail: [pks rsync](/tools/pks/rsync).

| Command | Purpose |
| --- | --- |
| `pks rsync init` | Registers a backup target interactively and test-connects to it. |
| `pks rsync list` | Prints every registered rsync target. |
| `pks rsync remove` | Removes a registered target after confirmation. |

### pks tailscale

Detail: [pks tailscale](/tools/pks/tailscale).

| Command | Purpose |
| --- | --- |
| `pks tailscale init` | Stores an auth key and join preferences that `pks vm tailscale` later uses to join a VM to the tailnet. |

### pks scaleway

Detail: [pks scaleway](/tools/pks/scaleway).

| Command | Purpose |
| --- | --- |
| `pks scaleway init` | Stores a Scaleway API key pair, resolves the organization and project, and selects a default zone. |

## Cloud identity, secrets, and signing

Credentials the agents borrow, and the two-factor gate that stands in front of the sensitive actions.

### pks azure

Detail: [pks azure](/tools/pks/azure).

| Command | Purpose |
| --- | --- |
| `pks azure init` | Runs the browser login flow, discovers the tenant, selects a subscription, and stores credentials. |
| `pks azure usage` | Shows Cost Management spend and Microsoft Customer Agreement credit balances over a chosen window. |

### pks foundry

Azure AI Foundry authentication and model selection — the credential backbone for most model-backed commands. Detail: [pks foundry](/tools/pks/foundry).

| Command | Purpose |
| --- | --- |
| `pks foundry init` | First-time sign-in, then a guided walk through subscription, Foundry resource, and model deployments. |
| `pks foundry select` | Re-runs resource and model selection using the stored refresh token, without a browser. |
| `pks foundry token` | Prints a bearer access token for the selected resource, for scripting. |
| `pks foundry status` | Prints the stored credentials and selection without making a network call. |
| `pks foundry proxy` | Runs a local reverse proxy that swaps a throwaway token for a fresh Azure token on every request. |
| `pks foundry usage` | Cost breakdown scoped to one Foundry resource, with a chart and a per-meter table. |

### pks google

Detail: [pks google](/tools/pks/google).

| Command | Purpose |
| --- | --- |
| `pks google init` | Registers and validates a Google AI Studio API key used by the image commands. |
| `pks google status` | Shows whether a key is registered, masked, with its registration timestamp. |

### pks ms-graph

Detail: [pks ms-graph](/tools/pks/ms-graph).

| Command | Purpose |
| --- | --- |
| `pks ms-graph register` | Configures and authenticates a Microsoft Graph app registration over the device-code flow, the prerequisite for email export. |

### pks authenticator

Detail: [pks authenticator](/tools/pks/authenticator).

| Command | Purpose |
| --- | --- |
| `pks authenticator init` | Enrolls a TOTP secret, printing the setup URI and recovery codes once, and persists only after a live code verifies. |
| `pks authenticator status` | Reports whether a second factor is enrolled and how many recovery codes remain. |

### pks actions

Detail: [pks actions](/tools/pks/actions).

| Command | Purpose |
| --- | --- |
| `pks actions` | Interactive editor for which actions require a second factor. Saving is itself gated, so the policy cannot be widened silently. |

### pks cert

Detail: [pks cert](/tools/pks/cert).

| Command | Purpose |
| --- | --- |
| `pks cert init` | Creates a self-signed code-signing certificate, stores it encrypted, and offers to export the public certificate. |
| `pks cert list` | Lists every pks-held certificate with subject, thumbprint, and expiry. |
| `pks cert show` | Shows one certificate's detail and prints its public PEM. |
| `pks cert export` | Writes the public trust certificate to a file for distribution. |
| `pks cert remove` | Permanently deletes a stored certificate and its encrypted blob. |

### pks sign

Detail: [pks sign](/tools/pks/sign).

| Command | Purpose |
| --- | --- |
| `pks sign` | Signs a Windows artifact with a pks-held certificate, resolving the key from the local store or, inside a job container, from the runner credential socket. |

## Source control, work tracking, and delivery targets

Where the work comes from and where the output goes.

### pks github

GitHub authentication and the devcontainer-based self-hosted runner. Detail: [pks github](/tools/pks/github).

| Command | Purpose |
| --- | --- |
| `pks github init` | Authenticates with GitHub and walks through installing the app on a repository. |
| `pks github status` | Reports whether a valid token is held, with token type, scopes, and expiry under verbose. |
| `pks github runner register` | Registers a repository for the runner to poll, verifying app access and admin permission first. |
| `pks github runner unregister` | Removes a repository's runner registration. |
| `pks github runner list` | Lists persisted runner registrations. |
| `pks github runner start` | Runs the runner daemon in the foreground, building a devcontainer per queued workflow job. |
| `pks github runner status` | Shows the daemon summary and any active jobs. |
| `pks github runner stop` | Requests a graceful shutdown once active jobs finish. |
| `pks github runner prune` | Removes duplicate registrations, keeping the most recent one per repository. |

### pks ado

Detail: [pks ado](/tools/pks/ado).

| Command | Purpose |
| --- | --- |
| `pks ado init` | Runs the Azure DevOps login flow, or registers a repository into the git-proxy allowlist. |
| `pks ado status` | Prints the stored user, organization, and token timestamps without a network call. |
| `pks ado git-proxy` | Runs a git smart-HTTP reverse proxy that injects a fresh token, so a container never holds one. |

### pks jira

Detail: [pks jira](/tools/pks/jira).

| Command | Purpose |
| --- | --- |
| `pks jira init` | Authenticates against Jira Cloud or Server and stores the credentials machine-wide. |
| `pks jira browse` | Interactive issue tree browser with multi-select and export to Markdown, JSON, and attachments. |
| `pks jira config` | Views or sets the custom-field mapping used to extract acceptance criteria during export. |

### pks confluence

Detail: [pks confluence](/tools/pks/confluence).

| Command | Purpose |
| --- | --- |
| `pks confluence init` | Bootstraps a local Confluence workspace scoped to a space and root page, with its own git repository. |
| `pks confluence checkout` | Pulls the page tree, or a single page, down to Markdown with frontmatter and comment sidecars. |
| `pks confluence commit` | Pushes selected staged edits, creations, and deletions back to Confluence, then resyncs. |
| `pks confluence delete` | Stages a page for deletion by writing a marker file; the deletion happens on the next commit. |

### pks git

Detail: [pks git](/tools/pks/git).

| Command | Purpose |
| --- | --- |
| `pks git askpass` | Git credential helper for Azure DevOps, answering username and password prompts with a freshly minted token. `--install` wires it into the shell. |

### pks registry

Container-registry credentials served to job containers. Detail: [pks registry](/tools/pks/registry).

| Command | Purpose |
| --- | --- |
| `pks registry init` | Registers a registry hostname, username, and password, verifying them with a real login first. |
| `pks registry status` | Lists registered registries, or shows one by hostname. Reads local state only. |
| `pks registry remove` | Deletes a registry's stored credentials without confirmation. |

### pks tools

Detail: [pks tools](/tools/pks/tools).

| Command | Purpose |
| --- | --- |
| `pks tools publish` | Generates tool-registry Markdown pages for every command class tagged for export, writing them into a discovered registry directory. |

### pks coolify

Detail: [pks coolify](/tools/pks/coolify).

| Command | Purpose |
| --- | --- |
| `pks coolify register` | Registers a Coolify instance and token, verifying it against the instance API. |
| `pks coolify list` | Lists registered instances from local state. |
| `pks coolify status` | Tests connectivity and renders each project's applications, services, and databases with status. |

## Storage, data, and observability

### pks storage

Provider-agnostic file operations. Detail: [pks storage](/tools/pks/storage).

| Command | Purpose |
| --- | --- |
| `pks storage list` | Enumerates every account and share visible across authenticated providers. Read-only. |
| `pks storage ls` | Lists files and directories inside a share path, with a machine-readable mode. Read-only. |
| `pks storage sync` | Downloads, uploads, or bidirectionally syncs a share against a local directory. Write paths require an interactive confirmation. |

### pks fileshare

Detail: [pks fileshare](/tools/pks/fileshare).

| Command | Purpose |
| --- | --- |
| `pks fileshare init` | Authenticates a file share provider and stores the tenant, subscription, storage account, and refresh token. |
| `pks fileshare status` | Renders authentication and share-count status for every registered provider. |

### pks appinsights

Detail: [pks appinsights](/tools/pks/appinsights).

| Command | Purpose |
| --- | --- |
| `pks appinsights init` | Discovers and stores the Application Insights resource that the telemetry queries read from. |
| `pks appinsights status` | Shows the configured resource and tests connectivity with a live query. |

### pks otel

Read-only telemetry queries against Application Insights. Detail: [pks otel](/tools/pks/otel).

| Command | Purpose |
| --- | --- |
| `pks otel errors` | Lists recent exceptions, newest first, narrowable by app, window, or operation. |
| `pks otel traces` | Lists recent requests with duration, success, and result code. |
| `pks otel logs` | Lists structured log entries by minimum severity or trace identifier. |
| `pks otel spans` | Lists the outbound dependency waterfall for one operation. |

### pks email

Detail: [pks email](/tools/pks/email).

| Command | Purpose |
| --- | --- |
| `pks email export` | Exports Microsoft Graph messages to a dated Markdown tree with frontmatter and downloaded attachments. |

## Content, media, and writing

### pks writing

Danish-first linting, agent-driven scoring, and a portable writer profile. Detail: [pks writing](/tools/pks/writing).

| Command | Purpose |
| --- | --- |
| `pks writing init` | Creates the global and per-project writing directories and seeds a profile template. |
| `pks writing lint` | Deterministic terminology pass that writes a report sidecar per file and always exits zero. |
| `pks writing prompt` | Emits a scoring prompt bundle with schema for an agent to run through its own model. |
| `pks writing accept` | Validates the model's reply against the schema and merges it into the report sidecar. |
| `pks writing score` | Superseded one-shot scoring that spawns a local Claude process itself. |
| `pks writing skill install` | Installs the bundled scoring skill so a skill-aware agent discovers the workflow. |
| `pks writing learn` | Turns report findings into a reviewable proposal of profile edits. |
| `pks writing apply` | Applies the accepted actions in a proposal to the global profile. |
| `pks writing corpus` | Aggregates per-post proposals into a corpus proposal, keeping only recurring terms. |
| `pks writing profile show` | Prints the resolved profile with counts and paths. |
| `pks writing profile author` | Interactive entry point for building the profile, by prompt or by editor. |
| `pks writing profile prompt` | Prints the authoring prompt to plain output for piping. |
| `pks writing profile ingest` | Ingests an authoring reply bundle into the profile store. |
| `pks writing profile export` | Packs the profile into a relocatable archive. |
| `pks writing profile import` | Restores a profile from an exported archive. |
| `pks writing naturalness prompt` | Emits the sentence-level naturalness critique bundle for an agent's own model. |
| `pks writing naturalness accept` | Validates a critic's reply, stores it per critic, and re-merges the canonical sidecar. |
| `pks writing naturalness review` | Interactive pick-one-of-three loop over every flagged sentence, saved incrementally. |
| `pks writing naturalness apply` | Applies the picked rewrites in place, archives the previous version, and records the patterns. |
| `pks writing naturalness merge` | Rebuilds the canonical candidate sidecar from the per-critic files. |
| `pks writing naturalness patterns show` | Renders the accumulated naturalness patterns as a table. |
| `pks writing naturalness patterns export` | Dumps the patterns store as Markdown to a file or to standard output. |

### pks persona

Reader-archetype scoring of content against rubrics. Detail: [pks persona](/tools/pks/persona).

| Command | Purpose |
| --- | --- |
| `pks persona list` | Lists the personas defined for a locale with id, name, segment, and bucket. |
| `pks persona show` | Prints one persona's Markdown source verbatim. |
| `pks persona lint` | Validates persona files for frontmatter, required sections, structure, and referenced assets. |
| `pks persona prompt` | Emits the persona and rubric scoring bundle without calling a model. |
| `pks persona accept` | Validates an externally produced reply and persists it into the scores sidecar. |
| `pks persona score` | Builds the prompt, calls a model in process, validates, and persists in one step. |
| `pks persona score-all` | Scores one file across the persona and rubric matrix, with an optional cheap pre-screen pass. |

### pks voice

Push-to-talk dictation via the heypoul companion binary. Detail: [pks voice](/tools/pks/voice).

| Command | Purpose |
| --- | --- |
| `pks voice start` | Starts the dictation daemon, or runs it inline, after resolving engine, microphone, and credentials. |
| `pks voice off` | Stops the running daemon using its recorded process id. |
| `pks voice show` | Browses dictation history and re-injects or copies a chosen entry. |
| `pks voice settings` | Launches the companion's native settings window. Windows only. |

### pks transcribe

A top-level sibling of the voice group, not a subcommand of it. Detail: [pks transcribe](/tools/pks/transcribe).

| Command | Purpose |
| --- | --- |
| `pks transcribe` | Transcribes an audio or video file using the cloud engine, a local on-device engine, or both at once for comparison. |

### pks tts

Detail: [pks tts](/tools/pks/tts).

| Command | Purpose |
| --- | --- |
| `pks tts` | Synthesizes speech to an MP3 through a Foundry deployment, or through neural-voice markup when a markup file is supplied, and optionally renders an audio-reactive video. |

### pks image

Detail: [pks image](/tools/pks/image).

| Command | Purpose |
| --- | --- |
| `pks image` | Generates an image from a prompt, or edits an existing one, resolving the provider from the model name. Also lists servable models. |

### pks promptwall

Detail: [pks promptwall](/tools/pks/promptwall).

| Command | Purpose |
| --- | --- |
| `pks promptwall` | Renders a chosen prompt, and optionally the reply, from a local session transcript as a branded square social image. |

### pks model

Local on-device models. Detail: [pks model](/tools/pks/model).

The per-model branches are **generated at startup from the model catalog** (`ModelCatalog.Known`): every catalog entry gets its own `pks model <name>` branch with the same four verbs, with no per-model code. The catalog currently holds exactly one entry, `parakeet-v3`, so the concrete rows below are today's complete surface — expect this list to grow one four-row block at a time as models are added.

| Command | Purpose |
| --- | --- |
| `pks model list` | Lists catalog models cross-referenced against what is installed locally. |
| `pks model <name> init` | Downloads, extracts, and registers a model through a staging directory. |
| `pks model <name> status` | Shows install detail for one model and flags an available update. |
| `pks model <name> update` | Removes and re-installs a model when the catalog version differs. |
| `pks model <name> remove` | Uninstalls a model and frees its disk space. |
| `pks model parakeet-v3 init` | Downloads and installs Parakeet TDT 0.6B v3 (multilingual, ~640 MiB). |
| `pks model parakeet-v3 status` | Shows install path, version, and size for Parakeet, and flags an available update. |
| `pks model parakeet-v3 update` | Re-installs Parakeet when the catalog version differs from the installed one. |
| `pks model parakeet-v3 remove` | Uninstalls Parakeet and frees its disk space. |

## Internal and demo commands

These three are registered on the root command but are not intended for general use. They exist as rendering demos and internal test harnesses, and they have no documentation pages of their own.

| Command | Registered description | Purpose |
| --- | --- | --- |
| `pks ascii` | Generate beautiful ASCII art for your projects | Cosmetic ASCII-art renderer for arbitrary text; accepts `--style`, `--color`, `--gradient`, and `--animate`. |
| `pks logging-demo` | Demonstrate the comprehensive logging system capabilities | Exercises the logging subsystem; accepts `--verbose`, `--interactive`, `--simulate-error`, `--delay`, and `--user`. |
| `pks test-swarm` | Test swarm MCP tools functionality | Stubbed smoke check for the swarm MCP tooling: it prints a hardcoded placeholder tool list and, with `--execute`, a simulated `swarm_init` result. It does not call the MCP tool service, so it verifies nothing about the real tools. |

## See also

- [pks](/tools/pks) — the tool overview, install paths, and where each family fits
- [pks claude](/tools/pks/claude) — the Claude Code launcher, alternate backends, and local usage analytics
- [pks agentics](/tools/pks/agentics) — login, the self-hosted runner daemon, and Assembly Line task submission
- [pks devcontainer](/tools/pks/devcontainer) — devcontainer authoring and the spawn lifecycle
- [pks foundry](/tools/pks/foundry) — the Azure AI Foundry credential backbone most model-backed commands depend on
- [pks update](/tools/pks/update) — channels and the install-method-aware self-update
