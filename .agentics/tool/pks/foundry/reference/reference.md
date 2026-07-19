---
title: "pks foundry reference"
description: "Complete command, flag, scope, and stored-credential reference for the pks foundry group — init, select, token, status, proxy, and usage."
tags: [reference, azure, auth, cli]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks foundry <command> [options]"
examples:
  - command: "pks foundry init --force"
    description: "Re-run the browser sign-in from scratch"
  - command: "pks foundry select"
    description: "Re-pick resource and deployments without a browser"
  - command: "pks foundry token --scope https://management.azure.com/.default"
    description: "Mint a management-plane token"
  - command: "pks foundry proxy --port 8080"
    description: "Run the local proxy on a fixed port"
  - command: "pks foundry status"
    description: "Print the stored credential record"
---

`pks foundry` manages Azure AI Foundry authentication and model selection for the pks CLI. It holds one credential record per user, refreshes Azure access tokens on demand, and exposes them to scripts and local processes.

The group has exactly six subcommands and no aliases or nested branches. All six share one settings type, so `-v|--verbose` is accepted everywhere. Credentials are global to the machine and user — nothing here is scoped per repository.

## Synopsis

```text
pks foundry <command> [options]
```

```text
init      Sign in to Azure AI Foundry and pick a resource and model deployments
select    Re-pick subscription, resource, and deployments using the stored token
token     Print a bearer access token for the selected resource
status    Print the stored credential and selection record
proxy     Run a localhost reverse proxy that injects a fresh Azure token
usage     Report Azure cost scoped to the selected Foundry resource
```

### Inherited options

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. Accepted by every subcommand. |

### Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `AZURE_CLIENT_ID` | (unset) | Service-principal fallback used by `DefaultAzureCredential` when no resource API key is stored. |
| `AZURE_CLIENT_SECRET` | (unset) | Secret paired with `AZURE_CLIENT_ID` for the same fallback. |

Neither variable is read by the `pks foundry` commands themselves. `pks foundry init` names them as the fallback path for launching `claude` in a devcontainer against Foundry when you skip the API-key prompt.

### Authentication model

Sign-in is an OAuth 2.0 authorization-code flow with Proof Key for Code Exchange (PKCE) against the Azure CLI's well-known public client ID, `04b07795-8ddb-461a-bbee-02f9e1bf7b46`. No app registration is required. The browser redirects to a local loopback listener, pks exchanges the code, and stores the refresh token.

Every command that needs a live token refreshes silently through the `refresh_token` grant at `https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token`, and rewrites the stored record when Azure rotates the refresh token.

The stored-credential check tests only that a non-empty refresh token exists. It does not confirm the token is still valid at Azure. A revoked or expired refresh token therefore surfaces as a failure inside the next command that calls Azure, typically a message about failing to obtain an access or management token, rather than at a preflight check.

| Scope | Value |
|---|---|
| Management plane | `https://management.azure.com/.default` |
| Cognitive services (default for `token` and `proxy`) | `https://cognitiveservices.azure.com/.default` |
| Initial login | includes `offline_access` so a refresh token is issued |

### Stored credential record

The record persists globally under the storage key `foundry.auth.credentials` and holds the tenant ID, refresh token, selected subscription ID and name, selected resource endpoint, name, group, location and kind, the default model, the enabled model deployments, the voice-classifier model, an optional API key, and creation and last-refresh timestamps.

Because all six subcommands read this one record, running `select` or `init --force` changes what `token`, `proxy`, and `usage` operate against.

## init

Runs the first-time interactive sign-in. Opens a browser, completes the loopback OAuth exchange, then walks through subscription, Foundry resource, model deployments, default model, and an optional resource API key.

Resource discovery lists Cognitive Services accounts of kind `AIServices` and any account whose endpoint contains `.services.ai.azure.com`. The inference endpoint is derived as `https://{resourceName}.services.ai.azure.com`, not the `cognitiveservices.azure.com` endpoint Azure Resource Manager returns, because the Anthropic-compatible API lives on the former host.

Without `--force`, an existing refresh token makes the command a no-op that prints the current resource and exits 0. Without `--tenant`, it prompts for an email and resolves the tenant through the Azure AD user-realm and OpenID discovery endpoints, falling back to `common`.

Partial credentials — tenant plus refresh token — are stored the moment login succeeds, before resource selection. A crash after login leaves a credential that `select` can finish from.

Exit code 1 on: browser timeout or error, no subscriptions found, no Foundry resources found, or no model deployments on the chosen resource. Deployment multi-select requires at least one choice.

| Flag | Default | Description |
|---|---|---|
| `-f`, `--force` | `false` | Force re-authentication even when credentials exist. |
| `-t`, `--tenant <id>` | `common` | Azure AD tenant ID, skipping email-based discovery. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

