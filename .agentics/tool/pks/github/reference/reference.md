---
title: "pks github reference"
description: "Complete command, argument, and flag reference for the pks github group — authentication, token status, and the devcontainer runner daemon lifecycle."
tags: [reference, github, runner, cli]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks github <command> [options]"
examples:
  - command: "pks github init https://github.com/owner/repo"
    description: "Authenticate and grant the app access to a repo"
  - command: "pks github status --verbose"
    description: "Show token type, scopes, and expiry"
  - command: "pks github runner register owner/repo --labels gpu"
    description: "Register a repository under a custom label"
  - command: "pks github runner start --max-jobs 2"
    description: "Run the daemon with two concurrent jobs"
  - command: "pks github runner prune"
    description: "Delete duplicate registrations, keeping the newest"
---

`pks github` is the GitHub command group of the pks CLI. It authenticates pks against GitHub through the OAuth device-code flow or a personal access token, and it manages a self-hosted GitHub Actions runner that executes each queued workflow job inside a devcontainer.

`pks github` is a branch command with no action of its own. Every command below is reachable as `pks github …`; the runner commands are reachable as `pks github runner …`.

## Synopsis

```text
pks github <command> [options]
```

```text
init [repoUrl]           Authenticate with GitHub, optionally granting app access to a repo
status                   Report whether a valid GitHub token is stored
runner register <REPO>   Register a repository for the runner daemon to poll
runner unregister <REPO> Remove a repository's runner registration
runner list              List all stored runner registrations
runner start [REPO]      Run the runner daemon in the foreground
runner status            Show the in-process daemon's summary and active jobs
runner stop              Request a graceful shutdown of the in-process daemon
runner prune             Delete duplicate registrations, keeping the newest per repo
```

### Global options

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. Accepted by every command in the group. |

### Files and state

No environment variables configure this group. The GitHub App client ID, app slug, requested scopes, and the GitHub device-code and token endpoints are compiled into the CLI. State lives on disk:

| Setting | Value |
|---|---|
| Credential store | `~/.pks-cli/` |
| Runner registrations | `~/.pks-cli/runners.json` |
| Daemon log | `~/.pks-cli/runner.log` |

### Authentication model

Authentication uses GitHub's OAuth device-code flow against the first-party Agentics Live GitHub App — client ID `Iv23liFv43zosMUb8t9y`, app slug `agentics-live`, in the si14agents organization — or a personal access token you supply. Default requested scopes are `repo`, `user:email`, and `write:packages`. `pks github runner start` refreshes the stored token on startup and falls back to an inline device-code login if the refresh fails.

## init

Authenticates pks with GitHub. With no arguments it runs the device-code flow, printing a user code and verification URL and polling GitHub every five seconds — backing off on `slow_down` — until the code is approved or expires. With `--token` it stores the supplied personal access token and exits, running no flow and no repository check.

When `repoUrl` is given, the command additionally checks whether the GitHub App can see that repository. If the app is not installed for the owner, it opens the installation page in a browser and waits for Enter. If the app is installed but scoped to a repository list that excludes the target, it opens the installation's settings page, waits for Enter, and re-checks. Both branches block on keyboard input.

| Flag | Default | Description |
|---|---|---|
| `-f`, `--force` | `false` | Force re-authentication even when already authenticated. |
| `-t`, `--token <token>` | — | Store a personal access token directly instead of using the device-code flow. |
| `--client-id <id>` | Agentics Live app | GitHub App or OAuth App client ID to authenticate against. |
| `--app-slug <slug>` | `agentics-live` | GitHub App slug used to build the installation URL. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

`repoUrl` is optional. Accepted forms are `https://github.com/owner/repo`, `http://github.com/owner/repo`, `github.com/owner/repo`, and `git@github.com:owner/repo`, with or without a trailing `.git`. Any other form fails with a parse error.

```bash
pks github init https://github.com/owner/repo --force
```

## status

Reports whether pks holds a valid GitHub token, which determines whether the runner daemon announces the `git:push` capability. Exits 0 when authenticated and 1 when not. It checks the token only — it does not check whether the GitHub App is installed on any specific repository.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Add a table with token type, scopes, created and expiry dates, and validity. |

