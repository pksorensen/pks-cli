---
title: "pks agentics runner start"
description: "Run the long-lived Agentics daemon that polls a project for jobs, advertises its capabilities, and executes devcontainer, git, and chat work."
tags: [how-to, runner, daemon, devcontainer]
category: infrastructure
platform: [linux, macos, windows]
icon: play
status: beta
author: Poul Kjeldager
component: pks
usage: "pks agentics runner start [options]"
examples:
  - command: "pks agentics runner start"
    description: "Start on the first saved registration"
  - command: "pks agentics runner start --project myorg/myproject"
    description: "Start for a project, auto-registering if needed"
  - command: "pks agentics runner start --polling-interval 5"
    description: "Poll for jobs every five seconds"
  - command: "pks agentics runner start --chat-llm-backend-url http://localhost:11434/v1"
    description: "Enable chat-llm:v1 against a local backend"
---

`pks agentics runner start` is the daemon. It polls the project's job endpoint, advertises what this machine can do, claims work, executes it, and reports the outcome — running until you stop it. Everything an Assembly Line Platform task does on your hardware happens inside this process or a container it spawns.

## 1. Prerequisites

- **A saved registration.** Run [`pks agentics runner register`](/tools/pks/agentics/runner/register) first, or pass `--project owner/project` to auto-register. Without either, the command errors with "No runner registrations found".
- **Docker**, if you want devcontainer jobs. Without it the runner still starts, in degraded mode.
- **A vibecast binary**, for devcontainer sessions. Set `VIBECAST_BINARY` or pass `--vibecast-binary`; otherwise the runner falls back to `npx vibecast`.
- **A chat backend**, only if you want `chat-llm:v1` jobs. See step 4.

## 2. Start the daemon

```bash
pks agentics runner start
```

Without `--project`, the first saved registration is used. To pick a project explicitly — and register it on the spot if it is not registered yet:

```bash
pks agentics runner start --project myorg/myproject --server agentics.dk
```

The runner polls every ten seconds by default. Lower it for a tighter feedback loop:

```bash
pks agentics runner start --polling-interval 5
```

The process runs until Ctrl+C (SIGINT) or SIGTERM. On shutdown it drains in-flight jobs and cleans up active spawn-mode resources.

## 3. Understand what it advertises

Each poll cycle recomputes the capability set from Docker and spawn availability plus the configured chat backend:

- `alp_operator` — the runner can act as an Assembly Line operator.
- `devcontainer-session:v1` — it can spawn a devcontainer for a job.
- `chat-session:v1` — it can host a chat session.
- `chat-llm:v1` — it can serve chat-completions jobs, enabled by a configured chat backend.

Job types dispatched from the poll result are `git_push`, `git_distribute`, `chat_llm`, and the devcontainer spawn lifecycle reached through the operator and session capabilities. `chat_llm` runs on a backgrounded task so a long-lived chat tab never blocks the loop from claiming other work; those tasks are awaited, not abandoned, at shutdown.

## 4. Configure the chat backend

Declaring a backend URL enables the `chat-llm:v1` capability.

```bash
pks agentics runner start \
  --chat-llm-backend-url http://localhost:11434/v1 \
  --chat-llm-backend-key sk-... \
  --chat-llm-model gpt-5.5
```

The key is sent to your backend only — it is never sent to or stored by the agentics.dk server, and it is ignored when no backend URL is set. Without a backend URL, `--chat-llm-model` (or `CHAT_LLM_MODEL`) is resolved through the same provider factory `pks agent` uses, falling back to the persisted profile's default chat model and then `gpt-5.5`.

To watch every chat frame as it moves, add `--chat-llm-verbose`. Frame text can include the user's own chat content, so avoid it on a shared or recorded terminal.

## 5. Handle a machine without Docker

Docker availability is probed before the GitHub device-code preflight and before the git credential socket listener is created, specifically so a machine without Docker Desktop does not hang at startup. Instead the runner starts degraded: no `alp_operator`, `chat-session:v1`, or `devcontainer-session:v1`, devcontainer jobs left queued for another runner, and `git_push`, `git_distribute`, and `chat_llm` still working.

