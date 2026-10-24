---
title: "pks exec"
description: "Run a companion tool through pks-cli's discovery contract, wiring in Azure AI Foundry, Gemini, or OpenAI-compatible provider credentials automatically."
tags: [reference, cli, providers]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks exec [options] <EXECUTABLE> [ARGS...]"
examples:
  - command: "pks exec agent-photographer.exe shoot"
    description: "Run a tool with providers and models wired up interactively"
  - command: "pks exec --provider foundry agent-photographer.exe preview"
    description: "Force Foundry as the provider, skip the selection prompt"
  - command: "pks exec --dry-run agent-photographer.exe shoot"
    description: "Preview resolved env vars without launching the child process"
---

`pks exec` is a universal launcher for companion tools that implement pks-cli's discovery contract. It runs the target executable once to read a capability manifest, lets you pick a provider and model per capability, resolves the manifest's env-var templates against that choice, and re-execs the same tool for real with the resolved environment injected — so the child tool never needs its own provider, auth, or model-selection UI.

`pks exec` is a single leaf command, not a branch: it has no subcommands of its own.

## Overview

- **Discovery first.** The child executable is run once with `PKS_DISCOVERY=1` and must print a JSON capability manifest to stdout, or the command fails.
- **Provider selection per capability.** For each capability the manifest declares, `pks exec` filters the manifest's offered providers down to ones you actually have available — Azure AI Foundry, Gemini, or an OpenAI-compatible endpoint — and prompts you to pick one, or picks automatically if only one qualifies.
- **Model prompts per role.** For each model role a chosen provider declares, `pks exec` prompts for a model ID with a suggested default.
- **Env resolution and re-exec.** The provider's env-var templates are expanded against your choices, then the same executable is launched again — this time for real — with `ARGS` and the resolved environment merged in.

Use `pks exec` when launching a companion CLI or agent binary (for example `agent-photographer.exe`, or any other `pks-agent-*` tool) that speaks the discovery contract and needs Foundry, Gemini, or OpenAI-compatible credentials and model IDs wired into its environment. It is not for arbitrary shell commands — the target must emit a valid `v1` manifest on `PKS_DISCOVERY=1`, or discovery fails immediately.

## Prerequisites

- **A discovery-aware executable.** `EXECUTABLE` must respond to `PKS_DISCOVERY=1` by exiting `0` and printing a `v1` manifest JSON object to stdout within 10 seconds.
- **At least one available provider.** Azure AI Foundry requires having already run [`pks foundry init`](/tools/pks/foundry) — availability is checked via the CLI's stored Foundry credentials, not just declared in the manifest. Gemini requires a key from `pks google init` or `GEMINI_API_KEY` exported in the shell `pks` runs in. An OpenAI-compatible endpoint requires `OPENAI_BASE_URL` or `OPENAI_API_KEY` to be exported.
- **The manifest must offer a provider you have.** `pks exec` does not prompt to set missing credentials — if no provider the manifest lists is available locally, it reports the gap and points at `pks foundry init` / `pks google init` / `OPENAI_BASE_URL`.

## How it works

