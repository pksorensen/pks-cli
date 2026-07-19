---
title: "pks github"
description: "Authenticate pks with GitHub and run a self-hosted Actions runner that builds a fresh devcontainer for every queued workflow job."
tags: [concept, github, runner, ci]
category: infrastructure
icon: github
status: stable
author: Poul Kjeldager
component: pks
usage: "pks github <command> [options]"
examples:
  - command: "pks github init"
    description: "Sign in to GitHub with the device-code flow"
  - command: "pks github status --verbose"
    description: "Show the stored token type, scopes, and expiry"
  - command: "pks github runner register owner/repo"
    description: "Register a repository for the runner daemon"
  - command: "pks github runner start"
    description: "Poll registered repos and run jobs in devcontainers"
---

`pks github` is the GitHub half of the pks CLI: it holds your GitHub credentials, and it runs a self-hosted GitHub Actions runner on a machine you control.

## Overview

`pks github` is a command group with two jobs. It authenticates pks against GitHub — either through GitHub's OAuth device-code flow against the first-party Agentics Live GitHub App, or with a personal access token you supply. It then uses that credential to run a runner daemon that picks up queued workflow jobs and executes each one inside a devcontainer.

- **Authentication.** `pks github init` and `pks github status` manage the stored token and the app installation on your repositories.
- **Runner registration.** `pks github runner register`, `unregister`, `list`, and `prune` decide which repositories the daemon watches.
- **Runner execution.** `pks github runner start` polls those repositories and builds a container per job, in the foreground, until you stop it.

## What you get

- **A device-code login.** No secret to paste. `pks github init` prints a code, you approve it in a browser, and the token lands in the pks credential store under `~/.pks-cli/`.
- **Devcontainer CI environments.** Each queued job runs in a container the daemon builds through the `devcontainer` CLI, so the CI environment comes from the same definition you develop in.
- **Git push from inside the job.** The daemon runs a local git-credential server over a Unix socket. Containers authenticate through that socket, so the GitHub token never has to be written into the container's filesystem.
- **Multiple repositories from one daemon.** Registrations are stored per repository with their own labels, and one `pks github runner start` polls every enabled registration.
- **Registration hygiene.** `pks github runner list` and `pks github runner prune` show and clean up the duplicate registrations that repeated or aborted `register` runs leave behind.

## How it fits together

The order is fixed. Authentication comes first, because everything else needs a token: `pks github init` runs the device-code flow and, when you pass a repository URL, walks you through installing the Agentics Live GitHub App on that repository. Then you register each repository you want built, with `pks github runner register owner/repo`. Registration is where GitHub's own constraint bites — it verifies that your identity has Admin permission on the repository, because GitHub's just-in-time runner registration tokens require admin-level access.

Then you start the daemon. `pks github runner start` re-checks the token (refreshing it, or falling back to an inline device-code login), checks that Docker and the `devcontainer` CLI are present, starts the git-credential socket, and begins polling. It blocks in the foreground with a live status table and writes a fuller log to `~/.pks-cli/runner.log`. Ctrl+C shuts it down gracefully, after active jobs finish.

- **At a glance — the token:** `init` stores it, `status` inspects it, `runner start` refreshes it.
- **At a glance — the work:** `runner register` selects the repositories, `runner start` executes their jobs.

## Commands

`init` · `status` · `runner register` · `runner unregister` · `runner list` · `runner start` · `runner status` · `runner stop` · `runner prune`

Every argument, flag, and default is listed on the [pks github reference](/tools/pks/github/reference).

> **Note.** The runner daemon keeps its state in memory, inside the process that runs `pks github runner start`. Because each `pks` invocation is a separate operating-system process, `pks github runner status` and `pks github runner stop` run from another terminal cannot see or stop that daemon. Use Ctrl+C in the terminal running `start`.

## Next steps

- [Authenticate pks with GitHub](/tools/pks/github/init) — run the device-code flow and grant the app access to a repository
- [Check GitHub authentication status](/tools/pks/github/status) — confirm the stored token is valid before a job needs to push
- [Self-hosted devcontainer runner](/tools/pks/github/runner) — the daemon's lifecycle, prerequisites, and operating model
- [Register a repository with the runner](/tools/pks/github/runner/register) — the admin-permission and app-access checks in detail
- [Run the runner daemon](/tools/pks/github/runner/start) — pre-flight checks, concurrency, and logs
- [pks github reference](/tools/pks/github/reference) — the complete command and flag surface
