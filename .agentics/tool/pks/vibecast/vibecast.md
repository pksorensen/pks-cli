---
title: "pks vibecast"
description: "Spawn or reattach to a devcontainer on a remote SSH target and drop into an interactive vibecast broadcast session inside it, including Vibegame matches."
tags: [reference, cli, devcontainer, remote]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks vibecast [PROJECT_PATH] [options]"
examples:
  - command: "pks vibecast"
    description: "Pick a registered SSH target and open vibecast there"
  - command: "pks vibecast --ssh-target my-vm --server https://my-tunnel.devtunnels.ms"
    description: "Skip the picker and forward a custom AGENTIC_SERVER"
  - command: "pks vibecast --ssh-target my-vm --env FOO=bar"
    description: "Forward an extra environment variable into the container"
  - command: "pks vibecast game abc123"
    description: "Join Vibegame match abc123 on a game-prefixed container"
---

`pks vibecast` spawns, or reattaches to, a devcontainer on a registered remote SSH target — typically an Azure VM registered with `pks vm` or `pks ssh register` — and drops you into an interactive `vibecast` session running inside it. It is the remote counterpart to running `vibecast` directly on a devcontainer host: instead of you SSHing in, finding the container, and running `docker exec` by hand, the command does that for you.

## Overview

The `vibecast` group is a Spectre.Console.Cli branch with one default command and one nested leaf:

- **`pks vibecast`** — the default command. Picks or is told a remote SSH target, ensures the target VM is running, checks Docker, offers to reattach to an existing devcontainer or spawns a new one, then execs into `npx -y vibecast` (or an SSH-injected embedded binary) inside the container.
- **`pks vibecast game <gameId>`** — joins a Vibegame tournament match. It reuses the exact same spawn flow but treats the leading argument as a game ID instead of a project path, and seeds the container with a tournament briefing and an event-monitor script.

Both commands share one `Settings` class (the one defined for `pks devcontainer spawn`), so every flag documented below parses identically under both, even though only a subset is meaningful for `game`.

## What you get

- **One command to reach a broadcasting agent session.** No manual SSH, no `docker ps` to find the container, no remembering the `npx -y vibecast` invocation.
- **Automatic VM lifecycle.** If the target maps to a known Azure VM record, `pks vibecast` starts it before connecting and can offer to deallocate it after you disconnect.
- **Reattach, not just spawn.** If a matching devcontainer is already running on the host, you're offered a reattach instead of a rebuild.
- **Locale-safe terminal.** The `docker exec` always sets `LANG=C.UTF-8`/`LC_ALL=C.UTF-8` so vibecast's TUI glyphs render correctly inside minimal devcontainer images.
- **Vibegame tournament mode.** `pks vibecast game <id>` layers a competitive live-coding match on top of the same spawn flow, with its own prompt and event feed.

## When to use it

Use `pks vibecast` to broadcast or drive a Claude Code (or other agent) session running inside a fresh or existing remote devcontainer without manually SSHing in and running `docker exec` yourself.

Use `pks vibecast game <gameId>` specifically to join a Vibegame match — a separate two-player live-coding competition product. It wraps the same remote-spawn flow but injects a game-specific initial prompt and an event-monitor script into the container.

Prefer plain SSH plus a manual `docker exec`/`npx vibecast` only when you need something the wizard doesn't support — for example a scripted or headless run with no interactive TTY. For that case, see `pks agentics runner start` instead.

## Prerequisites

- **A registered SSH target.** Register one with `pks ssh register <host>` first, or use the `Spawn new VM...` picker choice, which delegates to `pks vm init`. If you pass `--ssh-target`, it must exactly match a registered target's label or host — an unmatched value fails hard with "SSH target not found" rather than falling back to the picker.
- **Docker on the remote host.** Checked with `docker --version`; if the check fails, the command asks whether to proceed anyway rather than blocking outright.
- **A `.devcontainer` source, if none exists remotely.** Either an existing `.devcontainer` on the local project path (copied up), or a discoverable NuGet devcontainer template tagged `pks-devcontainers` or `pks-cli` — for example `dotnet new install PKS.Templates.PksFullstack`.
- **Node.js ≥18 and the `devcontainer` CLI on the remote host**, to run `devcontainer up`. `pks vibecast` auto-installs these (roughly two minutes) if missing.

## Authentication and credentials

`vibecast` itself has no built-in authentication. Access is inherited from the registered SSH target — key-based SSH stored in `~/.pks-cli/ssh-targets.json`, managed with `pks ssh register`.

