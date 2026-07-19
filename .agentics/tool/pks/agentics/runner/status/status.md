---
title: "pks agentics runner status"
description: "Check the remote tmux session of a runner handed off to an SSH target, see its last output lines, and get warned about a missing credentials volume."
tags: [how-to, runner, ssh, diagnostics]
category: infrastructure
platform: [linux, macos, windows]
icon: activity
status: beta
author: Poul Kjeldager
component: pks
usage: "pks agentics runner status [TARGET] [options]"
examples:
  - command: "pks agentics runner status hetzner"
    description: "Show the remote runner session on a target"
  - command: "pks agentics runner status hetzner --project myorg/myproject"
    description: "Disambiguate when several projects share a target"
---

When a runner is handed off to a registered SSH target, it runs inside a tmux session named after the owner/project. `runner status` captures that session's pane and tells you whether the runner is alive, what it printed last, and whether the target is missing the Claude credentials volume a headless devcontainer spawn needs.

## 1. Prerequisites

- **A completed handoff.** The offer appears during a degraded [`runner start`](/tools/pks/agentics/runner/start) on an interactive console. The local registration then records the SSH target label.
- **SSH reachability** to the target, registered with `pks ssh register` or created by `pks vm init`.
- **tmux on the remote machine.** This command drives `tmux capture-pane` over SSH exclusively — never systemd.

## 2. Check a target

```bash
pks agentics runner status hetzner
```

The output reports whether the session is present (it has output) or has exited, followed by the last 10 lines of the pane. For the whole buffer, use [`runner logs`](/tools/pks/agentics/runner/logs).

Omitting `TARGET` auto-selects the only registered SSH target, or opens an interactive picker when more than one is registered — so non-interactive callers must always pass it.

## 3. Disambiguate several projects

If more than one project was handed off to the same target, the command lists the ambiguous matches and exits 1. Name the project:

```bash
pks agentics runner status hetzner --project myorg/myproject
```

## 4. Read the credentials warning

The command also checks whether the target's Claude credentials Docker volume for this owner/project appears to be missing. A missing volume means a headless devcontainer spawn there stalls on an interactive OAuth login that nobody can complete. Fix it with [`runner claude-login`](/tools/pks/agentics/runner/claude-login).

## 5. Verify

A healthy target prints a present session, recent runner output such as poll cycles, and no credentials-volume warning.

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

- **"No project has been handed off to \<target\>".** No local runner registration records this target's label or host. Re-run the handoff from a degraded `runner start`.
- **The command lists several projects and exits 1.** Pass `--project owner/project`.
- **The session reports as exited.** The remote runner stopped. Re-run the handoff, or start the runner on the remote machine directly.
- **A credentials-volume warning keeps appearing.** Run `runner claude-login` against the same target and project, so the volume names match.

## See also

- [Print a handed-off runner's full output](/tools/pks/agentics/runner/logs) — the untruncated pane buffer
- [Stop a handed-off runner](/tools/pks/agentics/runner/stop) — kill the remote tmux session
- [Log in to Claude on an SSH target](/tools/pks/agentics/runner/claude-login) — clear the credentials warning
- [pks agentics CLI reference](/tools/pks/agentics/cli-reference) — every flag and environment variable