Verbose output distinguishes a personal access token from an OAuth token by the `ghp_` prefix.

## runner register

Registers a repository for the runner daemon to poll. The command runs the device-code login inline if pks is not authenticated, verifies that the GitHub App can access the repository — polling every five seconds for up to five minutes while access is granted — and verifies that the authenticated identity holds Admin permission on the repository. Admin is required because GitHub's just-in-time runner registration tokens need admin-level access.

If a registration already exists for the same repository, the command asks whether to replace it, defaulting to No. Declining exits 0 without changes; confirming deletes every existing registration for that repository before writing the new one.

| Flag | Default | Description |
|---|---|---|
| `--labels <LABELS>` | `devcontainer-runner` | Comma-separated runner labels, replacing the default. |
| `-v`, `--verbose` | `false` | Print raw repository and permission API response detail during the access check. |

`REPO` is required and must be in `owner/repo` format.

```bash
pks github runner register owner/repo --labels gpu,large
```

## runner unregister

Removes a repository's runner registration so the daemon stops polling it. Prompts for confirmation, defaulting to Yes. Exits 1 with a not-found message when no registration matches.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |

`REPO` is required and must be in `owner/repo` format. The repository string is what matches, not the truncated ID shown by `runner list`.

## runner list

Lists all stored runner registrations with their ID, repository, labels, registration timestamp, and enabled state. IDs are truncated in the table.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |

The command accepts a positional repository argument inherited from the shared runner settings, but ignores it. `list` always shows every registration.

## runner start

Runs the runner daemon in the foreground. Startup performs pre-flight checks in order: GitHub authentication, with token refresh and an inline device-code login as fallback; Docker availability; and `devcontainer` CLI availability. Missing Docker or a missing devcontainer CLI ends the command with exit 1, as does the absence of any enabled registration.

The daemon then starts a local git-credential server on a Unix socket, scoped to the first enabled registration's ID, so spawned containers can `git push` using the runner's token without that token being written into the container's filesystem. It polls every enabled registration — or one repository, when `REPO` is given — for queued workflow jobs, and builds a devcontainer per job.

`start` blocks with a live status table until Ctrl+C, which triggers a graceful shutdown that waits for active jobs to finish. Routine polling events go only to `~/.pks-cli/runner.log`, never to the table.

| Flag | Default | Description |
|---|---|---|
| `--max-jobs <MAX_JOBS>` | `1` | Maximum number of concurrent jobs. The value is persisted into the runner configuration. |
| `-v`, `--verbose` | `false` | Enable verbose output, including the full exception on failure. |

`REPO` is optional and restricts polling to a single `owner/repo` registration.

## runner status

Displays the daemon's summary — running state, start time, jobs completed and failed — together with any active jobs.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Also show a table of last poll times per repository. |

> **Note.** Daemon state is held in memory in the process that ran `runner start`. Because every `pks` invocation is a new process, this command inspects a daemon instance that was never started and reports no daemon and no jobs, even while `runner start` is running in another terminal.

## runner stop

Requests a graceful shutdown of the runner daemon, letting active jobs finish first.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |

Subject to the same per-process state limitation as `runner status`: invoked from a terminal other than the one running `start`, it reports that no daemon is running and stops nothing. Use Ctrl+C in the daemon's own terminal.

## runner prune

Removes duplicate runner registrations for the same repository, keeping the most recent one per repository. Prompts for confirmation, defaulting to Yes. Deletion is permanent — restoring a pruned registration means running `runner register` again.

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |

The command exits 0 without changes when there are fewer than two registrations, or when no two registrations name the same repository.

## See also

- [pks github](/tools/pks/github) — what the group is for and how the commands fit together
- [Authenticate pks with GitHub](/tools/pks/github/init) — the device-code and token walkthroughs
- [Check GitHub authentication status](/tools/pks/github/status) — token diagnostics
- [Self-hosted devcontainer runner](/tools/pks/github/runner) — the runner's operating model and defaults
- [Register a repository with the runner](/tools/pks/github/runner/register) — the permission checks in detail
- [Run the runner daemon](/tools/pks/github/runner/start) — pre-flight checks, logs, and shutdown