VM lifecycle actions — auto-starting the target on connect, or the post-session offer to deallocate it — go through Azure auth (an interactive device-code flow, the same one `pks vm` uses) plus pks-cli's `ActionGuard` second-factor gate on the `devcontainer.spawn.remote` and `vm.stop` action IDs. The initial confirmation prompts are agent-answerable, but the guard itself is not — an automated caller can still be blocked here even after answering yes.

> **Note.** `--inline` is defined on the shared `Settings` class and works for `pks claude`, but `VibecastCommand` does not override the pre-launch hook that honors it. Passing `--inline` to either `pks vibecast` or `pks vibecast game` has no effect — the command still goes through the full devcontainer/SSH flow.

## Synopsis

```text
pks vibecast [PROJECT_PATH] [options]
```

```text
vibecast        Spawn a devcontainer on a remote SSH target and connect via vibecast
vibecast game   Join a vibegame tournament match — code your bot and battle
```

## pks vibecast

Picks (or is told via `--ssh-target`) a registered remote SSH target, ensures the target VM is running, checks for Docker, then either offers to reattach to an existing devcontainer already running on that host or walks the full spawn flow: template selection, `.devcontainer` hash-drift detection against a possibly-edited volume, and `devcontainer up`. It then drops into an interactive `docker exec -it` session running `npx -y vibecast` (or an SSH-injected embedded binary) inside the container, with `AGENTIC_SERVER`, `--env` values, and Azure Foundry MSI token variables wired in.

On a clean exit (exit code `0`), it prompts for a post-quit lifecycle action: keep the container running, stop it, or destroy it — optionally followed by an offer to deallocate the underlying Azure VM to save cost. A crashed session (non-zero exit) skips this prompt.

| Flag | Description |
|---|---|
| `PROJECT_PATH` | Path to the project directory (positional, optional; defaults to the current directory) |
| `--volume-name <NAME>` | Custom volume name for the devcontainer |
| `--no-launch-vscode` | Don't automatically launch VS Code after spawning |
| `--no-copy-source` | Don't copy source files (only `.devcontainer` configuration) |
| `--no-bootstrap` | Use direct execution instead of a bootstrap container (advanced) |
| `--forward-docker-config` | Forward Docker credentials from host to devcontainer (default `true`, matches VS Code) |
| `--no-forward-docker-config` | Disable Docker credential forwarding |
| `--docker-config-path <PATH>` | Path to `config.json` to forward (defaults to `~/.docker/config.json`) |
| `--rebuild` | Force a rebuild even if no configuration changes are detected |
| `--no-rebuild` | Skip a rebuild even if configuration changes are detected |
| `--auto-rebuild` | Rebuild automatically without prompting when configuration changes are detected |
| `--ssh-target <TARGET>` | SSH target label or host to spawn on remotely |
| `--env <ENV>` | Extra environment variable (`KEY=VALUE`) forwarded into the container; repeatable |
| `--server <URL>` | Agentic server URL forwarded as `AGENTIC_SERVER` into the container |
| `--inline` | Documented for commands that support inline execution; a no-op here (see the note above) |
| `-o, --output-path <PATH>` | Output directory path for devcontainer files (default: current directory) |
| `-v, --verbose` | Enable verbose output |
| `-f, --force` | Force overwrite existing files; also skips the existing-container reuse prompt |
| `--dry-run` | Show what would be done without making changes |

```bash
pks vibecast
```

Opens the interactive picker: choose a registered SSH target — or spawn a new VM — then reattach to or spawn a devcontainer there before opening vibecast.

```bash
pks vibecast --ssh-target my-vm --server https://my-tunnel.devtunnels.ms
```

Skips the target picker and forwards `AGENTIC_SERVER=my-tunnel.devtunnels.ms` into the container. `--server` is normalized by stripping any `http(s)://` scheme before it's forwarded, so passing a bare host is equivalent.

## pks vibecast game

Joins a Vibegame tournament match. It runs the same remote-spawn/reattach flow as `pks vibecast`, but the leading positional argument is the game ID rather than a project path. The container and volume names are prefixed with `game-<first 8 chars of gameId>-<random 8-char session id>` so two players joining the same match get independent containers. The vibecast invocation gets `--attr game-id <id> --plugin vibegame` appended, and the container is seeded with a base64-encoded tournament briefing at `/tmp/vibegame-prompt.txt` (delivered via `VIBECAST_INITIAL_PROMPT_FILE`) plus an SSE event-monitor script at `/tmp/game_events.sh` that the agent is instructed to run in the background and watch for `countdown`, `game_start`, and `game_end` events.

> **Note.** Vibegame is a newer feature than the default `pks vibecast` command; expect its flags and behavior to change as the tournament flow matures.

