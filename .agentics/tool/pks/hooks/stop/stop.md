---
title: "Block a turn on a failing lint"
description: "Configure the Stop hook so Claude Code cannot finish a turn while your lint command fails, and hand the failure output back to the agent to fix."
tags: [how-to, cli, claude-code, quality-gate]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks hooks"
---

The Stop handler is the one hook in pks that enforces something — or would, once it works. Configure a lint command, and when Claude Code tries to end a turn, pks is meant to run that command first — a non-zero exit blocks the stop and returns the output so the agent fixes the problem before finishing.

> **Known limitation.** As of the current release, the lint command you set in the menu below is **not written to disk** — it only ever exists in the memory of the `pks hooks` process that set it. Claude Code invokes `pks hooks stop` as a fresh, separate process for every Stop event, and that new process reads its configuration from disk, so it never sees the command you configured. In practice the quality gate described on this page does not currently take effect. The rest of this page documents the intended workflow; the caveat in [step 3](#3-enable-a-lint-command) explains exactly where it breaks.

## 1. Prerequisites

- **A lint command that runs under `bash -c`** in the directory Claude Code operates in. Shell aliases and PATH entries that exist only in your interactive shell will not be there.
- **A command that finishes well under 60 seconds.** The handler kills anything slower and treats the timeout as a failure.
- **An interactive terminal.** The configuration menu prompts.

## 2. Open the menu

```bash
pks hooks
```

Running `pks hooks` with no subcommand opens the configuration menu. It currently offers one area: quality — the lint check on stop.

## 3. Enable a lint command

The menu offers a preset list plus a free-text entry:

- `npm run lint`
- `dotnet build --no-restore`
- `npx eslint .`
- Custom — type any command

The choice is written to the configuration key `hooks:quality:lint_command` — but only in memory, for the lifetime of this `pks hooks` process. The underlying config service persists a setting to disk only when it's written with global scope or under a `cli.` prefixed key; `hooks:quality:lint_command` is neither, so nothing is saved to `~/.pks-cli/settings.json` or any other file. The moment this process exits, the value is gone. **The next `pks hooks stop` invocation — which is what Claude Code actually runs, as a brand-new process — reads that key back as unset and proceeds with no lint check.** There is currently no supported way to make the choice survive across invocations.

If the current project's `.claude/settings.json` does not already reference `pks hooks stop`, the menu registers the Stop hook for you at that moment, using the same non-force logic as [`pks hooks init`](/tools/pks/hooks/init). That registration itself does persist (it's a direct file write, not a config-service setting) — it's only the lint command that fails to survive.

## 4. Understand what happens on stop

When Claude Code fires the Stop event, the handler runs in this order:

1. Reads the event payload from stdin.
2. If the payload carries `stop_hook_active: true`, it proceeds immediately. That field is Claude Code's re-entrancy guard and prevents a stop loop.
3. Reads `hooks:quality:lint_command`. If it is unset, it proceeds with no action — and because the key never persists (see step 3 above), it is unset in the fresh process Claude Code spawns for every Stop event, even after you've "enabled" it in the menu.
4. Runs the command with `bash -c` in the current working directory, capturing stdout and stderr together, with a 60-second timeout.
5. On exit code zero, the stop is allowed. On any other exit code — including a timeout kill — it returns a block decision with the captured output embedded in the reason.

A block tells Claude Code not to finish, and surfaces the lint output so the failure can be addressed in the same turn.

## 5. Verify

Simulate the event by piping an empty payload:

```bash
echo '{}' | pks hooks stop
```

`echo | pks hooks stop` is itself a new process, so — given the non-persistence caveat above — this will read `hooks:quality:lint_command` as unset and proceed silently even right after you "enabled" it in the menu, regardless of whether your lint command would pass or fail. A silent proceed here is not evidence the gate is working; it is currently the only outcome this command can produce.

## 6. Disable it

Re-open `pks hooks` and choose the disable option. That deletes the in-memory configuration key for that process — moot in practice, since (per the caveat above) the key was never written to disk in the first place, so every fresh process already behaves as if it were disabled. It does **not** remove the `Stop` entry from `settings.json`, so `pks hooks stop` still runs on every stop — it just returns immediately because no lint command is configured. To remove the invocation entirely, delete the `Stop` entry from the settings file by hand.

## 7. Next steps

- [Register pks hooks in a project](/tools/pks/hooks/init) — wire the Stop entry explicitly, or into user scope
- [Hook events and their handlers](/tools/pks/hooks/events) — the other six events and what they do
- [pks hooks reference](/tools/pks/hooks/reference) — behavior and options for every subcommand

## Troubleshooting

**Every stop is blocked, but the lint passes in my terminal.** The command runs through `bash -c`, not your login shell. Aliases, shell functions, and PATH additions made in an interactive rc file are absent. Use an absolute path or a package-manager script.

**Stops are blocked with a timeout message.** The lint command exceeded 60 seconds and the process tree was killed. That is treated as failure. Narrow the command's scope.

**The lint command is set but nothing runs.** Two possible causes, check both. First and most likely: the lint command does not currently persist to disk (see the caveat in [step 3](#3-enable-a-lint-command)), so any process other than the one that set it — including every real Stop event Claude Code fires — reads it as unset. Second: the `Stop` entry is missing from the `settings.json` that Claude Code actually loads. Run `pks hooks init` in the right scope and re-check the file; this fixes the second cause only.

**Claude Code loops on stopping.** The handler relies on `stop_hook_active` in the incoming payload to break the loop. A malformed payload is swallowed and treated as if the field were absent, which re-runs the lint. Confirm the lint command is deterministic and can actually be made to pass.

## See also

- [pks hooks](/tools/pks/hooks) — the group overview and mental model
- [Register pks hooks in a project](/tools/pks/hooks/init) — how the `Stop` entry gets into `settings.json`
- [Hook events and their handlers](/tools/pks/hooks/events) — the six pass-through handlers alongside this one
- [pks hooks reference](/tools/pks/hooks/reference) — the configuration key this page sets and how it is read
