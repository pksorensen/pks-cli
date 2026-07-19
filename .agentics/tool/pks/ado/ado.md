---
title: "pks ado"
description: "Authenticate pks against Azure DevOps with OAuth2 and PKCE, then run a local git-proxy so devcontainers push and pull without holding a token."
tags: [reference, cli, auth]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks ado <command> [options]"
examples:
  - command: "pks ado init"
    description: "Log in with the browser and store a refresh token"
  - command: "pks ado init https://dev.azure.com/Org/Project/_git/Repo"
    description: "Log in (if needed) and allowlist a repo for the proxy"
  - command: "pks ado status"
    description: "Show who you're authenticated as, without a network call"
  - command: "pks ado git-proxy --allow Org/Project/Repo"
    description: "Start the credential-injecting proxy on port 7878"
---

`pks ado` authenticates the CLI against Azure DevOps and runs a local git-proxy so a devcontainer or VM can clone and push without ever holding an Azure DevOps token itself.

## Overview

The group has three commands: `init` signs you in with a browser and, optionally, allowlists a repo in one step; `status` reads back what's stored, locally, with no network call; `git-proxy` is the long-running server that devcontainers route their git traffic through. Authentication is OAuth2 authorization-code with PKCE against Entra ID, using Microsoft's well-known Visual Studio public client ID — there is no app-registration step for you to do.

- **Sign in once, reuse everywhere:** `init` stores a refresh token in the global CLI config; every consumer mints a fresh access token from it on demand.
- **No token in the container:** `git-proxy` injects a Bearer token into each git smart-HTTP request server-side, so a devcontainer's git config never contains a credential.
- **Repo-scoped by design:** the proxy refuses to start without an explicit `--allow` allowlist and rejects any repo outside it.

## When to use it

Use `pks ado` when a workflow needs to clone or push to an Azure DevOps repo from a pks-managed environment — a local Docker container or a remote VM reached via `pks claude` — without embedding a personal access token or Bearer token in that (possibly untrusted) environment. Run `pks ado init` once to authenticate, then start `pks ado git-proxy` wherever the git traffic actually needs to flow, pointing the container's git config at it.

## Prerequisites

- **An Azure DevOps organization membership** on the Microsoft account you sign in with — `init` fails if the account belongs to no ADO organization.
- **A browser reachable from the machine running `pks ado init`.** In a devcontainer, set `BROWSER` so the login URL opens on the host instead of inside the container.
- **Port 7878 free** on whichever host will run `pks ado git-proxy` — the port is fixed and not configurable.

## Synopsis

```text
pks ado <command> [options]
```

```text
init         Log in via OAuth2/PKCE, or register a repo into the git-proxy allowlist
status       Show stored Azure DevOps auth state (local, read-only)
git-proxy    Run the git smart-HTTP credential-injection proxy on port 7878
```

### Global environment variables

| Variable | Default | Purpose |
|---|---|---|
| `BROWSER` | OS default opener | Executable `pks ado init` launches with the auth URL instead of `xdg-open`/`open`/`ShellExecute`. This is how a devcontainer pops the browser on the host. |

## pks ado init

Runs the OAuth2/PKCE browser login flow and stores the resulting refresh token, selected organization, and profile in the global CLI config. Pass a git URL instead of running it bare and `init` authenticates first if needed, then registers that repo into the git-proxy allowlist. The authorize URL is always printed to the terminal as a clickable fallback, and the command also tries to auto-open a browser, honoring `BROWSER` first. With more than one ADO organization on the account it prompts you to choose; with exactly one, it picks it automatically. Already-authenticated runs are a no-op unless `--force` is passed.

| Argument | Required | Description |
|---|---|---|
| `git-url` | no | ADO git URL to register in the proxy allowlist, e.g. `https://Delegate@dev.azure.com/Org/Project/_git/Repo`. Any userinfo prefix is stripped when parsed. If given, `init` behaves as a combined auth-and-register step instead of a bare login. |

| Flag | Description |
|---|---|
| `-f, --force` | Force re-authentication even if already authenticated. Clears stored credentials first. |
| `-t, --tenant <text>` | Azure AD tenant ID. Defaults to `common` — omit it and the interactive prompt asks for an email or tenant instead. |
| `-v, --verbose` | Enable verbose output. |

```bash
pks ado init
```

Interactive login: prompts for email or tenant (press Enter for the `common` tenant), opens a browser, then prompts for an organization if the account belongs to more than one.

```bash
pks ado init --force
```

Clears any existing credentials and re-runs the full login flow.

```bash
pks ado init https://dev.azure.com/Org/Project/_git/Repo
```

