---
title: "Run the local Foundry proxy"
description: "Start a localhost reverse proxy that accepts a throwaway token and forwards requests to Azure AI Foundry with a freshly refreshed Azure bearer token."
tags: [how-to, azure, proxy, scripting]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks foundry proxy [options]"
examples:
  - command: "eval $(pks foundry proxy)"
    description: "Start on a random port and export URL and token"
  - command: "pks foundry proxy --port 8080"
    description: "Listen on a fixed port"
  - command: "pks foundry proxy --token my-secret"
    description: "Use a caller-supplied proxy token"
---

Give a local process access to your Azure AI Foundry endpoint without giving it your Azure credential. `pks foundry proxy` listens on localhost, checks a throwaway token on each request, then forwards to the real Foundry endpoint with a freshly refreshed Azure bearer token injected.

> **Note.** The proxy blocks. It runs in the foreground until you stop it, so launch it as a background or detached process.

## 1. Prerequisites

- **A completed `pks foundry init`** with a resource selected. The proxy checks authentication once at startup, and returns 502 when no endpoint is configured.
- **A client that lets you set a base URL and an `Authorization` header** — for example a narration or text-to-speech generator that should never hold Azure credentials.

## 2. Start the proxy and capture its variables

```bash
eval $(pks foundry proxy)
```

The command prints exactly two lines to stdout before it binds the listener, so the shell can consume them:

```text
export FOUNDRY_PROXY_URL=http://localhost:PORT
export FOUNDRY_PROXY_TOKEN=...
```

pks suppresses its ASCII banner for this command specifically, because any extra stdout would break `eval`.

## 3. Pin the port or the token

Both default to a random value — a free port and a UUID. Fix either when another process needs to know it in advance:

```bash
pks foundry proxy --port 8080 --token my-secret
```

## 4. Point a client at the proxy

Send requests to `FOUNDRY_PROXY_URL` with `Authorization: Bearer $FOUNDRY_PROXY_TOKEN`. The check is an exact string match against the single token for that process.

The proxy strips `Authorization`, `Host`, `Connection`, `Transfer-Encoding`, `Keep-Alive`, `Proxy-Authenticate`, `Proxy-Authorization`, `TE`, `Trailers`, and `Upgrade` before forwarding, then injects the real bearer token. All other request headers pass through to Azure unchanged.

## 5. Choose a token scope

The forwarded Azure token uses the cognitive-services scope by default. Override it when the upstream call needs a different audience:

```bash
pks foundry proxy --scope https://management.azure.com/.default
```

## 6. Verify

With the proxy running, a request to `FOUNDRY_PROXY_URL` carrying the proxy token reaches Azure and returns the upstream response. A 401 means the proxy token did not match; a 502 means pks could not acquire an Azure token or has no endpoint configured.

## Options

| Flag | Default | Description |
|---|---|---|
| `-p`, `--port <port>` | random free port | Port the proxy listens on. |
| `-t`, `--token <token>` | random UUID | Token clients must send in the `Authorization` header. |
| `-s`, `--scope <scope>` | `https://cognitiveservices.azure.com/.default` | Azure token scope requested for forwarded calls. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

## Security model

The proxy holds one shared static token for the lifetime of the process. There is no per-client scoping, no expiry, and no rotation. Anyone who obtains the printed token while the process runs can mint real Azure tokens through it. Keep the proxy short-lived, bound to localhost, and prefer the random token over a fixed one.

## Troubleshooting

**The command never returns.** Expected. It blocks on the HTTP server. Run it in the background and capture the two exported lines first.

**`eval` produced nothing usable.** Confirm you ran `eval $(pks foundry proxy)` and not a variant that swallows stdout. The command emits only the two `export` lines.

**502 "Failed to acquire Azure access token".** The stored credential stopped working while the proxy was running. Stop it, run `pks foundry init --force`, and start it again.

**502 with no endpoint mentioned.** Resource selection never completed. Run [pks foundry select](/tools/pks/foundry/select).

**401 from the proxy.** The client sent the wrong token. Compare it against `FOUNDRY_PROXY_TOKEN`.

**No log output when something fails.** Logging inside the proxy is disabled to keep stdout clean. Failures surface only as HTTP status codes at the client.

## Next steps

- [Print a Foundry access token](/tools/pks/foundry/token) — when the client can hold a token directly
- [Inspect stored Foundry credentials](/tools/pks/foundry/status) — check which endpoint the proxy forwards to
- [pks foundry reference](/tools/pks/foundry/reference) — all flags and scopes
