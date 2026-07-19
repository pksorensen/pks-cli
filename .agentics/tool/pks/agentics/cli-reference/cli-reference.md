---
title: "pks agentics CLI reference"
description: "Complete command, flag, and environment-variable reference for pks agentics — login, runner registration, the daemon, SSH handoff, and task submission."
tags: [reference, cli, runner, alp]
category: infrastructure
platform: [linux, macos, windows]
status: stable
author: Poul Kjeldager
component: pks
usage: "pks agentics <command> [options]"
examples:
  - command: "pks agentics init --no-browser"
    description: "Device-code login without opening a browser"
  - command: "pks agentics runner register myorg/myproject --name my-runner"
    description: "Register under an explicit runner name"
  - command: "pks agentics runner start --polling-interval 5"
    description: "Poll for jobs every five seconds"
  - command: "pks agentics runner cleanup --dry-run"
    description: "List orphaned containers, remove nothing"
  - command: "pks agentics task submit --assembly-line-url https://agentics.dk/p/owner/project/assembly-lines/stage-id --title \"CI failure\""
    description: "File a task onto an assembly line stage"
---

`pks agentics` is the command group that connects a machine to agentics.dk, the Assembly Line Platform (ALP): user login, self-hosted runner registration and execution, remote runner handoff over SSH, and task submission onto an assembly line.

The group is a Spectre.Console.Cli branch with two nested branches, `runner` and `task`. No command in the group has an alias. Every command accepts `-v` / `--verbose`.

## Synopsis

```text
pks agentics <command> [options]
```

```text
init                  Device-code login to agentics.dk, stores tokens in ~/.pks-cli
runner register       Register this machine as a runner for one owner/project
runner start          Poll for and execute ALP jobs until stopped
runner cleanup        Remove Docker containers orphaned by a previous runner
runner status         Show the remote tmux session of a handed-off runner
runner logs           Print the full remote tmux pane of a handed-off runner
runner stop           Kill the remote tmux session of a handed-off runner
runner claude-login   Interactive Claude login on an SSH target
task submit           Submit a task onto an assembly line stage
```

### Global environment variables

| Variable | Default | Purpose |
|---|---|---|
| `AGENTICS_SERVER` | `(none)` | Preferred server-host override for `runner register`, `runner start`, and `task submit` server resolution. Checked first. |
| `AGENTIC_SERVER` | `(none)` | Legacy server-host variable, checked after `AGENTICS_SERVER`, before the `agentics.dk` default. |
| `CHAT_LLM_BACKEND_URL` | `(none)` | `runner start`: OpenAI-compatible chat-completions base URL. Its presence enables the `chat-llm:v1` capability. |
| `CHAT_LLM_BACKEND_KEY` | `(none)` | `runner start`: API key sent to that backend. Never sent to or stored by the agentics.dk server. |
| `CHAT_LLM_MODEL` | `gpt-5.5` | `runner start`: model id for `chat-llm:v1` jobs when no backend URL override is set. |
| `VIBECAST_BINARY` | `(none)` | `runner start`: path to the vibecast binary used inside spawned devcontainers. Falls back to `npx vibecast`. |
| `GITHUB_ACTIONS` | `(none)` | `task submit`: when `true`, enables GitHub OIDC as an auth source and CI-context enrichment of the description. |
| `GITHUB_REPOSITORY` | `(none)` | `task submit`: CI provenance in the failure block. |
| `GITHUB_RUN_ID` | `(none)` | `task submit`: CI provenance in the failure block. |
| `GITHUB_RUN_ATTEMPT` | `(none)` | `task submit`: CI provenance in the failure block. |
| `GITHUB_JOB` | `(none)` | `task submit`: job name matched against the Actions jobs API for log enrichment. |
| `GITHUB_WORKFLOW` | `(none)` | `task submit`: CI provenance in the failure block. |
| `GITHUB_SHA` | `(none)` | `task submit`: CI provenance in the failure block. |
| `GITHUB_REF_NAME` | `(none)` | `task submit`: CI provenance in the failure block. |
| `GITHUB_ACTOR` | `(none)` | `task submit`: CI provenance in the failure block. |
| `GITHUB_EVENT_NAME` | `(none)` | `task submit`: CI provenance in the failure block. |
| `GITHUB_SERVER_URL` | `(none)` | `task submit`: CI provenance in the failure block. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `(none)` | `runner start`: when set, the runner emits a `runner.start` span and per-job `runner.execute_job` spans from the `pks-cli.agentics.runner` activity source. |
| `OTEL_SERVICE_NAME` | `(none)` | `runner start`: OpenTelemetry service name. |
| `OTEL_RESOURCE_ATTRIBUTES` | `(none)` | `runner start`: OpenTelemetry resource attributes. |

