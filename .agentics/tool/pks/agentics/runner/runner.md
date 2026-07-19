---
title: "pks agentics runner"
description: "Register a machine as a self-hosted Agentics runner, start the job-polling daemon, hand it off to an SSH target, and clean up its containers."
tags: [runner, agents, devcontainer, alp]
category: infrastructure
icon: radio
status: beta
author: Poul Kjeldager
component: pks
usage: "pks agentics runner <command> [options]"
examples:
  - command: "pks agentics runner register myorg/myproject"
    description: "Register this machine for one owner/project"
  - command: "pks agentics runner start"
    description: "Start the daemon on the first saved registration"
  - command: "pks agentics runner start --project myorg/myproject"
    description: "Start for a project, auto-registering if needed"
  - command: "pks agentics runner cleanup --dry-run"
    description: "List orphaned containers without removing them"
  - command: "pks agentics runner status hetzner"
    description: "Check a runner handed off to an SSH target"
---

A runner is the process that claims Assembly Line Platform jobs and executes them on your hardware. `pks agentics runner` covers its whole life: registering the machine against one owner/project, starting the polling daemon, moving it to a remote SSH target when the local machine cannot run Docker, and removing the containers it leaves behind.

## Overview

`pks agentics runner start` polls `{server}/api/owners/{owner}/projects/{project}/runners/jobs` on an interval. Each cycle it recomputes and advertises a capability set — `alp_operator`, `chat-session:v1`, `devcontainer-session:v1`, and `chat-llm:v1` — from what the machine can do right now, then dispatches whatever job the server hands back.

- **Register first.** `runner register owner/project` obtains the per-runner bearer token and saves it locally.
- **Then start.** `runner start` uses the first saved registration, or the one named by `--project`.
- **Inspect and stop.** After an SSH handoff, `runner status`, `runner logs`, and `runner stop` drive the remote tmux session.

## What you get

- **Capability-driven dispatch.** The runner never receives work it cannot execute. Capabilities are recomputed every poll from Docker availability and the configured chat backend.
- **Several job types.** `git_push` and `git_distribute` run in the runner process. `chat_llm` runs on a backgrounded task so an open chat tab does not block the poll loop. Devcontainer work runs the spawn lifecycle: claim, in progress, container spawn, agent exec, completed.
- **Degraded mode.** Docker availability is probed before the GitHub preflight and before the git credential socket is created, so a machine without Docker starts cleanly instead of hanging.
- **SSH handoff.** On an interactive console with Docker missing and SSH targets registered, the runner offers to run on a remote target instead.
- **Container hygiene.** `runner cleanup` finds containers labelled `pks.agentics.fingerprint` that no live runner process owns.

## How it fits together

Registration binds this machine to one owner/project and yields a bearer token. The daemon authenticates with that token, polls for jobs, and reports state transitions back to the server. For projects hosted on github.com, `runner register` also walks you through a GitHub device-code login so the runner can clone without prompting, then verifies that the resulting token actually reaches that specific repository.

Docker decides the shape of the run. With Docker, the runner advertises the spawn capabilities and executes devcontainer jobs. Without it, the runner advertises the reduced set and leaves devcontainer jobs queued for another runner.

> **Note.** If Docker becomes available mid-run, spawn capabilities are not re-enabled. A runner that started degraded stays degraded for its whole process lifetime — restart it to pick up spawn mode.

## Commands

`register` · `start` · `cleanup` · `status` · `logs` · `stop` · `claude-login`

`status`, `logs`, `stop`, and `claude-login` operate on a runner that was handed off to a registered SSH target. They do nothing for a runner running locally in the foreground.

## Next steps

- [Register a machine as an Agentics runner](/tools/pks/agentics/runner/register) — the one-time registration and GitHub access check
- [Start the Agentics runner daemon](/tools/pks/agentics/runner/start) — poll loop, capabilities, degraded mode, chat backends
- [Remove orphaned runner containers](/tools/pks/agentics/runner/cleanup) — what counts as an orphan and how it is decided
- [Check a handed-off runner](/tools/pks/agentics/runner/status) — remote tmux status plus the credential-volume warning
- [Read a handed-off runner's logs](/tools/pks/agentics/runner/logs) — tail the remote tmux session's output
- [Stop a handed-off runner](/tools/pks/agentics/runner/stop) — shut down the remote session cleanly
- [Log in to Claude on an SSH target](/tools/pks/agentics/runner/claude-login) — populate the credentials volume headless spawns need
- [pks agentics CLI reference](/tools/pks/agentics/cli-reference) — every flag and environment variable in one table
