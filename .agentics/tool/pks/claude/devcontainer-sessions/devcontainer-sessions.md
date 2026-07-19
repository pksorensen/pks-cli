---
title: "Devcontainer and inline Claude Code sessions"
description: "Spawn a devcontainer for your project on Docker or a remote SSH target and attach an interactive Claude Code session to it, or run inline instead."
tags: [how-to, devcontainer, claude-code, ssh]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks claude [PROJECT_PATH] [options]"
---

Get an isolated Claude Code session running in a few minutes: confirm a devcontainer config exists, run `pks claude`, choose where the container lives, and attach. The same command runs the session inline in your current shell when you pass `--inline`.

This page covers the branch's default command. For the complete flag list see the [pks claude reference](/tools/pks/claude/reference).

## 1. Prerequisites

- **The `claude` CLI on your PATH.** Required by `--inline` and by every launcher in the branch. Install with `npm i -g @anthropic-ai/claude-code`.
- **Docker.** Required for the containerized path, either on this machine or on the remote host you target.
- **A `.devcontainer/devcontainer.json` in the project.** Required for the local containerized path. Run `pks devcontainer init` first if the project has none.
- **A registered SSH target.** Required only for `--ssh-target`. Targets live in `~/.pks-cli/ssh-targets.json`.

## 2. Check the project has a devcontainer

```bash
ls .devcontainer/devcontainer.json
```

If the file is missing, the local containerized path aborts with an error telling you to run `pks devcontainer init`. Create the config before continuing.

## 3. Spawn and attach

```bash
pks claude
```

With no `--ssh-target` and no `--inline`, the command prompts for a location: local Docker, a registered SSH target, or a newly spawned VM. Pick one, and it builds or reuses the container, then attaches an interactive `claude --dangerously-skip-permissions` session over `docker exec`. You end up at the Claude Code prompt inside the container.

To skip the location prompt, name the target:

```bash
pks claude --ssh-target my-vm
```

To operate on a project other than the current directory, pass its path:

```bash
pks claude ./my-project --rebuild
```

### Option A — containerized (recommended)

The default. The agent runs in an isolated filesystem with forwarded Docker credentials, and you can put it on a remote box.

### Option B — inline

```bash
pks claude --inline
```

This skips the devcontainer entirely and runs `claude --dangerously-skip-permissions` directly in your current shell. It can also stand up a local Azure AI Foundry MSI token server so the inline session uses Foundry-hosted models. That wiring is skipped when `CLAUDE_CODE_USE_FOUNDRY` and `ANTHROPIC_BASE_URL` are already set in your environment.

## 4. Handle an existing container

When a container already exists for the project, the command compares three hashes — the host `.devcontainer` directory, the container's build-time label, and the live volume — and prompts to sync, discard, or resolve the conflict. Pass `-f` to skip the detection and reuse prompt entirely.

To control rebuilds without being asked:

```bash
pks claude --auto-rebuild     # rebuild silently when config changed
pks claude --no-rebuild       # never rebuild, even when config changed
pks claude --rebuild          # rebuild even when nothing changed
```

## 5. Pass environment and server settings

```bash
pks claude --env FOO=bar --env BAZ=qux --server https://my-tunnel.devtunnels.ms
```

`--env` is repeatable and forwards `KEY=VALUE` pairs into the container. It is also honored by `--inline`, where the pairs are exported into the local `claude` process. `--server` forwards its URL into the container as `AGENTIC_SERVER`.

## 6. Exit and choose a lifecycle action

Quit the Claude Code session normally. On a clean remote-session exit the command prompts for what to do with the container: reattach VS Code, keep it running, stop it, or remove it. Choosing removal deletes the container and any Docker volumes discovered through `docker inspect`, behind a confirmation that defaults to No.

Remote spawns may follow up with an offer to deallocate the backing Azure VM. That offer is gated by a second factor and can be denied even after you confirm.

## 7. Verify

```bash
docker ps
```

You should see the container for your project listed while the session is running, and — if you chose to stop or remove it on exit — absent afterwards.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Error naming `pks devcontainer init` | The project has no `.devcontainer/devcontainer.json`. Create one, or use `--inline`. |
| Exit code `127` under `--inline` | The `claude` CLI is not on PATH. Install it with `npm i -g @anthropic-ai/claude-code`. |
| The command keeps prompting | Interactive is the default. Pass `--ssh-target` or `--inline`, and `-f` to skip the reuse prompt. |
| Inline session ignores Foundry | `CLAUDE_CODE_USE_FOUNDRY` or `ANTHROPIC_BASE_URL` is already exported, so the Foundry picker is skipped. Unset them and retry. |
| Extra git-proxy moving parts on a remote spawn | Registered ADO git repos plus ADO authentication trigger an offer to copy the ADO refresh token to the VM and start a local git proxy inside it. Decline the offer if you do not need ADO clones. |

## Next steps

- [pks claude reference](/tools/pks/claude/reference) — every flag on the default command
- [pks claude codex](/tools/pks/claude/codex) — point a session at Azure AI Foundry instead
- [pks claude backup](/tools/pks/claude/backup) — mirror the `~/.claude` state these sessions produce
- [pks claude](/tools/pks/claude) — how the launchers and the analytics halves fit together