Server resolution across the group is: the `--server` flag, then `AGENTICS_SERVER`, then `AGENTIC_SERVER`, then `agentics.dk`. A bare host prefixed `localhost` or `127.0.0.1` is addressed over `http://`; everything else over `https://`.

## init

Runs an OAuth 2.0 device-authorization grant (RFC 8628) against Keycloak. It prints a verification URL and user code, attempts to open a browser, then polls the token endpoint honoring `authorization_pending`, `slow_down` (adds five seconds), `expired_token`, and `access_denied`. The polling interval is clamped to a five-second minimum and the deadline is the server-provided lifetime with a sixty-second floor. On success the access, refresh, and id tokens plus the server, realm, and client id are written to `~/.pks-cli/agentics-auth.json` with mode `0600`. The Keycloak base URL is derived as `https://keycloak.<server>/realms/<realm>` unless `--server` is already a full `http(s)` URL.

| Flag | Default | Description |
|---|---|---|
| `--server <SERVER>` | `agentics.dk` | Agentics server host or full URL. |
| `--realm <REALM>` | `agentics` | Keycloak realm. |
| `--client-id <ID>` | `pks-cli` | OAuth client id. |
| `--no-browser` | — | Print the verification URL instead of opening a browser. |
| `-v`, `--verbose` | — | Enable verbose output. |

**Endpoint:** `POST {keycloak}/realms/{realm}/protocol/openid-connect/auth/device`.

## runner register

Posts to the project's runners endpoint, then stores the returned runner id, name, and token locally. Registration labels are always `self-hosted` plus the operating system (`windows`, `macos`, `linux`, or `unknown`). If the project's git URL is on github.com, the command runs a GitHub device-code login — skipped when already authenticated — then verifies the resulting token reaches that repository, printing a GitHub App install link when it does not. The command is idempotent per project; re-run it to retry a skipped or failed GitHub step. The returned token is printed in plaintext in the success table.

| Argument | Required | Description |
|---|---|---|
| `OWNER_PROJECT` | yes | Target project in `owner/project` form. A value without `/` is rejected. |

| Flag | Default | Description |
|---|---|---|
| `--name <NAME>` | machine hostname | Runner name recorded on the server. |
| `--server <SERVER>` | `agentics.dk` | Agentics server URL. |
| `-v`, `--verbose` | — | Enable verbose output. |

**Endpoint:** `POST {server}/api/owners/{owner}/projects/{project}/runners`.

## runner start

Starts the long-running daemon. Each cycle it polls the jobs endpoint advertising a capability set computed from Docker and spawn availability plus the configured chat backend — `alp_operator`, `chat-session:v1`, `devcontainer-session:v1`, `chat-llm:v1` — and dispatches the returned job: `git_push`, `git_distribute`, `chat_llm` (backgrounded so it never blocks the poll loop, and drained on shutdown), or a devcontainer spawn lifecycle. Without `--project` it uses the first saved registration; with `--project` it auto-registers when no local registration exists. It runs until SIGINT or SIGTERM, draining in-flight jobs and cleaning up spawn-mode resources.

Docker availability is probed before the GitHub preflight and before the git credential socket listener is created, so a Docker-less machine starts in degraded mode instead of hanging: spawn capabilities are not advertised, devcontainer jobs are left queued, and git and chat jobs keep working. Spawn capabilities are not re-enabled if Docker returns mid-run. On an interactive console with Docker unavailable and SSH targets registered, the degraded path offers to hand the runner off to a remote target; accepting exits the local process with code 0.

