---
title: "pks hooks reference"
description: "Complete command and option reference for pks hooks — the setup commands, the seven Claude Code event handlers, and the three shared flags."
tags: [reference, cli, claude-code, hooks]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks hooks <command> [options]"
examples:
  - command: "pks hooks init --force"
    description: "Merge handlers without confirmation prompts"
  - command: "pks hooks init --scope user"
    description: "Write to the user-global Claude settings file"
  - command: "pks hooks list"
    description: "Print the supported hook events"
  - command: "pks hooks"
    description: "Open the interactive quality-gate menu"
---

`pks hooks` is the pks command group that integrates with Claude Code's hook system. It writes hook wiring into a Claude Code `settings.json` and provides the handler executables that wiring points at. All operations are local: file reads and writes, a configuration key, and a subprocess. No network calls and no credentials are involved.

## Synopsis

```text
pks hooks <command> [options]
```

```text
init                  Merge pks hook entries into a Claude Code settings.json
list                  Print the hook events pks provides handlers for
pre-tool-use          PreToolUse handler (pass-through)
post-tool-use         PostToolUse handler (pass-through)
user-prompt-submit    UserPromptSubmit handler (pass-through)
stop                  Stop handler — runs the configured lint command
notification          Notification handler (payload dump)
subagent-stop         SubagentStop handler (payload dump)
pre-compact           PreCompact handler (payload dump)
```

### Shared options

All subcommands, and the group itself, share one option set.

| Flag | Default | Description |
| --- | --- | --- |
| `-f`, `--force` | `false` | Overwrite or update existing hooks configuration without confirmation. Read by `init` and by the interactive menu's auto-registration path. |
| `-s`, `--scope <user\|project\|local>` | `project` | Which Claude Code settings file to read or write. Read by `init` and by the interactive menu's auto-registration path. |
| `-j`, `--json` | `false` | Declared on every subcommand. Read only by the shared handler error path, and by `post-tool-use` and `user-prompt-submit` to suppress their debug console dump. |

Scope resolution: `user` targets `~/.claude/settings.json`; `project` targets `<cwd>/.claude/settings.json`; `local` targets `./.claude/settings.json` and resolves to the same relative path as `project`.

The `--json` flag is inert on `init`, `list`, `notification`, `subagent-stop`, and `pre-compact`. Their output is unconditional.

## hooks

The group's default command. With no subcommand, it opens an interactive menu offering one area: quality, the lint check on stop. From there you enable, change, or disable the lint command, held under the configuration key `hooks:quality:lint_command`.

> **Known limitation.** That key is only ever set in memory. The configuration service persists a setting to disk only when it's written with global scope or under a `cli.`-prefixed key, and `hooks:quality:lint_command` is neither — so nothing is written to `~/.pks-cli/settings.json`, and the value disappears when the `pks hooks` process exits. Every subsequent `pks hooks stop` invocation, including the ones Claude Code actually fires, is a separate process that reads the key back as unset. The quality gate described below does not currently take effect across invocations.

The presets offered are `npm run lint`, `dotnet build --no-restore`, `npx eslint .`, and a free-text custom entry.

When you enable or change a lint command and the current project's `settings.json` does not already reference `pks hooks stop`, the menu runs the same non-force logic as `init` to register the Stop entry. That registration is a direct file write and does persist — it's only the lint command itself that doesn't.

Disabling deletes the in-memory key for that process — moot in practice, since the key was never persisted to begin with. The `Stop` entry stays in `settings.json`, and the handler continues to run and return immediately.

This command is fully interactive and prompts unconditionally. It is not usable in a non-interactive session.

## init

Merges pks hook entries into the settings file selected by `--scope`. For each known hook name it merges an entry of the form `{matcher, hooks: [{type: "command", command: "<pks-exe> hooks <event>"}]}` into the existing `hooks` object rather than replacing the object. Existing non-pks entries for the same event are preserved alongside.

