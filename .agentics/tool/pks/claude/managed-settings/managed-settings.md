---
title: "pks claude managed-settings"
description: "Render a Claude Code managed-settings.json from your registered plugin marketplaces, to stdout or straight into the OS-level policy location."
tags: [how-to, policy, devcontainer, claude-code]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks claude managed-settings [options]"
---

`pks claude managed-settings` produces a Claude Code `managed-settings.json` from the marketplaces registered in `~/.pks-cli/claude-marketplace.json`. It is the step that bakes marketplace policy into a devcontainer or enterprise image, so every Claude Code session started on that image sees the same managed configuration.

## Prerequisites

- **Registered marketplaces.** The registry at `~/.pks-cli/claude-marketplace.json` is populated elsewhere in pks, not by this command. With nothing registered the render is effectively empty.
- **Write access to the target path.** `/etc/claude-code/` normally requires root. The command does not elevate.

## What managed settings are

Claude Code reads an OS-level managed-policy file at `/etc/claude-code/managed-settings.json`. Settings there are applied to sessions on that machine regardless of the user's own configuration. This command renders that file; it does not install, elevate, or validate against a running Claude Code.

## 1. Inspect the render

```bash
pks claude managed-settings
```

The rendered JSON goes to stdout. Read it before writing it anywhere — this is the fastest way to confirm your marketplace registry contains what you expect.

## 2. Write it to a file

```bash
pks claude managed-settings --output ./managed-settings.json
```

Parent directories are created as needed.

## 3. Install it as machine policy

```bash
sudo pks claude managed-settings --output /etc/claude-code/managed-settings.json
```

This is the canonical target. In a Dockerfile, run the same command as part of the image build rather than at container start, so the policy is present before any session begins.

## Options

| Flag | Description |
|---|---|
| `--output <PATH>` | Write to a file instead of stdout, creating parent directories as needed. |

## Verify

```bash
pks claude managed-settings
```

You should see JSON on stdout reflecting your registered marketplaces. Output that looks empty or default means no marketplaces are registered yet.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The render is empty or default | No marketplaces are registered. This command does not register them — populate `~/.pks-cli/claude-marketplace.json` through the marketplace commands first. |
| Permission denied writing to `/etc/claude-code/` | The command does not elevate. Run it under `sudo`, or render to a writable path and install the file separately. |
| Claude Code ignores the file | Confirm the path is exactly `/etc/claude-code/managed-settings.json` on the machine running Claude Code, not on your build host. |

## See also

- [pks claude backup](/tools/pks/claude/backup) — the other small utility in the branch
- [Devcontainer and inline sessions](/tools/pks/claude/devcontainer-sessions) — the images this policy file targets
- [pks claude reference](/tools/pks/claude/reference) — every command and flag in the branch