| Flag | Default | Description |
|---|---|---|
| `--polling-interval <SECONDS>` | `10` | Seconds between job polls. |
| `--inprocess` | — | Execute jobs in-process instead of spawning devcontainers. A testing aid. |
| `--worktree` | — | With `--inprocess`, use a git worktree of the current repo as the job workspace instead of a fresh clone. |
| `--work-dir <PATH>` | `.agentics/_work` | Base work directory. |
| `--vibecast-binary <PATH>` | `VIBECAST_BINARY`, else `npx vibecast` | Path to the vibecast binary. |
| `--project <owner-project>` | first saved registration | Project to run for. Auto-registers when not already registered. |
| `--server <SERVER>` | `agentics.dk` | Agentics server URL used when auto-registering. |
| `--git-user-name <NAME>` | `si-14x` | Git `user.name` configured inside the devcontainer. |
| `--git-user-email <EMAIL>` | `si-14x@agentics.dk` | Git `user.email` configured inside the devcontainer. |
| `--chat-llm-backend-url <URL>` | `CHAT_LLM_BACKEND_URL` | OpenAI-compatible chat-completions base URL. Enables `chat-llm:v1`. |
| `--chat-llm-backend-key <KEY>` | `CHAT_LLM_BACKEND_KEY` | API key sent to that backend. Ignored without a backend URL. |
| `--chat-llm-model <MODEL>` | `gpt-5.5` | Model id for `chat-llm:v1` jobs when no backend URL override is set. |
| `--chat-llm-verbose` | — | Log every chat frame to the console. Frame text can include user chat content. |
| `--configure` | — | Re-run the interactive capability and chat-model prompts. Ignored on a non-interactive console. |
| `-v`, `--verbose` | — | Enable verbose output. |

**Endpoint:** `POST {server}/api/owners/{owner}/projects/{project}/runners/jobs`.

## runner cleanup

Lists every Docker container carrying the `pks.agentics.fingerprint` label, running or stopped, and removes those orphaned by a previous runner process. Containers are bound to their creating process by a `pks.agentics.runner-instance` label, and a restarted runner cannot reuse the old ones because their bind mounts point at temp directories it does not share. Liveness is approximated by looking for a running `pks-cli agentics runner start` process with `pgrep`: when none is found, every labelled container is a removal candidate; when one is found, only label-less containers are treated as orphans. Removal uses `docker rm -f`. Without `--dry-run` and without `--yes`, the command prompts `Remove N container(s)?`, defaulting to No.

| Flag | Description |
|---|---|
| `-n`, `--dry-run` | Show what would be removed without removing anything. |
| `-y`, `--yes` | Skip the confirmation prompt. |
| `--all` | Remove all `pks.agentics.*` containers, including those of currently-running runners. |
| `-v`, `--verbose` | Enable verbose output. |

Requires the `docker` CLI on `PATH`. A failure to list containers exits 1.

## runner status

Captures the remote tmux pane (`tmux capture-pane -p`) for the session named after the owner/project on an SSH target, reports whether the session is present or exited, prints the last 10 lines, and warns when the target's Claude credentials Docker volume for this owner/project appears to be missing. Target and project are resolved from local runner registrations whose profile records the SSH target label. Omitting `TARGET` auto-selects the only registered target, or opens an interactive picker when more than one is registered. Exits 1 when no project was handed off to the target, or when several projects match and `--project` is not given.

| Argument | Required | Description |
|---|---|---|
| `TARGET` | no | SSH target label or host. Auto-selected when only one target is registered; interactive picker otherwise. |

| Flag | Description |
|---|---|
| `--project <owner-project>` | Disambiguate when more than one project was handed off to this target. |
| `-v`, `--verbose` | Enable verbose output. |

## runner logs

Prints the full remote tmux pane output rather than the last 10 lines. Target and project resolution is identical to `runner status`. Output is limited to what is currently in the pane's scrollback and is capped by the remote's tmux history-buffer size; this is not a persisted or streamed log file.

| Argument | Required | Description |
|---|---|---|
| `TARGET` | no | SSH target label or host. Auto-selected when only one target is registered; interactive picker otherwise. |

