---
title: "pks share"
description: "Log a machine into an Agent Share server via OIDC PKCE loopback login, the one-time prerequisite before pks agent register can mint an agent inbox."
tags: [reference, cli, auth]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks share <command>"
examples:
  - command: "pks share init"
    description: "Log this host into the default Agent Share server"
---

`pks share` logs a host into an Agent Share server (`share.agentics.dk` by default) so a later command can register a coding session against it. Agent Share is a hosted directory that coding sessions register themselves into, so other people can hand tasks or questions to an agent by sharing into it.

The group has exactly one command, `init`. It performs the login and stores an encrypted refresh token; it does not itself register a session — that is a separate step handled by `pks agent register`.

## When to use it

Run `pks share init` once per machine or devcontainer, before ever running `pks agent register`. Re-run it if the stored refresh token for a host has expired or been revoked, or to log in against a different Agent Share host or issuer — each host gets its own credential file, so re-running for the same host string overwrites that host's saved login.

## Prerequisites

- **An interactive terminal.** The command checks `Console.IsInputRedirected` and hard-fails with `pks share init must run in an interactive terminal.` under any piped or non-interactive invocation — it will not work in scripts or CI.
- **A browser reachable from the loopback callback.** On macOS/Linux, opened via `$BROWSER` if set, else the platform default (`open` on macOS, `xdg-open` on Linux); on Windows the launcher always shells out to `cmd /c start` and `$BROWSER` is ignored. Or, inside a devcontainer, the editor's automatic forwarding of the random ephemeral port the CLI binds for the callback.
- **Network access** to the OIDC issuer's `/.well-known/openid-configuration` discovery document and its token endpoint, plus best-effort (non-fatal) access to the Agent Share host's `/api/agents` endpoint.

## Auth model

`pks share init` runs an OIDC **Authorization Code + PKCE loopback flow** (RFC 8252) against a public client — `agentics-share-desktop` by default, no client secret. The CLI opens a browser to the issuer's authorization endpoint and catches the redirect on an ephemeral `127.0.0.1:<random-port>/callback/` HTTP listener. It exchanges the code for tokens and decodes (without verifying — the server verifies) the returned JWT locally to read `sub`/`email`/`name`.

Only the **refresh token** is persisted, AES-GCM encrypted at rest per host under `~/.pks-cli/share/{sanitized-host}.json`, with the 32-byte key stored separately in `~/.pks-cli/.share-kek` — the same scheme used by the certificate and SSH-key stores.

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `BROWSER` | (unset) | Executable used to open the sign-in URL instead of the OS default opener (`open`/`xdg-open`), **macOS/Linux only** — on Windows the launcher always uses `cmd /c start` and this variable is not consulted. If it fails to launch, the failure is swallowed silently — the printed URL is the fallback for headless environments. |

## Synopsis

```text
pks share <command>
```

```text
init    Log this host into an Agent Share server via OIDC
```

## init

Interactively logs this host into an Agent Share server via OIDC so that `pks agent register` can later register a coding session — mint an agent inbox — on your behalf.

The command prompts for three values, in order, with no flags to supply them non-interactively:

1. **Agent Share server URL** — default `https://share.agentics.dk`.
2. **OIDC issuer** — default `https://login.agentics.dk/realms/agentics`.
3. **OIDC client id** — default `agentics-share-desktop`, a public loopback client.

Pressing Enter at each prompt accepts the default and logs into the standard agentics.dk setup.

After the prompts, it runs the full PKCE loopback flow: prints and opens the auth URL, then waits up to 5 minutes on a local HTTP listener for the browser redirect. It exchanges the returned code for tokens, makes a best-effort sanity `GET {host}/api/agents` with the new bearer token (a failure here only prints a yellow warning and still saves the login), then encrypts and persists the refresh token plus the resolved identity (`sub`, display name) to `~/.pks-cli/share/{sanitized-host}.json`. On success it prints who you're logged in as and points at the next step, `pks agent register`.

There are no arguments or options.

```bash
pks share init
```

You should see a browser open (or a URL printed) for sign-in, followed by a confirmation of the logged-in identity once the browser redirect completes.

### Gotchas

- Refuses to run with redirected stdin — interactive TTY only, exits `1` otherwise.
- No CLI flags exist for server, issuer, or client id — every run re-prompts for all three.
- Waits up to 5 minutes for the browser round-trip. If the browser never returns to the loopback listener (forwarding not set up in a remote devcontainer, or the tab is closed), the command throws and exits `1` with a red "Login failed" message.
- A state mismatch or a missing `code` query parameter on the callback also throws: `OIDC sign-in failed (no code / state mismatch).`
- The post-login verification `GET {host}/api/agents` is advisory only. A non-2xx response or network error only prints a yellow warning — credentials are saved regardless, so a mistyped host URL can silently succeed with a warning instead of failing outright.
- Credentials are keyed by a sanitized version of the host string (non-alphanumerics replaced with `_`) as `~/.pks-cli/share/{key}.json`. Re-running `share init` for the same host overwrites that file; there is no separate path for adding a second identity against the same host.
- The refresh token is encrypted with a KEK auto-generated at `~/.pks-cli/.share-kek` on first use. Losing or deleting that file makes all previously saved share credentials undecryptable.
- There is no `share status`, `share list`, or `share logout`/`remove` subcommand, even though the underlying credential store supports list/get/remove internally — those methods are currently consumed only by the agent-registration path, not exposed as their own `share` subcommands.

## Troubleshooting

> **Note.** `pks share init` only performs the login and provider setup. It does not register a session — running it alone will not make anything appear in the Share panel. Run `pks agent register` afterward.

- **"must run in an interactive terminal"** — you invoked the command with redirected stdin (a script, CI, or a piped shell). Run it directly in a TTY.
- **"Login failed" after 5 minutes** — the browser never completed the redirect back to the loopback listener. In a devcontainer, confirm the editor is forwarding the ephemeral callback port; otherwise check that the browser wasn't closed before completing sign-in.
- **"OIDC sign-in failed (no code / state mismatch)"** — the callback arrived without a valid `code`, or the `state` parameter didn't match. Re-run the command and complete the sign-in flow without navigating away mid-flow.
- **Yellow warning after an otherwise successful-looking login** — the sanity check against `{host}/api/agents` failed. Credentials were still saved; verify the server URL you entered was correct before assuming the login itself failed.
- **Need to log into a different host or re-authenticate** — run `pks share init` again; it overwrites the credential file for that host.

## See also

- [pks](/tools/pks) — command families and the operator mental model this group belongs to.