1. **Discovery.** `pks exec` shells out to `EXECUTABLE` with `PKS_DISCOVERY=1` and reads its stdout within a 10-second timeout, parsing it as a `v1` manifest. Discovery fails if the exit code is non-zero, stdout is empty, no `{` is found, the JSON does not deserialize, or `manifestVersion` is not `"v1"`.
2. **Provider selection.** For each capability in the manifest, if the capability is not required you are prompted to opt in. `pks exec` then filters that capability's listed providers down to ones currently available, auto-picks the sole available provider (or an explicit `--provider` match), and otherwise prompts an interactive selection.
3. **Model selection.** For each model role the chosen provider declares, `pks exec` prompts for a model ID with a role-based suggested default — for example Foundry's stored default model or `claude-opus-4-7`/`claude-sonnet-4-6` by role, Gemini's `gemini-2.5-pro`/`gemini-2.5-flash`, or OpenAI-compatible's `gpt-4o`/`gpt-4o-mini`. There is no validation against what the provider actually serves.
4. **Env resolution.** The provider's `Env` dictionary templates are expanded, substituting `{endpoint}`, `{endpoint:openai}`, `{apikey}`, `{imds:endpoint}`, `{imds:header}`, `{proxy:endpoint}`, `{proxy:token}`, and `{model:<role>}` placeholders. The `{imds:*}` placeholders lazily start a loopback managed-identity (IMDS) proxy that forwards Azure token requests through the CLI's own stored Foundry credentials, guarded by a per-run random `X-IDENTITY-HEADER` secret. The `{proxy:*}` placeholders lazily start a loopback credential proxy instead — see [Credential proxy](#credential-proxy).
5. **Exec.** Unless `--dry-run` is set, `EXECUTABLE` is launched with `ARGS` and the resolved environment overlay merged in. `Ctrl+C` is forwarded to the child as a tree-kill. When the child exits, the IMDS and credential proxies (if started) are stopped and the child's own exit code is returned — or `127` if the process failed to start, `2` if `EXECUTABLE` was omitted, `1` if discovery failed or if no usable provider was found for a required capability.

## Credential proxy

`{proxy:endpoint}` and `{proxy:token}` give a child access to its provider **without the child ever seeing a credential** — not a key, not an Entra token.

| Placeholder | Resolves to | Shown by `--dry-run` |
|---|---|---|
| `{proxy:endpoint}` | `http://127.0.0.1:<port>` — no trailing slash, OS-assigned port, IPv4 loopback so clients that try `::1` for `localhost` first (Go on Windows) still connect | yes |
| `{proxy:token}` | a random per-run bearer token the proxy accepts | `(set, hidden)` |

The child points its client at `{proxy:endpoint}` exactly as it would at the provider's own endpoint — same paths, same query strings — and sends `Authorization: Bearer {proxy:token}`. The proxy checks the token (anything else is `401`), drops it, signs the request with the real credential and forwards it:

- **`foundry`** — upstream is the selected Foundry resource endpoint; the credential is an Entra access token for `https://cognitiveservices.azure.com/.default`, minted from your `pks foundry init` sign-in and held in memory for at most five minutes. It works for every data-plane route on the resource: `/speechtotext/transcriptions:transcribe`, `/openai/deployments/…`, `/openai/v1/…`, and the realtime routes.
- **`openai-compatible`** — upstream is `OPENAI_BASE_URL`; the credential is `OPENAI_API_KEY`.
- **Any other kind** — both placeholders resolve to empty.

**WebSockets are proxied too.** A WebSocket upgrade (`ws://127.0.0.1:<port>/mai/v1/realtime?…`) is checked against the same token, and the proxy opens the upstream socket (`wss://…`) with the real credential *before* accepting the child's, so an upstream refusal reaches the child as `502` on its upgrade request rather than as a socket that opens and dies. Frames then pass through untouched in both directions — text and binary, fragmented messages, and close status and reason — for as long as the session lasts; the token signs only the handshake, so an hour-long session is not cut off when the token that opened it expires. If either side closes, the close is forwarded; if either side fails, the other is closed with `1011`.

The proxy forwards a request body with the child's `Content-Length` intact rather than re-chunking it. Foundry's Decision route (`/providers/microsoft/v1/systemone`) refuses chunked bodies with HTTP 400 `no_content_length_header`, and a tool that sends a known length upstream gets the same length forwarded.

**Two routes are pks's own, served by the proxy and never forwarded.** pks is the gateway, whichever provider the manifest bound:

- `GET {proxy:endpoint}/pks/v1/models`, with the same bearer token, is the catalog across *every* provider registered with pks — Foundry, Anthropic, OpenAI, Gemini, Moonshot, TypeSafe, an OpenAI-compatible endpoint — in the OpenAI list shape with a `pks` object per entry (`provider`, `enabled`, `default`, `roles`). The same document is [pks models](/tools/pks/models).
- `POST {proxy:endpoint}/pks/v1/chat/completions` takes an OpenAI chat-completions body and routes it by its `model`: pks finds the provider that lists it, rewrites the name to that provider's own, signs the request with *that provider's* credential and relays the answer, streaming included. `provider/id` (`gemini/gemini-2.5-flash`) pins the provider; a bare id goes to the first provider in catalog order that lists it. A model pks cannot route is an OpenAI-shaped `404 model_not_found`, never a forwarded request.

So a tool bound to Foundry for speech can clean a transcript with a Gemini or Claude model through the same endpoint and token, and never holds a second key. A `502` on the catalog route carries the reason when no provider at all could be listed.

Every capability that uses `{proxy:*}` in one run shares one proxy per provider kind and one token. The proxy listens on loopback only and stops when the child exits; the token is the boundary while it runs, so treat it like the credential's stand-in it is, and do not log it.

A manifest binding for a tool that talks to Foundry's speech and realtime APIs looks like:

```json
{ "kind": "foundry",
  "env": { "FOUNDRY_ENDPOINT": "{proxy:endpoint}", "FOUNDRY_TOKEN": "{proxy:token}" } }
```

## Synopsis

```text
pks exec [options] <EXECUTABLE> [ARGS...] [-- TOOL-ARGS...]
```

`EXECUTABLE` and `ARGS` are positional. `pks exec`'s own options (`--provider`, `--port`, `--dry-run`) are recognized by name wherever they appear on the command line, including after `EXECUTABLE`, so `pks exec ./tool.exe --dry-run shoot` behaves the same as `pks exec --dry-run ./tool.exe shoot`.

**The tool's own options go after `--`.** Everything after `--` is forwarded verbatim, so `pks exec heypoul -- meeting --no-me --language en-US` runs `heypoul meeting --no-me --language en-US`. An option `pks exec` does not know that appears *before* `--` is refused with exit code `2` rather than silently dropped.

### Arguments

- **`EXECUTABLE`** (required) — path to the tool to run. Must implement the discovery contract.
- **`ARGS`** (optional, repeatable) — positional words after `EXECUTABLE`, forwarded to the real, non-discovery invocation, followed by everything after `--`.

### Options

| Flag | Default | Description |
|---|---|---|
| `--provider <KIND>` | — | Skip the interactive provider prompt and pick this provider kind directly (for example `foundry`, `gemini`, `openai-compatible`). Fails with an error if the kind is not both offered by the manifest and locally available. The bound provider decides where the proxy's plain paths go; the `/pks/v1/*` routes see every registered provider regardless. |
| `--port <N>` | random free port | Bind the local managed-identity (IMDS) proxy to this port instead of an OS-assigned one. Only relevant when the manifest's env templates use `{imds:endpoint}`/`{imds:header}`, which today means Foundry-backed capabilities. |
| `--dry-run` | — | Run discovery and env resolution, then print the resolved command line and the full env overlay instead of exec'ing the child. Values that come from a credential placeholder (`{apikey}`, `{imds:header}`, `{proxy:token}`, `{entra:clientsecret}`) print as `(set, hidden)`, or `(not set)` when nothing filled them. Any proxy that was started is stopped immediately afterward. |

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `PKS_DISCOVERY` | `(unset)` | Set to `1` by `pks exec` itself when invoking the child for the discovery phase. Not something you set — the child must detect it and print its manifest instead of running normally. |
| `GEMINI_API_KEY` | `(unset)` | Presence makes the `gemini` provider selectable for a capability, and resolves the `{apikey}` placeholder for Gemini — unless `pks google init` stored a key, which wins. |
| `OPENAI_BASE_URL` | `(unset)` | Presence (or `OPENAI_API_KEY`) makes the `openai-compatible` provider selectable, and resolves the `{endpoint}` placeholder. |
| `OPENAI_API_KEY` | `(unset)` | Presence makes `openai-compatible` selectable, and resolves the `{apikey}` placeholder for it. |

## Examples

```bash
pks exec agent-photographer.exe shoot
```

Runs `agent-photographer.exe`'s `shoot` subcommand, prompting interactively for a provider and model per capability the tool's manifest declares.

```bash
pks exec --provider foundry agent-photographer.exe preview
```

Forces Azure AI Foundry as the provider for every capability, skipping the selection prompt, then runs the tool's `preview` subcommand.

```bash
pks exec --dry-run agent-photographer.exe shoot
```

Resolves providers and models as usual, then prints the command line and env overlay — with secret-looking values masked — instead of launching the child.

## Troubleshooting

- **`discovery failed for …`** — `EXECUTABLE` either exited non-zero, printed nothing, printed non-JSON, or printed a manifest whose `manifestVersion` is not `v1`, when run with `PKS_DISCOVERY=1`. An ordinary binary that does not know about `PKS_DISCOVERY` will run normally instead of returning a manifest, which fails discovery outright — or hangs until the 10-second timeout kills the process tree.
- **"no registered provider matches this capability"** — none of the providers the manifest lists for that capability are available locally. Run `pks foundry init` for Foundry, `pks google init` (or export `GEMINI_API_KEY`) for Gemini, or export `OPENAI_BASE_URL` (and `OPENAI_API_KEY`) for an OpenAI-compatible endpoint.
- **`404 model_not_found` from `/pks/v1/chat/completions`** — the `model` is not in `pks models`, names a provider pks has no credential for (the message says which `init` to run), or is a TypeSafe judge, which has no chat route.
- **`--provider` still fails** — `--provider` only chooses among providers the manifest offers **and** that are locally available. Asking for a kind the tool does not declare, or one you have not authenticated or configured, fails fast rather than falling back to a prompt.
- **Position of `--provider`/`--port`/`--dry-run` doesn't matter.** These are recognized by name no matter where they appear relative to `EXECUTABLE`, so putting them after `EXECUTABLE` still gets them parsed by `pks exec` rather than forwarded in `ARGS`.
- **A typo'd model ID is not caught.** Model IDs are solicited with a free-text prompt and a suggested default; there is no validation against what the provider actually serves, so a typo is passed straight through as the resolved `{model:<role>}` value.
- **Scripting around `pks exec`.** The child's own exit code is returned as-is, so check `$?` against the wrapped tool's exit-code conventions, not against `pks`'s own.

> **Note.** The `{imds:endpoint}`/`{imds:header}` loopback proxy only checks the `X-IDENTITY-HEADER` secret when a request includes that header at all — a request that omits it is accepted with no check performed, and the token scope is caller-controlled via the request's `resource` query string. The listener is loopback-only, but any other local process can reach it while the child is running.

## See also

- [pks](/tools/pks) — the CLI's full command surface and installation paths
- [pks foundry](/tools/pks/foundry) — the prerequisite for making the `foundry` provider kind available to `pks exec`