| Flag | Description |
|---|---|
| `--project <owner-project>` | Disambiguate when more than one project was handed off to this target. |
| `-v`, `--verbose` | Enable verbose output. |

## runner stop

Kills the remote tmux session (`tmux kill-session`) for a project handed off to an SSH target, stopping that runner daemon. There is no confirmation prompt. Exit code 1 covers both an unreachable target and an already-stopped session; the two are not distinguished. Devcontainers and volumes the remote runner created are not removed.

| Argument | Required | Description |
|---|---|---|
| `TARGET` | no | SSH target label or host. Auto-selected when only one target is registered; interactive picker otherwise. |

| Flag | Description |
|---|---|
| `--project <owner-project>` | Disambiguate when more than one project was handed off to this target. |
| `-v`, `--verbose` | Enable verbose output. |

## runner claude-login

Opens an interactive Claude Code login session on an SSH target to populate its Claude credentials Docker volume, so later devcontainer spawns for this owner/project run headless. It resolves the target's SSH key — materializing a pks-managed key to a temporary path and deleting it afterwards in a `finally` block — builds a one-off container login command, and launches it interactively. The operator logs in and exits with Ctrl+D. The volume uses the `project` credential scope, the same default a job uses when its agent definition leaves the scope unset. Failure to materialize a managed key exits 1 before any SSH is attempted.

| Argument | Required | Description |
|---|---|---|
| `TARGET` | no | SSH target label or host. Auto-selected when only one target is registered; interactive picker otherwise. |

| Flag | Description |
|---|---|
| `--project <owner-project>` | Disambiguate when more than one project was handed off to this target. |
| `-v`, `--verbose` | Enable verbose output. |

## task submit

Parses owner, project, and stage id out of the assembly-line URL, resolves a bearer token, and posts the title, description, column id, priority, and labels to the stage's tasks endpoint. The URL must match `/p/{owner}/{project}/assembly-lines/{stageId}`; any other shape fails to parse. `--assembly-line-url` and `--title` are validated by the command rather than the parser — omitting either prints a red error and exits 1.

Token resolution order: `--token`, then GitHub OIDC when `GITHUB_ACTIONS` is `true`, then the stored user token from `pks agentics init` (refreshed in place if expired), then a matching runner registration's token as a back-compat fallback. Inside GitHub Actions the description gains a `## CI/CD Failure` block with workflow, job, commit, actor, and trigger metadata, plus — when a stored GitHub token is available — the failed step names and the last 100 lines of that job's log. Enrichment is best-effort and never fails the submission; when `GITHUB_JOB` does not match a job by name, any job with conclusion `failure` is used.

| Flag | Required | Description |
|---|---|---|
| `--assembly-line-url <URL>` | yes | Full assembly-line URL, in the form `/p/{owner}/{project}/assembly-lines/{stageId}`. |
| `--title <TITLE>` | yes | Task title. |
| `--description <TEXT>` | no | Task description or raw error output. |
| `--column-id <ID>` | no | Target column id. Defaults to the stage's first column. |
| `--priority <PRIORITY>` | no | Task priority: `low`, `medium`, or `high`. Defaults to `high`. |
| `--labels <LABELS>` | no | Comma-separated labels to apply. |
| `--server <URL>` | no | Server URL override. Defaults to the runner registration's server, then `agentics.dk`. |
| `--token <TOKEN>` | no | Bearer token override that skips the auth chain. |
| `-v`, `--verbose` | no | Enable verbose output. The reported auth source is inferred display logic, not necessarily the source used internally. |

**Endpoint:** `POST {server}/api/owners/{owner}/projects/{project}/assembly-lines/{stageId}/tasks`.

## See also

- [pks agentics](/tools/pks/agentics) — the group overview and mental model
- [Log in to agentics.dk](/tools/pks/agentics/init) — the device-code login walkthrough
- [Run a self-hosted Agentics runner](/tools/pks/agentics/runner) — registration, daemon, handoff, cleanup
- [Submit a task to an assembly line](/tools/pks/agentics/task) — the CI/CD filing walkthrough