Authenticates if needed, then adds `Org/Project/Repo` to the git-proxy allowlist so it's available to `pks claude`'s repo selection.

> **Note.** If the tenant prompt input parses as a GUID it's used directly as the tenant ID; any other non-empty input is treated as an email and passed as `login_hint` against the `common` tenant, and Entra routes to the correct tenant from there.

## pks ado status

Reads the stored Azure DevOps credentials with no network call and prints a table of authenticated user, email, organization, org URL, when auth was created, when the token was last refreshed, and whether a refresh token is present. This is a local, read-only diagnostic — it does not validate the token against Azure DevOps.

| Flag | Description |
|---|---|
| `-v, --verbose` | Enable verbose output. |

```bash
pks ado status
```

If you've never authenticated, this prints `Not authenticated with Azure DevOps.` and a hint to run `pks ado init`. Exit code is `0` either way — read the printed message, not the exit code.

> **Note.** `Refresh Token: Missing` can appear alongside otherwise-populated fields if the stored credential file has an empty refresh-token string. That account is unusable for token minting until you run `pks ado init --force`.

## pks ado git-proxy

Starts a long-running HTTP reverse proxy on `0.0.0.0:7878` in front of `dev.azure.com`. For each request it checks that the request is actually git smart-HTTP (by `service` query parameter or `Content-Type`), parses `org/project/repo` out of the path, checks that triple against `--allow` (exact match, case-insensitive), mints or refreshes a Bearer access token server-side, strips whatever `Authorization` header the client sent, injects the fresh token, forwards the request upstream, and streams the response back. Point a devcontainer's git config at it — `git config --global url.'http://172.17.0.1:7878/'.insteadOf 'https://dev.azure.com/'` — and the container never has to hold a token.

| Flag | Required | Description |
|---|---|---|
| `--allow <org/project/repo>` | yes | Repo to allow through the proxy, repeatable. Example: `--allow Delegate/MyProject/my-repo`. Any repo not listed — or omitting the flag entirely — is refused. |
| `-v, --verbose` | no | Enable verbose output. |

```bash
pks ado git-proxy --allow Org/Project/Repo
```

Starts the proxy on `0.0.0.0:7878` restricted to that one repo.

```bash
pks ado git-proxy --allow Org/ProjA/repo1 --allow Org/ProjB/repo2
```

Repeat `--allow` to permit multiple repos through the same proxy instance.

> **Availability.** `git-proxy` binds to every interface on a fixed port with no auth beyond the allowlist, and it logs to a fixed unstructured file rather than stdout. It is documented `beta` for that reason — treat it as less hardened than `init`/`status`.

## Troubleshooting

- **`No ADO credentials found` (exit 1).** `git-proxy` needs either `~/.pks-cli/ado-credentials.json` (the file copied to a VM by `pks claude`) or a locally authenticated session from `pks ado init`. It does not offer an interactive login itself — run `pks ado init` on the machine first, or make sure the credentials file was copied.
- **`No repos in allowlist` (exit 1).** `--allow` was omitted or empty. There is no "allow everything" mode — pass at least one `org/project/repo`.
- **`403` with the repo name echoed back.** The requested path didn't match any `--allow` entry exactly, case-insensitively. Check multi-segment project names carefully — the match is against the full path, not a prefix.
- **`400 Only git smart HTTP protocol accepted`.** Something other than a git client hit the proxy — a browser, or a dumb-HTTP git client. Confirm the client is using smart HTTP.
- **`502 Failed to acquire ADO access token`.** Both credential sources (the copied credentials file and the local `settings.json` entry) failed to refresh for that request. The proxy process itself keeps running — retry once the underlying auth is fixed, or re-run `pks ado init --force`.
- **Diagnosing `git-proxy` behavior.** Kestrel logging is disabled; every token-acquisition attempt, success or failure, is appended to `/tmp/pks-ado-proxy.log` on the host running the proxy. Check that file, not Aspire or stdout.
- **`Authentication timed out.` from `init`.** The loopback callback listener has a timeout and the browser flow wasn't completed in time. Re-run `pks ado init` and complete the browser step promptly.
- **`Cannot parse ADO git URL` from `init`.** The URL passed to `init` is missing an `/_git/` segment or isn't otherwise a valid ADO git URL.

## See also

- [pks claude CLI reference](/tools/pks/claude) — spawns the devcontainer or VM that routes its git traffic through `git-proxy`.
- [pks git CLI reference](/tools/pks/git) — the separate `pks git askpass` credential helper, registered under its own branch rather than under `ado`.
