---
title: "Self-hosted devcontainer runner"
description: "Register the repositories a self-hosted runner watches, then run the daemon that builds an ephemeral devcontainer for each queued GitHub Actions job."
tags: [concept, github, runner, ci, devcontainer]
category: infrastructure
icon: play
status: stable
author: Poul Kjeldager
component: pks
usage: "pks github runner <command> [options]"
examples:
  - command: "pks github runner register owner/repo"
    description: "Register a repository for the daemon to poll"
  - command: "pks github runner list"
    description: "Show every registration and its labels"
  - command: "pks github runner start"
    description: "Poll all enabled repos and run queued jobs"
  - command: "pks github runner prune"
    description: "Delete duplicate registrations, keeping the newest"
---

`pks github runner` runs GitHub Actions jobs on your own machine, one devcontainer per job, instead of on a GitHub-hosted or permanently installed self-hosted runner.

## Overview

The runner is a foreground daemon plus a small local registration store. Registrations name the repositories to poll and the labels to claim jobs under; the daemon polls GitHub for queued workflow jobs on those repositories and builds a container through the `devcontainer` CLI for each one. Nothing is installed into the repository, and no runner service is left behind — when you stop the daemon, the machine stops taking work.

- **Registration commands.** `register`, `unregister`, `list`, and `prune` operate purely on the local store, with no long-running process.
- **Daemon commands.** `start` runs the daemon; `status` and `stop` inspect and signal it.
- **Default label.** New registrations claim jobs labelled `devcontainer-runner` unless `--labels` says otherwise.

## What you get

- **Ephemeral job environments.** Each job gets a container built through the `devcontainer` CLI, then goes away.
- **A credential socket, not a copied token.** `start` runs a git-credential server over a Unix socket, scoped to the first enabled registration, so containers can `git push` without the GitHub token being written into them.
- **Per-repository labels.** Registrations carry their own label list, so different repositories can target different runner pools from one daemon.
- **A concurrency setting that sticks.** `--max-jobs` on `start` is persisted into the runner configuration, not applied only to that run.
- **A log file alongside the live table.** Routine polling events go to `~/.pks-cli/runner.log` and stay out of the on-screen table.

## How it fits together

Registration is the gate. `pks github runner register owner/repo` refuses to store anything unless the authenticated identity has Admin permission on the repository, because GitHub issues just-in-time runner registration tokens only to admins. It also verifies that the Agentics Live GitHub App can see the repository, polling for up to five minutes while you grant access. What it writes is a local record — GitHub is not told about a runner until a job is claimed.

`pks github runner start` is the only command that talks to GitHub continuously. It refreshes the stored token, verifies Docker and the `devcontainer` CLI, starts the credential socket, and then polls every enabled registration — or a single repository, if you name one. It holds the terminal with a live status table until Ctrl+C, which triggers a graceful shutdown that waits for active jobs to finish.

- **At a glance — local only:** `register`, `unregister`, `list`, `prune`.
- **At a glance — talks to GitHub:** `start`.

## Commands

`register` · `unregister` · `list` · `start` · `status` · `stop` · `prune`

Every argument, flag, and default is listed on the [pks github reference](/tools/pks/github/reference).

> **Note.** `status` and `stop` read and signal an in-memory daemon inside their own process. Invoked from a second terminal, they report a daemon that was never started, and they change nothing. Stop a running daemon with Ctrl+C in its own terminal.

## Defaults

| Setting | Value |
|---|---|
| Runner labels | `devcontainer-runner` |
| Concurrent jobs | `1` |
| Registration store | `~/.pks-cli/runners.json` |
| Daemon log | `~/.pks-cli/runner.log` |

No environment variables configure this group. The GitHub App client ID, app slug, scopes, and endpoints are compiled into the CLI, and the rest of the state lives in the files above.

## Next steps

- [Register a repository with the runner](/tools/pks/github/runner/register) — permission checks, labels, and duplicate handling
- [Run the runner daemon](/tools/pks/github/runner/start) — pre-flight checks, concurrency, logs, and shutdown
- [Authenticate pks with GitHub](/tools/pks/github/init) — the credential every runner command depends on
- [Check GitHub authentication status](/tools/pks/github/status) — verify that credential before starting
- [pks github reference](/tools/pks/github/reference) — the complete command and flag surface