`init` also migrates legacy camelCase hook keys written by an older pks version — `preToolUse`, `stop`, and siblings — to the PascalCase names Claude Code expects. The `.claude` directory is created when missing.

`<pks-exe>` is resolved at run time from the executing process: the absolute path of a self-contained binary, or `dotnet <dll-path>` when pks was started through `dotnet run`. The hook therefore works without pks being on `PATH`, and breaks if the binary later moves or the install method changes. Re-run with `--force` after any install-method change.

Without `--force`, an existing `hooks` block triggers a confirmation prompt, and existing pks entries trigger a second one. Both require an explicit yes, so a non-interactive run against a repo that already has hooks exits without writing.

`init` writes a `PostCompact` entry pointing at `pks hooks post-compact`. That subcommand does not exist. Claude Code does not currently emit a PostCompact event, so the entry is unreachable rather than broken.

```bash
pks hooks init --scope user --force
```

## list

Prints a static table of the seven hook events pks provides handlers for, with the command, event type, and a description. It reads no settings file and reports nothing about the current configuration. `PostCompact` is not listed, because no handler exists for it.

## pre-tool-use

Handler for the PreToolUse event. Reads the tool-call payload from stdin and always returns a proceed decision. It never blocks or approves a tool call. A plain proceed produces no stdout output.

`init` registers this handler with a `Bash` matcher, so it fires only for Bash tool calls.

## post-tool-use

Handler for the PostToolUse event. Reads the tool-result payload from stdin and always returns a proceed decision. When `--json` is absent, it additionally prints a debug block containing the raw stdin content and the working directory.

`init` registers this handler with a `Bash` matcher.

## user-prompt-submit

Handler for the UserPromptSubmit event. Reads the prompt payload from stdin and always returns a proceed decision. No content filtering is applied. When `--json` is absent, it prints the first 200 characters of the prompt plus the working directory; the preview is truncated deliberately.

`init` registers this handler with no matcher, so it fires for every prompt.

## stop

Handler for the Stop event, and the only handler with enforcement behavior.

It reads stdin and returns a proceed decision immediately when the payload carries `stop_hook_active: true`, Claude Code's re-entrancy guard against stop loops. A payload that fails to parse is swallowed and processing continues as if the field were absent.

Otherwise it reads `hooks:quality:lint_command`. When that key is unset, it proceeds with no action — which, per the known limitation above, is what every real invocation currently observes, since the key never persists to the process Claude Code spawns. When it is set (i.e. within the same `pks hooks` process that just configured it), the command is executed through `bash -c` in the current working directory with stdout and stderr captured together and a 60-second timeout. A timeout kills the entire process tree and is treated as exit code 1.

A zero exit allows the stop. Any non-zero exit returns a block decision with the captured output embedded in the reason, which tells Claude Code to refuse to finish and surfaces the failure.

```bash
echo '{}' | pks hooks stop
```

The gate only takes effect when both halves exist: the stored lint command, and a `Stop` entry in the settings file Claude Code loads.

## notification / subagent-stop / pre-compact

Handlers for the Notification, SubagentStop, and PreCompact events. All three share one shape: they dump the full environment variable list, the command-line arguments, and raw stdin to the console, then exit zero. They return no decision and block nothing.

The dump is unconditional. `--json` does not suppress it.

> **Note.** Because the dump includes every environment variable, any secret present in the process environment is printed each time one of these hooks fires. Remove the corresponding entries from `settings.json` if the output is captured anywhere durable.

## See also

- [pks hooks](/tools/pks/hooks) — group overview and mental model
- [Register pks hooks in a project](/tools/pks/hooks/init) — step-by-step wiring with scope guidance
- [Block a turn on a failing lint](/tools/pks/hooks/stop) — configuring and troubleshooting the quality gate
- [Hook events and their handlers](/tools/pks/hooks/events) — event-by-event behavior and matchers
