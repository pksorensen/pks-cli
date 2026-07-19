---
title: "How the Foundry passthrough works"
description: "The loopback proxy behind pks codex — per-request Entra tokens, the managed Codex config block, request sanitizing, and where silent failures are recorded."
tags: [concept, codex, foundry, auth]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
---

Every `pks codex` command runs the same small piece of machinery: a local web server on your own machine that stands between the Codex CLI and Azure AI Foundry. Understanding it explains the flags, the config files, and the failure modes.

## The loopback passthrough

When a `codex` subcommand starts, pks launches a Kestrel HTTP server bound to `127.0.0.1` on the chosen port — `8788` by default, or a free port with a warning if that one is already bound. Codex is then launched pointing at that server.

The server does three things per request: it validates the caller, it attaches a freshly obtained Microsoft Entra ID bearer token from the stored Foundry credentials, and it forwards the body to the Foundry Responses endpoint unchanged. No request or response shape is translated.

Its request body limit is raised to 256 MB, because Codex resends the full conversation — including base64-encoded screenshots — on every Responses call.

## Why a proxy instead of an environment variable

An Entra access token lives about an hour. Exporting one into the Codex process at launch would strand any session that runs longer than that. Minting a token inside the proxy, per request, means the session length is bounded by your work, not by token lifetime.

The same design keeps secrets off disk. Nothing in `~/.codex/config.toml` is confidential — the credential lives only in the pks Foundry store and is applied in memory as requests pass through.

## The per-run token

pks generates a random token for each run and passes it to the launched Codex process as `PKS_CODEX_TOKEN`. The passthrough rejects any request that does not carry it, so another local process cannot borrow your Foundry credential through the open loopback port.

pks also removes `OPENAI_API_KEY` from the launched Codex environment. An ambient OpenAI key would otherwise let Codex quietly bypass the selected Foundry provider.

## The managed config block

`pks codex init` writes into `~/.codex/config.toml` between two markers:

```text
# >>> pks-codex (managed) — edit via `pks codex init`
# <<< pks-codex
```

Only the content between those markers is replaced on each run of `init`. Your own `model_provider` default, ChatGPT login, and every other setting in the file are preserved verbatim. The block configures `model_providers.pks-foundry` to point at the loopback passthrough.

pks-side launch defaults live separately, in `~/.pks-cli/codex.json`: deployment, port, and reasoning effort. `init` writes that file; every other `codex` subcommand reads it as a fallback.

## What gets stripped

Azure AI Foundry rejects some OpenAI-internal features and encrypted tool namespaces that the Codex CLI would otherwise send. pks handles this on both sides of the launch:

- Codex is started with `collaboration_modes`, `apps`, `multi_agent_v2`, and `multi_agent` disabled.
- The passthrough removes collaboration-named and collaboration-namespaced entries from the outgoing request's additional tools.

A Codex feature added after this list was written can hit the same incompatibility. The symptom is an upstream rejection recorded in the failure log.

## Where failures go

Auth failures, non-2xx upstream HTTP responses, and `response.failed` streaming events are appended to:

```text
~/.pks-cli/codex-passthrough-failures.log
```

They are not printed verbosely on the terminal. When a session ends without a clear error, that file is the first place to look.

## Environment variables

| Variable | Purpose |
|---|---|
| `PKS_CODEX_TOKEN` | Random per-run token pks passes to the launched Codex process; the passthrough validates every request against it. Not user-set. |
| `OPENAI_API_KEY` | Removed from the launched Codex environment so an ambient key cannot bypass the Foundry provider. |
| `PKS_CODEX_FOUNDRY_API_KEY` | Environment-key name used by the direct Azure-API-key provider block. No current command path writes that block, so this variable is unpopulated today. |

## See also

- [pks codex](/tools/pks/codex) — group overview and mental model
- [Set up pks codex against Azure AI Foundry](/tools/pks/codex/init) — writing the managed block
- [Launch the Codex CLI on Foundry](/tools/pks/codex/run) — the everyday entry point
- [pks codex CLI reference](/tools/pks/codex/reference) — every subcommand and flag
