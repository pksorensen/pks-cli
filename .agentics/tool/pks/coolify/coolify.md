---
title: "pks coolify"
description: "Register Coolify instances so the self-hosted runner can auto-match a repo and branch to a deployable application and inject deploy credentials into CI jobs."
tags: [reference, coolify, runner, deployment]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks coolify <command> [url]"
examples:
  - command: "pks coolify register https://projects.si14agents.com"
    description: "Register a Coolify instance, prompting for the API token"
  - command: "pks coolify list"
    description: "List every registered instance"
  - command: "pks coolify status"
    description: "Check connectivity and show every project's resources"
  - command: "pks coolify status --debug"
    description: "Dump raw Coolify API JSON while debugging a match"
---

`pks coolify` manages the CLI's local registry of Coolify instances so the self-hosted runner daemon can auto-discover which Coolify application corresponds to a job's repository and branch.

## Overview

Coolify is a self-hosted PaaS. A CI job dispatched by the pks-cli runner daemon needs a webhook URL and a Bearer token to trigger a deploy on the matching Coolify application, but a workflow author shouldn't have to hardcode either one per repository. `pks coolify` closes that gap: register a Coolify instance once, and the runner daemon matches jobs to applications by `git_repository` and `git_branch` at dispatch time.

- **Host-scoped, per-user store.** Registrations live in `~/.pks-cli/coolify.json` on the machine running `pks-cli`, not per repository.
- **Verified at registration.** `pks coolify register` calls the instance's API before saving the token.
- **Doubles as a status dashboard.** `pks coolify status` walks every registered instance's projects and applications from the terminal, which is also the practical way to debug why a deploy didn't fire.

## What you get

- **A credential store the runner reads at job time.** `ICoolifyLookupService.FindAllAppsAsync` matches a job's GitHub owner/repo/branch against every registered instance's applications.
- **Automatic credential proxying, not env var injection.** A matched job gets `COOLIFY_APP_FQDN`/`COOLIFY_ENVIRONMENT` plus a scoped `PKS_TOKEN` in its environment, with no workflow changes; the job resolves the real webhook URL and Bearer token through a per-job Unix-socket proxy on the runner host, so the raw Coolify credential never enters the container.
- **A terminal health check.** `pks coolify status` shows every project's applications, services, and databases, color-coded by running state.
- **Environment-aware matching.** When a repo/branch matches more than one application, the default `COOLIFY_APP_FQDN`/`COOLIFY_ENVIRONMENT` env vars prefer Coolify's "production" environment (or the first match otherwise); a job can also resolve a specific environment's webhook explicitly through the runner's proxy.

## How it fits together

`pks coolify register` writes `{Id, Url, Token, RegisteredAt}` to `~/.pks-cli/coolify.json`, guarded by an in-process semaphore (not a file lock, so two `pks` processes on the same host could still race). The runner daemon (`RunnerContainerService`) reads that store at job dispatch time via `CoolifyLookupService.FindAllAppsAsync`, which matches the job's GitHub owner, repo, and branch against every registered instance's applications' `git_repository`/`git_branch` fields, across every environment. Every match is registered against the job in an in-memory `CoolifyTokenStore`, keyed by a per-job ID, but the job container itself only receives `COOLIFY_APP_FQDN`/`COOLIFY_ENVIRONMENT` (from the production-or-first match) plus a scoped `PKS_TOKEN` JWT and `PKS_TOKEN_URL=/var/run/pks-creds/creds.sock`. The instance's actual webhook URL — `{instanceUrl}/api/v1/deploy?uuid={uuid}` — and its Bearer token never enter the container's environment.

Instead, the `coolify-deploy` GitHub Action running inside the job calls `GET /coolify/token` and `POST /coolify/deploy` over that Unix socket, authenticated with `PKS_TOKEN`. The runner's `GitCredentialServer` resolves the requested `environment` query param against `CoolifyTokenStore` — a strict match or a 404, never a fallback to a different environment's app — and, for `/coolify/deploy`, proxies the deploy request server-side using the stored Bearer token, so the raw token stays on the runner host.

