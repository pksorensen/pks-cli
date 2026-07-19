---
title: "pks codex"
description: "Run the real OpenAI Codex CLI against an Azure AI Foundry deployment through a local token-refreshing passthrough, with no request translation."
tags: [cli, codex, foundry, agents]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks codex [subcommand] [options]"
examples:
  - command: "pks codex init"
    description: "Preflight a deployment and write the managed provider block"
  - command: "pks codex"
    description: "Launch codex against the default Foundry deployment"
  - command: "pks codex -m gpt-5.6-sol"
    description: "Pick a specific Foundry deployment for this run"
  - command: "pks codex resume --last"
    description: "Resume the most recent Codex session through Foundry"
  - command: "pks codex --print-env"
    description: "Print the launch command instead of starting codex"
---

`pks codex` runs the genuine upstream Codex CLI — the `@openai/codex` package — against an Azure AI Foundry Responses-API deployment. Nothing about the Codex request or response is translated; pks only supplies authentication and points Codex at a local endpoint.

## Overview

`pks codex` starts a loopback HTTP passthrough on `127.0.0.1`, injects a fresh Microsoft Entra ID bearer token into every Codex Responses request, and forwards the body through unchanged. Because the token is minted per request rather than exported once at launch, a Codex session outlives the roughly one-hour lifetime of an Entra access token.

- **Real Codex, real UX.** The binary that runs is the upstream `codex` CLI, with its own interface, sessions, and tooling.
- **Foundry billing and quota.** Requests land on an Azure AI Foundry deployment under your organization's resource, not on OpenAI's API.
- **Managed config, preserved config.** `pks codex init` writes one marker-delimited block into `~/.codex/config.toml` and leaves everything else in that file alone.
- **Sibling command:** `pks claude codex` runs Claude Code against a Foundry GPT/Codex deployment through a translating proxy. `pks codex` is the opposite choice — the real Codex CLI, billed through Foundry.

## What you get

- **A per-request Entra token.** The passthrough obtains bearer tokens through the stored Foundry credentials, so long sessions never fail on an expired token.
- **No secrets on disk.** `~/.codex/config.toml` gets a provider block pointing at the loopback proxy. No key or token is written into it.
- **A preflight that fails early.** `pks codex init` makes a real Foundry Responses call against the chosen deployment before writing any configuration, so a wrong deployment name surfaces immediately.
- **Foundry-compatibility fixes.** Codex features and tool namespaces that Foundry rejects are disabled on launch and stripped from outgoing requests.
- **Native session commands.** `resume`, `exec`, `fork`, `archive`, `unarchive`, and `delete` are forwarded verbatim to the real Codex CLI through the same authenticated path.
- **A failure log.** Auth failures, non-2xx upstream responses, and `response.failed` events are appended to `~/.pks-cli/codex-passthrough-failures.log`.

## How it fits together

Authentication comes from `pks foundry init`. Every `codex` subcommand checks that you are authenticated and that a Foundry resource endpoint is resolved; without both it prints an error pointing at `pks foundry init` and exits 1.

On launch, pks resolves a deployment name, starts the passthrough on a loopback port, generates a random per-run token that the passthrough validates on every incoming request, and then executes the real `codex` binary with the `pks-foundry` provider selected. Codex talks to `127.0.0.1`; the passthrough talks to Foundry. Two files hold state: `~/.codex/config.toml` holds the managed provider block, and `~/.pks-cli/codex.json` holds the pks-side launch defaults written by `init`.

- **pks owns:** authentication, the loopback proxy, request sanitizing, and the provider block.
- **Codex owns:** the interface, the session store, and everything `resume`, `fork`, `archive`, `unarchive`, and `delete` do.

## Commands

| Subcommand | Purpose | Guide |
|---|---|---|
| `run` | Launch codex against the resolved Foundry deployment (default) | [Launch the Codex CLI on Foundry](/tools/pks/codex/run) |
| `resume` | Reattach to an earlier session by ID or with `--last` | [Resume a Codex session](/tools/pks/codex/resume) |
| `exec` | Codex's non-interactive execution mode | [Run Codex non-interactively](/tools/pks/codex/exec) |
| `fork` | Branch a session into a second continuation | [Manage Codex sessions](/tools/pks/codex/sessions) |
| `archive` | Move a session out of the active set | [Manage Codex sessions](/tools/pks/codex/sessions) |
| `unarchive` | Restore an archived session | [Manage Codex sessions](/tools/pks/codex/sessions) |
| `delete` | Permanently delete a session — no undo | [Manage Codex sessions](/tools/pks/codex/sessions) |
| `init` | Preflight a deployment and write the managed config block | [Set up pks codex](/tools/pks/codex/init) |

Running `pks codex` with no subcommand is identical to `pks codex run`. Full flag, argument, and environment detail lives on the [pks codex CLI reference](/tools/pks/codex/reference).

## Next steps

- [Set up pks codex against Azure AI Foundry](/tools/pks/codex/init) — preflight a deployment and write the managed provider block
- [Launch the Codex CLI on Foundry](/tools/pks/codex/run) — the everyday entry point, flag by flag
- [Resume a Codex session through Foundry](/tools/pks/codex/resume) — reattach to earlier sessions
- [Run Codex non-interactively](/tools/pks/codex/exec) — scripted, one-instruction runs
- [Manage Codex sessions through Foundry](/tools/pks/codex/sessions) — fork, archive, unarchive, and delete
- [How the Foundry passthrough works](/tools/pks/codex/passthrough) — the loopback proxy, the managed block, and the failure log
- [pks codex CLI reference](/tools/pks/codex/reference) — every subcommand, flag, and environment variable

## Defaults

| Setting | Value |
|---|---|
| Deployment | `gpt-5-codex` |
| Loopback port | `8788` |
| Reasoning effort | `medium` |
| Approvals and sandbox | Bypassed unless `--safe` is passed |

`pks codex init` overwrites these defaults in `~/.pks-cli/codex.json`, and `-m`, `-p`, and `-e` override them for a single run. See the [pks codex CLI reference](/tools/pks/codex/reference) for the resolution order.