## select

Re-runs subscription, resource, deployment, default-model, and voice-classifier selection using the stored refresh token. No browser, no re-login. Exits 1 when no refresh token is stored.

Zero deployments on the chosen resource is tolerated, which is the expected path for a Speech-only resource, since voice transcription uses the Azure Speech REST API. Previously enabled deployments are pre-ticked, so re-running is non-destructive by default.

The voice-classifier model turns spoken phrases into pks subcommands and is chosen from the deployments just enabled, or set to none for simple text matching.

The command attempts to fetch the resource's Cognitive Services subscription key through an Azure Resource Manager `listKeys` call with the management token, and falls back to manual entry when that fails. After saving, it warns when the resource kind is not `AIServices`, `CognitiveServices`, or `SpeechServices`, because `pks voice` returns 404 against other kinds.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |

## token

Prints a bearer access token for the selected resource, refreshing first. Exits 1 with a pointer to `pks foundry init` when not authenticated, and exits 1 suggesting `pks foundry init --force` when the refresh fails.

Output mode switches on whether stdout is redirected. In a terminal, the token prints and the command waits for a keypress; pressing `c` copies it using `xclip`, `xsel`, or `wl-copy` on Linux, `pbcopy` on macOS, or `clip` on Windows, silently skipped when none is present. When redirected or piped, the raw value is written with no keypress wait.

`--json` does not emit plain JSON. It gzip-compresses and base64-encodes a payload of the token, endpoint, model, resource, and subscription, for a browser tool that decodes it with `pako.ungzip`. Parsing it with `jq` requires base64-decoding and gunzipping first.

| Flag | Default | Description |
|---|---|---|
| `-s`, `--scope <scope>` | `https://cognitiveservices.azure.com/.default` | Azure token scope to request. |
| `--json` | `false` | Emit the gzip-compressed, base64-encoded payload form. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

## status

Prints the stored tenant, subscription, resource, endpoint, default model, resource group, authentication and refresh timestamps, and whether a refresh token is present. Makes no network call.

It reports a token as present based on disk state alone, so a revoked credential still shows as present. When nothing is stored it prints a not-authenticated hint and exits 0 — the exit code is not an auth signal.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |

## proxy

Starts a blocking, foreground HTTP reverse proxy on localhost. Requests carrying the proxy token in the `Authorization` header are forwarded to the Foundry endpoint with a freshly refreshed Azure bearer token injected.

The two `export` lines print to stdout before the listener binds, which is what makes `eval $(pks foundry proxy)` work. pks suppresses its banner for this command so stdout contains only those lines.

```text
export FOUNDRY_PROXY_URL=http://localhost:PORT
export FOUNDRY_PROXY_TOKEN=...
```

Authentication is one shared static token per process, compared by exact match, with no scoping, expiry, or rotation. Anyone holding the printed token can mint real Azure tokens through the proxy while it runs.

The proxy strips `Authorization`, `Host`, `Connection`, `Transfer-Encoding`, `Keep-Alive`, `Proxy-Authenticate`, `Proxy-Authorization`, `TE`, `Trailers`, and `Upgrade` before forwarding, and passes all other request headers through. It returns 502 when no Foundry endpoint is configured and when an Azure access token cannot be acquired. Internal logging is disabled, so failures appear only as HTTP status codes at the client.

| Flag | Default | Description |
|---|---|---|
| `-p`, `--port <port>` | random free port | Port the proxy listens on. |
| `-t`, `--token <token>` | random UUID | Token clients must send in the `Authorization` header. |
| `-s`, `--scope <scope>` | `https://cognitiveservices.azure.com/.default` | Azure token scope for forwarded calls. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

## usage

Reports Azure cost scoped to the selected resource's Azure Resource Manager ID, using the same billing service as `pks azure usage`. Prints a summary table, a cost-over-time chart, and a top-15 breakdown by meter. Exits 1 when not authenticated.

A saved resource triggers a confirmation prompt, defaulting to yes, before offering a different subscription and resource. The time window is an interactive prompt with no flag equivalent, so the command cannot run non-interactively.

Query failures — permissions, throttling, or a resource with no billable usage yet — are surfaced as an error with exit code 1 rather than partially rendered tables. The caller's identity needs Cost Management Reader or equivalent on the subscription; a valid Foundry data-plane token does not grant it.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |

## See also

- [pks foundry](/tools/pks/foundry) — the group landing page and mental model
- [Sign in to Azure AI Foundry](/tools/pks/foundry/init) — step-by-step first-time setup
- [Run the local Foundry proxy](/tools/pks/foundry/proxy) — proxy walkthrough and security notes
- [Print a Foundry access token](/tools/pks/foundry/token) — scripting patterns for token output
- [pks](/tools/pks) — the full pks CLI