`pks coolify` itself never prints or exposes that per-app webhook URL — only the instance-level `register`/`list`/`status` commands. If the job requests an `environment` that has no registered match for it, `/coolify/token` returns 404 rather than falling back to a different environment's app; if no `environment` is requested at all, it falls back to the first app registered for the job.

- **At a glance, the write path:** you run `pks coolify register`, the instance's `GET /api/v1/applications` verifies the token, pks writes `~/.pks-cli/coolify.json`.
- **At a glance, the read path:** the runner daemon reads that same file at job dispatch, matches apps, and issues the job a scoped `PKS_TOKEN` that resolves the real webhook URL and Bearer token through the runner's own Unix-socket proxy — never through env vars.

## Commands

`register` · `list` · `status`

There is no default command on this branch, so a bare `pks coolify` prints the subcommand list.

| Command | What it does |
|---|---|
| `pks coolify register` | Register a Coolify instance URL and API token, verified against the instance before storing. |
| `pks coolify list` | List every registered instance from the local store — offline, no API calls. |
| `pks coolify status` | Check connectivity and show every registered instance's projects, applications, services, and databases. |

## register

Registers a Coolify instance URL and API token so the runner daemon can later match GitHub repos and branches to applications on it. `pks coolify register` normalizes the URL — adding `https://` if missing and stripping a trailing slash — then verifies the token by calling `GET /api/v1/applications` on the instance. Registration fails with exit code 1 (with a hint to check the URL and token) if the call errors, returns a non-2xx status, or the connection throws. On success the instance is upserted into `~/.pks-cli/coolify.json`: re-registering the same URL (matched case-insensitively and trailing-slash-insensitively) silently replaces the prior entry and token rather than creating a duplicate. A confirmation table (ID, URL, Registered timestamp in UTC) prints on success.

### Synopsis

```text
pks coolify register [URL]
```

| Argument | Required | Description |
|---|---|---|
| `URL` | no | Coolify instance URL, e.g. `https://projects.si14agents.com`. Prompted for interactively if omitted. |

| Flag | Description |
|---|---|
| `--token <TOKEN>` | Coolify API token. Prompted for via a masked (secret) input if not provided on the command line. |
| `-v`, `--verbose` | Show verbose output. Inherited from the shared settings; not read by this command's execution logic. |
| `--debug` | Dump raw API responses for debugging. Inherited from the shared settings; not read by this command's execution logic. |

```bash
pks coolify register https://projects.si14agents.com
```

Prompts for the API token interactively.

```bash
pks coolify register https://projects.si14agents.com --token <TOKEN>
```

Registers without an interactive prompt, for scripted setup.

> **Do not commit.** Passing `--token` on the command line puts the credential in shell history and the process list. Prefer the interactive masked prompt for manual use.

## list

Lists every Coolify instance currently registered in `~/.pks-cli/coolify.json` — a purely local, offline read with no API calls to Coolify itself. Shows a truncated 8-character instance ID, the instance URL, and the registration timestamp. Prints a hint to run `pks coolify register <url>` when nothing is registered.

### Synopsis

```text
pks coolify list [URL]
```

The `URL` argument is inherited from the shared settings but is not used — `list` always shows every registered instance.

| Flag | Description |
|---|---|
| `-v`, `--verbose` | Inherited from the shared settings; not read by this command. |
| `--debug` | Inherited from the shared settings; not read by this command. |

```bash
pks coolify list
```

> **Note.** `list` does not verify that instances are still reachable or that tokens are still valid. Use `pks coolify status` for a live connectivity check.

## status

