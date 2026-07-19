---
title: "pks agentics"
description: "Log in to agentics.dk, turn a machine into a self-hosted Assembly Line runner, and file tasks onto an assembly line from a CI/CD pipeline."
tags: [cli, agents, runner, alp]
category: infrastructure
icon: bot
status: stable
author: Poul Kjeldager
component: pks
usage: "pks agentics <command> [options]"
examples:
  - command: "pks agentics init"
    description: "Log in to agentics.dk once per machine"
  - command: "pks agentics runner register myorg/myproject"
    description: "Register this machine as a runner for a project"
  - command: "pks agentics runner start"
    description: "Start the job-polling runner daemon"
  - command: "pks agentics runner cleanup --dry-run"
    description: "List orphaned runner containers, remove nothing"
  - command: "pks agentics task submit --assembly-line-url https://agentics.dk/p/owner/project/assembly-lines/stage-id --title \"Fix failing tests\""
    description: "File a task onto an assembly line from CI"
---

`pks agentics` connects a machine to agentics.dk, the Assembly Line Platform (ALP) where AI coding agents pick up work. It covers three jobs: authenticating you as a user, running the daemon that executes ALP jobs on your own hardware, and submitting tasks onto an assembly line from a pipeline.

## Overview

An assembly line holds tasks. A runner is the process that claims those tasks and does the work — spawning a devcontainer and running a coding agent inside it, pushing or distributing git branches, or bridging a chat session to a language-model backend.

- **Log in once** with `pks agentics init`, an OAuth 2.0 device-authorization grant (RFC 8628) against Keycloak.
- **Register and start a runner** with `pks agentics runner register` and `pks agentics runner start`, turning a Docker-capable machine into a job executor for one owner/project.
- **File work** with `pks agentics task submit`, typically from a failing GitHub Actions job.

## What you get

- **Self-hosted execution.** Jobs run on your machine, in your network, against your clones. The server hands out work; the runner decides what it accepts by advertising a capability set on every poll cycle.
- **Devcontainer isolation.** When Docker is available, the runner spawns a devcontainer per job, runs the agent inside it, and reports the result back.
- **Graceful degradation.** Without Docker the runner still starts and keeps handling `git_push`, `git_distribute`, and chat jobs, leaving devcontainer work queued for a runner that can take it.
- **Remote handoff.** A Docker-less machine can hand its runner to a registered SSH target, then inspect it with `runner status`, `runner logs`, and `runner stop`.
- **CI/CD task filing.** Inside GitHub Actions, `task submit` enriches the task description with workflow, job, commit, actor, and the last 100 lines of the failed job's log.

## How it fits together

Authentication and execution are two separate paths that meet in `task submit`. `pks agentics init` stores a user token in `~/.pks-cli/agentics-auth.json`. `pks agentics runner register` stores a per-runner bearer token the server issues for one owner/project. `runner start` uses the runner token. `task submit` resolves a token from an ordered chain: an explicit `--token`, GitHub OIDC (OpenID Connect workload identity) inside GitHub Actions, the stored user token from `init` (refreshed in place if expired), then a matching runner registration's token as a back-compat fallback.

Execution is pull-based. The runner polls the project's jobs endpoint on an interval, advertises its capabilities, claims a job, marks it in progress, executes it, and reports completion. The server never connects into your machine.

- **Runner token** — scoped to one owner/project, created by `runner register`, used by the daemon.
- **User token** — machine-wide, created by `init`, used when nothing more specific applies.

## Commands

`init` · `runner register` · `runner start` · `runner cleanup` · `runner status` · `runner logs` · `runner stop` · `runner claude-login` · `task submit`

Every command in this group accepts `-v` / `--verbose`. The complete flag surface is on the reference page.

## Next steps

- [Log in to agentics.dk](/tools/pks/agentics/init) — the one-time device-code login and where credentials land
- [Run a self-hosted Agentics runner](/tools/pks/agentics/runner) — register, start, degrade, hand off, clean up
- [Submit a task to an assembly line](/tools/pks/agentics/task) — file work from GitHub Actions or a script
- [pks agentics CLI reference](/tools/pks/agentics/cli-reference) — every command, flag, and environment variable
