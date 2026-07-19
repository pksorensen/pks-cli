---
title: "pks agentics runner claude-login"
description: "Run an interactive Claude Code login on an SSH target so its credentials volume is populated and later devcontainer spawns can run headless."
tags: [how-to, runner, ssh, credentials]
category: infrastructure
platform: [linux, macos, windows]
icon: key-round
status: beta
author: Poul Kjeldager
component: pks
usage: "pks agentics runner claude-login [TARGET] [options]"
examples:
  - command: "pks agentics runner claude-login hetzner"
    description: "Log in interactively on an SSH target"
  - command: "pks agentics runner claude-login hetzner --project myorg/myproject"
    description: "Disambiguate when several projects share a target"
---

A devcontainer job on a remote runner runs headless. If the target's Claude credentials Docker volume is empty, the spawn stalls on an interactive OAuth login that nobody is there to complete. `runner claude-login` opens that login once, by hand, and populates the volume so every later job runs unattended.

## 1. Prerequisites

- **A completed SSH handoff** for this owner/project, or at least a registration whose SSH target label matches.
- **A terminal you are watching.** This flow is genuinely interactive and cannot be scripted or automated.
- **An SSH key for the target.** If the target uses a pks-managed key, the key is materialized to a temporary path for the duration of the call and deleted afterwards in a `finally` block.

## 2. Start the login

```bash
pks agentics runner claude-login hetzner
```

The command resolves the target's SSH key, builds a one-off container login command, and launches it. Target and project resolution match `runner status`: omitting `TARGET` auto-selects the only registered target or opens an interactive picker when more than one is registered, and `--project` is required when several projects share the target.

## 3. Complete the login and exit

Follow the login prompts in the container. When you are done, press Ctrl+D to exit — that finishes the session and leaves the credentials in the volume.

## 4. Verify

```bash
pks agentics runner status hetzner
```

The credentials-volume warning should no longer appear.

## 5. Repeat per target and project

The login uses the `project` credential scope — the same default a job uses when its agent definition does not set a scope. Run this once per SSH target and owner/project before dispatching devcontainer jobs there.

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

- **Exit code 1 before any SSH happens.** The pks-managed key could not be materialized. Check the key exists in the pks key store and that the target references the right managed key id.
- **The warning persists after a successful login.** The volume scope did not match. This command uses the `project` scope; a job whose agent definition sets a different credentials scope reads a different volume.
- **Jobs still stall on a login prompt.** Confirm the login ran against the same target and the same owner/project the job dispatches to.
- **The session cannot be automated in CI.** By design. A human must complete the login once per target.

## See also

- [Check a handed-off runner](/tools/pks/agentics/runner/status) — where the credentials-volume warning appears
- [Start the Agentics runner daemon](/tools/pks/agentics/runner/start) — the handoff that creates a remote runner
- [pks agentics CLI reference](/tools/pks/agentics/cli-reference) — every flag and environment variable