On an interactive console, if SSH targets are registered with `pks ssh register` or `pks vm init`, the degraded path offers to hand this project's runner off to a remote target. Accepting exits the local process immediately with code 0; the remote side is now the runner. Drive it afterwards with [`runner status`](/tools/pks/agentics/runner/status), [`runner logs`](/tools/pks/agentics/runner/logs), and [`runner stop`](/tools/pks/agentics/runner/stop).

> **Note.** If Docker comes back mid-run, spawn capabilities are not re-enabled. A degraded runner stays degraded for its whole process lifetime — restart it.

## 6. Reconfigure the profile

The first interactive run prompts for capability and chat-model configuration and persists the answers. To re-run those prompts:

```bash
pks agentics runner start --configure
```

The prompt only fires on a genuinely interactive console. In CI, `--configure` prints a warning and falls back to the persisted profile rather than blocking.

## 7. Verify

Watch the console. A healthy runner prints its registration, the capabilities it is advertising, and a poll cycle at your chosen interval. With `OTEL_EXPORTER_OTLP_ENDPOINT` set, it also emits a `runner.start` span and a `runner.execute_job` span per job from the `pks-cli.agentics.runner` activity source.

## Options

| Flag | Default | Description |
|---|---|---|
| `--polling-interval <SECONDS>` | `10` | Seconds between job polls. |
| `--inprocess` | — | Execute jobs in-process instead of spawning devcontainers. A testing aid, not a production execution mode. |
| `--worktree` | — | With `--inprocess`, use a git worktree of the current repo as the job workspace instead of a fresh clone. |
| `--work-dir <PATH>` | `.agentics/_work` | Base work directory. |
| `--vibecast-binary <PATH>` | `VIBECAST_BINARY`, else `npx vibecast` | Path to the vibecast binary. |
| `--project <owner-project>` | first saved registration | Project to run for, in `owner/project` form. Auto-registers when not already registered. |
| `--server <SERVER>` | `agentics.dk` | Agentics server URL used when auto-registering. Falls back to `AGENTIC_SERVER`. |
| `--git-user-name <NAME>` | `si-14x` | Git `user.name` configured inside the devcontainer. |
| `--git-user-email <EMAIL>` | `si-14x@agentics.dk` | Git `user.email` configured inside the devcontainer. |
| `--chat-llm-backend-url <URL>` | `CHAT_LLM_BACKEND_URL` | OpenAI-compatible chat-completions base URL. Declaring it enables `chat-llm:v1`. |
| `--chat-llm-backend-key <KEY>` | `CHAT_LLM_BACKEND_KEY` | API key sent to that backend. Ignored without a backend URL. |
| `--chat-llm-model <MODEL>` | `gpt-5.5` | Model id for `chat-llm:v1` jobs when no backend URL override is set. Falls back to `CHAT_LLM_MODEL`. |
| `--chat-llm-verbose` | — | Log every chat frame to the console for debugging. |
| `--configure` | — | Re-run the interactive capability and chat-model prompts. Ignored on a non-interactive console. |
| `-v`, `--verbose` | — | Enable verbose output. |

## Troubleshooting

- **"No runner registrations found".** Register first, or pass `--project owner/project` to auto-register.
- **Devcontainer jobs stay queued.** The runner started degraded. Confirm the Docker daemon is running, then restart the runner.
- **No GitHub device-code prompt on startup.** The preflight only runs when the project's repository requires GitHub and spawn mode is available. Self-hosted git projects never trigger it.
- **`--configure` did nothing in CI.** The prompt requires an interactive console. Configure the profile once on an interactive machine.
- **Chat jobs are never claimed.** `chat-llm:v1` is only advertised when a backend URL is configured, via `--chat-llm-backend-url` or `CHAT_LLM_BACKEND_URL`.
- **Containers pile up after restarts.** Restarting the runner orphans the previous instance's containers. Run [`pks agentics runner cleanup`](/tools/pks/agentics/runner/cleanup).

## See also

- [Register a machine as an Agentics runner](/tools/pks/agentics/runner/register) — the prerequisite step
- [Remove orphaned runner containers](/tools/pks/agentics/runner/cleanup) — what to run after a restart
- [Check a handed-off runner](/tools/pks/agentics/runner/status) — for a runner moved to an SSH target
- [pks agentics CLI reference](/tools/pks/agentics/cli-reference) — every flag and environment variable