For every registered instance, tests connectivity via `GET /api/v1/version`, then fetches all Coolify projects and their applications, services, and databases — walking projects → project detail → environments as needed, since Coolify's API doesn't always return resources in the flat project list. Each project's resources render in a table with a color-coded status (green for running, red for stopped or exited, yellow for anything else) and its FQDN.

This walks the same discovery path the runner's app-matching logic uses internally, so it's the practical way to debug why a repo/branch didn't match a Coolify application: read the tree it prints and check the application's project and environment placement.

### Synopsis

```text
pks coolify status [URL]
```

The `URL` argument is inherited from the shared settings but does not filter — `status` always iterates every registered instance.

| Flag | Description |
|---|---|
| `-v`, `--verbose` | Prints a summary line per instance: project count and total resource count across all projects. |
| `--debug` | For each instance, dumps the raw JSON of `GET /api/v1/projects`, then for every project the raw JSON of `GET /api/v1/projects/{uuid}`, and lists each environment's application UUID-to-name pairs. |

```bash
pks coolify status
```

```bash
pks coolify status --debug
```

Dump raw Coolify API JSON per project and environment when a deploy match isn't behaving as expected.

```bash
pks coolify status -v
```

Show per-instance project and resource counts alongside the resource tables.

> **Availability.** `status` catches per-instance failures — a failed connectivity check or a thrown `GET /api/v1/projects` call — and reports them inline as a red error line, then continues to the next instance rather than aborting.

## Troubleshooting

- **A repo/branch didn't trigger a deploy.** Run `pks coolify status --debug` and confirm the application's `git_repository`/`git_branch` fields, its project, and its environment match what the job carries. Resource discovery falls back through three response shapes because Coolify API versions differ in what they embed; if none yield resources, the project renders as "No resources" even if it has applications — that usually means an unhandled Coolify API version, not an empty project.
- **Registration fails immediately.** `pks coolify register` requires network access to `<url>/api/v1/applications` with a 10-second timeout; a slow or unreachable instance fails registration outright even when the token is valid.
- **`status` reports an instance as unreachable under load.** Both the connectivity check and the projects walk carry a 10-second `HttpClient` timeout per request. An instance under heavy load can time out and be reported unreachable even though it's technically up.
- **Two applications match one job.** The default `COOLIFY_APP_FQDN`/`COOLIFY_ENVIRONMENT` env vars prefer Coolify's "production" environment (or the first match if none is named "production"). To target a specific non-default environment, the job's `coolify-deploy` action must request it explicitly via the `environment` query param against the runner's Unix-socket proxy — an environment with no registered match there returns 404 rather than silently falling back to a different one.
- **Need to remove a registered instance.** There is no `pks coolify remove` or `unregister` command, even though the underlying store supports removal. Edit `~/.pks-cli/coolify.json` by hand, or re-register the same URL with a corrected token to overwrite the entry.

## Defaults

| Setting | Value |
|---|---|
| Credential store path | `~/.pks-cli/coolify.json` |
| Token storage | Plaintext JSON |
| Instance matching | Case-insensitive, trailing-slash-insensitive |
| Injected env vars | `COOLIFY_APP_FQDN`, `COOLIFY_ENVIRONMENT`, `PKS_TOKEN`, `PKS_TOKEN_URL` |
| Deploy webhook shape | `{instanceUrl}/api/v1/deploy?uuid={uuid}` (resolved by the runner's socket proxy; never in the container's env) |
| Concurrency guard | In-process semaphore, not a file lock |

No environment variable overrides the store path — the location is fixed on every operating system. On Windows it resolves to `C:\Users\<user>\.pks-cli\coolify.json`.

> **Note.** The API token is stored in plaintext. Treat `~/.pks-cli/coolify.json` as a secret file and protect it with filesystem permissions.

## Next steps

- [pks registry](/tools/pks/registry) — the equivalent flow for container-registry credentials on a runner host
- [pks](/tools/pks) — the full command surface and where `coolify` fits among the delivery-target commands
