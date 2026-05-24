---
id: FT-008
title: Agentics runtime (runner + tasks)
domain: agentic-runtime
status: draft
adrs: []
tests: []
source-files: [src/Commands/Agentics/AgenticsCommand.cs, src/Commands/Agentics/AgenticsInitCommand.cs, src/Commands/Agentics/Runner/AgenticsRunnerStartCommand.cs, src/Commands/Agentics/Runner/AgenticsRunnerRegisterCommand.cs, src/Commands/Agentics/Runner/AgenticsRunnerCleanupCommand.cs, src/Commands/Agentics/Runner/OtlpProxy.cs, src/Commands/Agentics/Tasks/AgenticsTaskSubmitCommand.cs, src/Infrastructure/Services/Agentics/AgenticsAuthService.cs, src/Infrastructure/Services/AgenticsProxy/AgenticsProxy.cs, src/Infrastructure/Services/AgenticsProxy/HostPolicy.cs, src/Infrastructure/Services/Runner/RunnerDaemonService.cs, src/Infrastructure/Services/Runner/RunnerContainerService.cs, src/Infrastructure/Services/Runner/NamedContainerPool.cs, src/Infrastructure/Services/Runner/JobTokenService.cs, src/Infrastructure/Services/Runner/GitCredentialServer.cs]
sessions: [4b5dc880-9084-478c-aa5e-5d931a2a9f85, da2af576-277c-44e8-94c6-73170c6bf047, c3f66d01-cee1-4079-9871-4f6b88251362, 02f82b87-a9e8-43d2-a2f7-c2e9088bf9c8, 001b405a-37d9-4427-b542-4b5037a6f5d8]
---

## Description
Register and run long-lived agentic workers and submit tasks to them for the Agentics
platform. `pks agentics runner register` enrolls a host against an `owner/project` on
the Agentics server; `pks agentics runner start` runs a polling daemon that claims jobs,
spins up per-job containers (via `RunnerContainerService` / `NamedContainerPool`), and
streams telemetry through the in-process `OtlpProxy`. `pks agentics task submit` is the
client-side counterpart that queues work onto the same line. Each job gets a scoped
`AgenticsProxy` (HTTP + Unix-socket) that brokers short-lived capability tokens against
pre-approved hosts (Foundry, ADO, GitHub) plus a `GitCredentialServer` so the agent
inside the container can `git push` without ever holding the user's PAT. The runner is
the substrate the wider Agentics product (assembly lines, stations, tasks) executes on.

## Intent

> From session c3f66d01 (2026-04-25), prompt:
> "do we actually need firecracker. If our goal is to run our pks-cli agentic runner,
> we can just spawn it inside a container instead of inprocess. Why even use inprocess.
> I think we got sidetracked at some point. … When we use the aspire command to start
> runner for project atm, why dont we just ignore the inprocess and pks-cli agentic
> runner should work as normal. My goal is to remove the inprocess part because we
> end up with alot of errors and it confuses you when making new features."

> From session 02f82b87 (2026-04-18), prompt:
> "then it evolved to ALP, server, runner, operator and agent. and the assembly lines.
> this basically becomes a task that goes though the line … where each station uses
> vibecast as operator meaning it becomes one stream per station."

> From session 58672429 (2026-03-29), prompt (referenced from search index):
> "Runner daemon stopped. Jobs processed: 0" — recurring observation that drove the
> rework of the polling/claim loop and the cleanup command.

## Key decisions
- **Polling daemon, not webhooks.** `AgenticsRunnerStartCommand` long-polls the server
  for jobs and publishes a `runner.polls` counter on the `pks-cli.agentics.runner`
  meter so the runner being alive is observable from the AppHost without needing a
  callback URL through NAT.
- **Per-job container, runner-instance-scoped reuse.** `NamedContainerPool` reuses a
  warm container only when both the fingerprint *and* the runner-instance id match;
  restarting the runner forces fresh containers so bind-mounts / sockets / OTLP
  endpoints stay valid (see referenced `docs/adr/0002-runner-container-lifetime.md`).
- **Capability tokens instead of shared secrets.** `AgenticsProxy` + `HostPolicy`
  enforce a per-job allow-list of hosts; the agent calls `POST /api/token` (via
  Unix socket bind-mounted as `$AGENTICS_PROXY_SOCKET`) for a short-lived token,
  so real Azure / GitHub credentials never enter the container.
- **OtlpProxy co-located with the runner.** A TcpListener-based OTLP receiver lives in
  the runner process so spawned agents can ship spans/logs without each container
  needing its own collector wiring — the runner forwards upstream.
- **Drop in-process execution.** The earlier "runner start --inprocess" mode used for
  local dev was retired in favour of "always spawn a container" after it kept masking
  bugs the prod path would hit (see session c3f66d01).

## Gotchas / known issues
- Recurring "Runner daemon stopped. Jobs processed: 0" symptom — `AgenticsRunnerCleanupCommand`
  exists partly because half-claimed jobs and orphaned containers from earlier daemon
  crashes had to be reaped by hand.
- Earlier kill paths used `pkill -f "pks-cli agentics runner"`, which matched the
  user's own shell history line and killed the wrong process; the cleanup command now
  scopes by runner-instance id instead.
- The runner inherits the Foundry-proxy SSH/tunnel constraints from [[FT-005-foundry-proxy-substrate-boundary]]
  when jobs need Foundry tokens — `GatewayPorts yes` on the VM sshd is still required
  or the per-job proxy is unreachable from inside the container.
