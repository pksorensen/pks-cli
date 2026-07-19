---
title: "Register pks hooks in a project"
description: "Run pks hooks init to merge pks handlers into a Claude Code settings.json, pick the right scope, and verify the wiring survives a reinstall."
tags: [how-to, cli, claude-code, setup]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks hooks init --scope project"
---

Get pks wired into Claude Code in one command: run `init`, choose which `settings.json` to write, and confirm the handler entries landed. After this, Claude Code invokes `pks hooks <event>` at each lifecycle point.

Wiring alone changes no behavior — every handler except `stop` is pass-through, and `stop` stays inert until you configure a lint command. See [Block a turn on a failing lint](/tools/pks/hooks/stop) for that step.

## 1. Prerequisites

- **pks installed.** Any install method works; the command path baked into `settings.json` is derived from the running process.
- **A project directory.** For the default project scope, run from the repository root — the file is written relative to the current working directory.
- **An interactive terminal**, unless you pass `--force`. A non-force run against a file that already has a `hooks` block asks for confirmation.

## 2. Choose a scope

`--scope` decides which file is written.

| Flag | Description |
| --- | --- |
| `--scope user` | Writes `~/.claude/settings.json`, so every project on the machine picks up the handlers. |
| `--scope project` | Writes `<cwd>/.claude/settings.json`. This is the default. |
| `--scope local` | Writes `./.claude/settings.json`; resolves to the same relative path as project scope. |

The `.claude` directory is created if it doesn't exist.

## 3. Run init

```bash
pks hooks init
```

The command merges an entry for each known hook event into the file's `hooks` object. Existing entries for the same event that are not pks commands are preserved next to the new ones.

For the machine-wide variant:

```bash
pks hooks init --scope user
```

## 4. Handle an existing hooks block

If the target file already has a `hooks` object, `init` prompts before merging, and prompts a second time if some of the existing entries are already pks commands. Both prompts require an explicit yes. Decline either one and nothing is written.

To skip both prompts — required for any unattended run — pass `--force`:

```bash
pks hooks init --force
```

Without `--force`, an unattended run against a repo that already has hooks prints a cancellation message and exits without writing.

## 5. Verify

Read the file you targeted and look for the pks entries:

```bash
cat .claude/settings.json
```

You should see a `hooks` object whose keys are PascalCase event names — `PreToolUse`, `PostToolUse`, `UserPromptSubmit`, `Stop`, `Notification`, `SubagentStop`, `PreCompact` — each containing a command entry that ends in `hooks <event>`. `PreToolUse` and `PostToolUse` carry a `Bash` matcher, so those two fire only for Bash tool calls.

If the file previously used the old camelCase keys (`preToolUse`, `stop`), they are migrated to the PascalCase names in the same run.

> **Note.** `init` also writes a `PostCompact` entry pointing at `pks hooks post-compact`. No such subcommand exists. Claude Code does not currently emit a PostCompact event, so the entry is inert — leave it or delete it by hand.

## 6. Next steps

- [Block a turn on a failing lint](/tools/pks/hooks/stop) — turn the Stop handler into a real quality gate
- [Hook events and their handlers](/tools/pks/hooks/events) — what each registered entry actually does when it fires
- [pks hooks reference](/tools/pks/hooks/reference) — the complete option surface

## Troubleshooting

**Nothing was written and the output says the operation was cancelled.** The file already had a `hooks` block and a confirmation was declined or could not be answered. Re-run with `--force`.

**The hook stops working after reinstalling pks.** The command string in `settings.json` is captured from the executing process when `init` runs — the absolute path of a self-contained binary, or `dotnet <dll-path>` when pks was started with `dotnet run`. Switching install methods, moving the binary, or having run `init` from a throwaway checkout leaves a stale path behind. Re-run `pks hooks init --force` after any install-method change.

**Handlers fire in one project but not another.** Project and local scope write into the current directory. Use `--scope user` if you want them everywhere.

## See also

- [pks hooks](/tools/pks/hooks) — the group overview and mental model
- [Block a turn on a failing lint](/tools/pks/hooks/stop) — configure the one handler that enforces something
- [Hook events and their handlers](/tools/pks/hooks/events) — what each of the seven events does today
- [pks hooks reference](/tools/pks/hooks/reference) — per-command behavior and the shared option table
