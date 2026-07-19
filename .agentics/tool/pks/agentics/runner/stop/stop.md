---
title: "pks agentics runner stop"
description: "Kill the remote tmux session of a runner handed off to an SSH target, stopping that runner daemon immediately and without a confirmation prompt."
tags: [how-to, runner, ssh, lifecycle]
category: infrastructure
platform: [linux, macos, windows]
icon: square
status: beta
author: Poul Kjeldager
component: pks
usage: "pks agentics runner stop [TARGET] [options]"
examples:
  - command: "pks agentics runner stop hetzner"
    description: "Stop the remote runner on a target"
  - command: "pks agentics runner stop hetzner --project myorg/myproject"
    description: "Disambiguate when several projects share a target"
---

`runner stop` ends a runner that was handed off to an SSH target by killing its remote tmux session. It stops the daemon and nothing else — containers and volumes the runner created stay where they are.

## 1. Prerequisites

- **A completed SSH handoff** for the project you want to stop.
- **SSH reachability** to the target and tmux running on it.

## 2. Stop the runner

```bash
pks agentics runner stop hetzner
```

There is **no** confirmation prompt — the session is killed immediately. Target and project resolution match `runner status`: omitting `TARGET` auto-selects the only registered target or opens an interactive picker when more than one is registered, and `--project` is required when several projects share the target.

```bash
pks agentics runner stop hetzner --project myorg/myproject
```

## 3. Verify

```bash
pks agentics runner status hetzner
```

The session should report as exited.

## 4. Clean up the remote machine

Stopping the session leaves any devcontainers the runner spawned behind. Run [`pks agentics runner cleanup`](/tools/pks/agentics/runner/cleanup) on the remote machine separately when you want them gone.

## Options

| Flag | Description |
|---|---|
| `--project <owner-project>` | Disambiguate when more than one project was handed off to this target. |
| `-v`, `--verbose` | Enable verbose output. |

### Argument

| Argument | Required | Description |
|---|---|---|
| `TARGET` | no | SSH target label or host. Auto-selected when only one target is registered; an interactive picker is shown otherwise. |

## Troubleshooting

- **Exit code 1 with a failure message.** Either the target is unreachable or the session was already stopped. The two cases are not distinguished in the output — check reachability with a plain `ssh` first, then confirm with `runner status`.
- **"No project has been handed off to \<target\>".** No local registration records this target. Nothing to stop there.
- **Jobs still appear to run after stopping.** A container the runner spawned may still be alive. Clean up on the remote machine.

## See also

- [Check a handed-off runner](/tools/pks/agentics/runner/status) — confirm the session actually ended
- [Remove orphaned runner containers](/tools/pks/agentics/runner/cleanup) — what to run on the remote machine afterwards
- [pks agentics CLI reference](/tools/pks/agentics/cli-reference) — every flag and environment variable