`pks vibecast game` inherits the identical `Settings` class as `pks vibecast`, so every flag above parses the same way; only `PROJECT_PATH`, `--ssh-target`, `--env`, and `--server` are meaningful in game mode.

| Flag | Description |
|---|---|
| `PROJECT_PATH` | Repurposed as the Vibegame match/game ID (for example `abc123`), not a filesystem path |
| `--volume-name <NAME>` | Custom volume name (inherited; rarely used since game mode auto-derives the name) |
| `--no-launch-vscode` | Don't automatically launch VS Code after spawning |
| `--no-copy-source` | Don't copy source files (only `.devcontainer` configuration) |
| `--no-bootstrap` | Use direct execution instead of a bootstrap container (advanced) |
| `--forward-docker-config` | Forward Docker credentials from host to devcontainer (default `true`) |
| `--no-forward-docker-config` | Disable Docker credential forwarding |
| `--docker-config-path <PATH>` | Path to `config.json` to forward (defaults to `~/.docker/config.json`) |
| `--rebuild` | Force a rebuild even if no configuration changes are detected |
| `--no-rebuild` | Skip a rebuild even if configuration changes are detected |
| `--auto-rebuild` | Rebuild automatically without prompting when configuration changes are detected |
| `--ssh-target <TARGET>` | SSH target label or host to spawn on remotely |
| `--env <ENV>` | Extra environment variable (`KEY=VALUE`) forwarded into the container; repeatable |
| `--server <URL>` | Agentic server URL forwarded as `AGENTIC_SERVER` into the container |
| `--inline` | Documented for commands that support inline execution; a no-op here |
| `-o, --output-path <PATH>` | Output directory path for devcontainer files (default: current directory) |
| `-v, --verbose` | Enable verbose output |
| `-f, --force` | Force overwrite existing files |
| `--dry-run` | Show what would be done without making changes |

```bash
pks vibecast game abc123
```

Joins Vibegame match `abc123`, spawning or attaching to a game-prefixed devcontainer on the picked or registered SSH target.

## Troubleshooting

- **TUI glyphs render as `__`.** This shouldn't happen — `pks vibecast` always sets `LANG=C.UTF-8`/`LC_ALL=C.UTF-8` on the `docker exec` specifically to avoid it. If you still see it, check whether a custom `.devcontainer` image overrides the locale after container start.
- **Foundry credentials missing inside `claude` despite being set on the host.** `pks vibecast` seeds Azure Foundry MSI token variables into tmux's global environment (`tmux set-environment -g`) before starting vibecast. A tmux server that started on the host before Foundry credentials were minted can still leak stale or missing values — kill the stale tmux server on the remote host and reconnect.
- **The embedded vibecast binary doesn't seem to be running.** If pks-cli was built with `-p:EmbedVibecast=true` and the SSH injection of that binary fails, the command falls back silently to `npx -y vibecast` with a yellow warning. Check the connect output for that warning rather than assuming the embedded binary launched.
- **`--ssh-target` fails with "SSH target not found".** The value must exactly match a registered target's label or host in `~/.pks-cli/ssh-targets.json`. There is no fuzzy match and no fallback to the interactive picker — register the target first with `pks ssh register`.
- **Destroying a container is irreversible.** The `Remove` option in the post-quit prompt requires a typed confirmation and deletes every Docker volume mounted into the container along with it. There is no undo.
- **Deallocating the VM afterward doesn't happen even after confirming.** The offer is gated by `ActionGuard`'s `vm.stop` action ID. The initial confirm prompt is agent-answerable, but the guard check itself can still block an automated caller.
- **Vibegame events never arrive.** `game_events.sh` (injected by `pks vibecast game`) depends on `VIBEGAME_SERVER`/`VIBEGAME_GAME_ID`, which pks-cli does not set — they come from the vibecast binary itself when `--plugin vibegame` is active. If events don't show up, check the vibegame plugin in the `vibecast` source, not pks-cli. It also depends on the agent runtime exposing a Monitor tool the agent can point at the background script; without one, the agent has no way to see live events.

## See also

- [pks](/tools/pks) — the full command surface `vibecast` is one branch of
- [pks ssh](/tools/pks/ssh) — register and manage the SSH targets `vibecast` connects through
- [pks ssh register](/tools/pks/ssh/register) — add a new SSH target before spawning
- [pks vm](/tools/pks/vm) — provision and manage the Azure VMs behind most SSH targets
- [pks devcontainer spawn](/tools/pks/devcontainer/spawn) — the underlying spawn flow `vibecast` reuses
- [pks agentics runner](/tools/pks/agentics/runner) — the scripted/headless alternative when no interactive TTY is available
