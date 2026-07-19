---
title: "Run the runner daemon"
description: "Run the pks runner daemon in the foreground: pre-flight checks, a git-credential socket for containers, and one devcontainer per queued Actions job."
tags: [how-to, github, runner, devcontainer, daemon]
category: infrastructure
icon: play
status: stable
author: Poul Kjeldager
component: pks
usage: "pks github runner start [REPO] [options]"
examples:
  - command: "pks github runner start"
    description: "Poll every enabled registration, one job at a time"
  - command: "pks github runner start owner/repo"
    description: "Poll only the registration for owner/repo"
  - command: "pks github runner start --max-jobs 3"
    description: "Allow three concurrent jobs and persist the setting"
  - command: "pks github runner start --verbose"
    description: "Add verbose output and full exception detail"
---

`pks github runner start` turns the current machine into a GitHub Actions runner. It polls your registered repositories for queued jobs and builds a devcontainer for each one, holding the terminal until you press Ctrl+C.

## 1. Prerequisites

- **Docker, installed and running.** The daemon checks it at startup and exits 1 if `docker version` fails.
- **The devcontainer CLI**, installed globally with `npm install -g @devcontainers/cli`. Startup exits 1 if `devcontainer --version` fails.
- **At least one enabled registration**, from [`pks github runner register`](/tools/pks/github/runner/register). Without one the daemon exits 1.
- **A GitHub token.** The daemon refreshes the stored token automatically, and falls back to an inline device-code login if the refresh fails.

## 2. Start the daemon

```bash
pks github runner start
```

Startup runs the pre-flight checks in order — GitHub authentication, Docker, the `devcontainer` CLI — then starts a local git-credential server on a Unix socket. That socket is how containers authenticate `git push` using the runner's own token, without the token being written into the container's filesystem. The daemon then begins polling every enabled registration.

## 3. Limit the daemon to one repository

```bash
pks github runner start owner/repo
```

The optional positional argument restricts polling to that single registration. Without it, every enabled registration is polled.

## 4. Set concurrency

```bash
pks github runner start --max-jobs 3
```

The default is one job at a time. When you pass `--max-jobs`, the value is saved into the runner configuration, so it applies to later runs as well until you change it again.

## 5. Read the output

The daemon renders a live status table in the terminal for as long as it runs. Routine polling events are written only to the log file, to keep the table readable:

```text
~/.pks-cli/runner.log
```

Add `--verbose` for more detail on screen, including the full exception text when a job or the daemon itself fails.

## 6. Verify

Queue a workflow job in a registered repository whose `runs-on` labels match the registration. Within one polling cycle the job appears in the daemon's table, and a container is built for it. `~/.pks-cli/runner.log` records the same sequence.

## 7. Stop the daemon

Press Ctrl+C in the terminal running `start`. Shutdown is graceful: the daemon stops claiming new work and waits for active jobs to finish before exiting.

> **Note.** `pks github runner stop` does not stop a daemon started in another terminal. Daemon state lives in memory inside a single process, and every `pks` invocation is a new process, so a `stop` from a second terminal signals a daemon that was never started. The same applies to `pks github runner status`, which reports no daemon and no active jobs while `start` is genuinely running elsewhere.

## 8. Options

| Flag | Default | Description |
|---|---|---|
| `--max-jobs <MAX_JOBS>` | `1` | Maximum number of concurrent jobs. The value is persisted into the runner configuration. |
| `-v`, `--verbose` | `false` | Enable verbose output, including the full exception on failure. |

The positional `REPO` argument is optional and restricts polling to one `owner/repo` registration.

## 9. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Exit 1 with a Docker message. | Docker is not installed or the daemon is not running. Start Docker and re-run. |
| Exit 1 with a devcontainer CLI message. | The CLI is missing. Run `npm install -g @devcontainers/cli`. |
| `No enabled runner registrations found`. | Nothing is registered, or every registration is disabled. Run `pks github runner register owner/repo`. |
| The daemon runs, but jobs stay queued on GitHub. | The workflow's `runs-on` labels do not match the registration's labels. Check them with `pks github runner list`. |
| `pks github runner status` reports nothing while jobs are running. | Expected. Daemon state is per process — read the live table in the daemon's own terminal, or `~/.pks-cli/runner.log`. |
| A job fails to push back to the repository. | The stored token lacks access. Check it with `pks github status --verbose`, then re-authenticate. |
| The terminal is blocked. | `start` runs in the foreground by design. Run it in its own terminal, or under a terminal multiplexer. |

## 10. Next steps

- [Register a repository with the runner](/tools/pks/github/runner/register) — add or relabel a repository the daemon polls
- [Self-hosted devcontainer runner](/tools/pks/github/runner) — the daemon's operating model and defaults
- [Check GitHub authentication status](/tools/pks/github/status) — diagnose token problems behind push failures
- [pks github reference](/tools/pks/github/reference) — `status`, `stop`, `list`, and `prune` in full
