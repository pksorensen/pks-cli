---
id: FT-011
title: Claude Code hooks & managed settings
domain: claude-hooks
status: draft
adrs: []
tests: []
source-files: [src/Commands/Hooks/HooksCommand.cs, src/Commands/Hooks/BaseHookCommand.cs, src/Commands/Hooks/PreToolUseCommand.cs, src/Commands/Hooks/PostToolUseCommand.cs, src/Commands/Hooks/UserPromptSubmitCommand.cs, src/Commands/Hooks/StopCommand.cs, src/Commands/Hooks/SubagentStopCommand.cs, src/Commands/Hooks/PreCompactCommand.cs, src/Commands/Hooks/NotificationCommand.cs, src/Commands/Hooks/HooksMenuCommand.cs, src/Commands/Claude/ManagedSettings/ClaudeManagedSettingsRenderCommand.cs, src/Commands/Claude/ClaudeStatsCommand.cs, src/Commands/Claude/ClaudeUsageCommand.cs, src/Commands/Claude/ClaudeBackupCommand.cs, src/Infrastructure/Services/HooksService.cs]
sessions: [db1daceb-3ef0-4dab-8f2e-1523508106b2, 13973d8f-8531-4dbe-869d-7a36f7c19e81, a0d68f27-c923-4892-868d-20b90f0ec07f, 1c417c4d-838b-400a-b4c8-4f2742d056e7, 60f0c526-f1d9-4416-93f5-186882d634d0]
---

## Description
pks-cli ships a full Claude Code lifecycle hook dispatcher: per-event subcommands
(`pks hooks pre-tool-use|post-tool-use|user-prompt-submit|stop|subagent-stop|pre-compact|notification`)
inherit `BaseHookCommand` and are wired into the user's or project's
`settings.json` via `HooksService.InitializeClaudeCodeHooksAsync`, with explicit
`SettingsScope.Project` vs `User` so the hook block lands next to the code that
owns it. Alongside the dispatcher, `pks claude managed-settings render` produces
the JSON for Claude's enterprise-managed settings file, and `pks claude stats` /
`pks claude usage` scan `~/.claude/projects/**/*.jsonl` to surface per-model
token counts, contribution heat-maps and a 24h-per-hour bar chart of activity.
`pks claude backup` snapshots the same Claude state directory so a corrupted
settings/projects tree can be rolled back. Together they form pks-cli's
"observe + control" surface over a local Claude Code installation — the same
ingest pipeline (`HookNames.UserPromptSubmit` events) is what feeds the `pks brain`
extractor.

## Intent
> From session 60f0c526 (2026-05-03), prompt:
> "I keep removing hooks from ~/.claude/settings.json and they resurface, i think we might have something in pks-cli tool when that runs it adds them which i am not sure is intended"

> From session 60f0c526 (2026-05-03), prompt:
> "but we should use the project claude files and not ~/.claude . I think its fine it reinit if they are gone. I can test it some more, but we need to find out why it ends in user settings instead of project settings."

> From session 1c417c4d (2026-05-03), prompt:
> "dotnet run --project external/pks-cli/src -- claude usage   (scanned 1800+ files)\ndotnet run --project external/pks-cli/src -- claude stats   (scanned only 188 files)\nBy default both should scann all projects in claude folder.\nCould claude usage also print a bar chart for the past 24 hours per hour ?"

> From session a0d68f27 (2026-05-04), prompt:
> "could we bring in this nice little overview to our: \"pks-cli claude stats\" tool ?"

## Key decisions
- **One C# command per hook event, single base class**: `BaseHookCommand` reads
  the Claude JSON payload from stdin, applies common envelope/output rules, and
  each `*Command.cs` only implements the event-specific logic — keeps the
  dispatcher uniform with Claude's hook contract (`HookNames.*` mirrors the
  exact event strings Claude emits).
- **Project scope is the default for installs**: after the May-2026 incident
  where hooks reappeared in `~/.claude/settings.json`, install was changed to
  prefer `SettingsScope.Project` and only fall back to user scope when no
  project file exists — re-init is acceptable, silent leaking into user state
  is not.
- **Render, don't merge, for managed settings**: `claude managed-settings render`
  emits the full JSON the admin should drop in place rather than trying to
  surgically patch an existing managed file — managed settings are an
  enterprise/MDM artefact, so authority belongs to whoever deploys it.
- **`claude usage` and `claude stats` scan all Claude projects by default**:
  the May-2026 bug where `stats` only scanned 188 of 1800+ files was treated
  as a scope regression; both commands now walk every project folder unless
  filtered, and `usage` ships the 24h-per-hour bar chart on top of the
  monthly/model breakdown.
- **Backup as the safety net for the destructive surface**: because hook
  install, managed-settings render and usage scans all read or rewrite under
  `~/.claude`, `claude backup` exists as a one-shot snapshot users can take
  before letting pks-cli touch their Claude state.

## Gotchas / known issues
- Hook re-appearance: any code path that calls `InitializeClaudeCodeHooksAsync`
  without an explicit `scope` will silently default and may re-add hooks the
  user just deleted — always pass `SettingsScope.Project` from new call sites.
- `HookNames.Legacy` exists because older Claude builds used different event
  names; the dispatcher accepts both but only emits the modern names when
  installing — mixing the two in the same `settings.json` confuses Claude.
- Stats vs usage discrepancy historically came from different default scan
  roots; if numbers diverge again, check that both commands are rooted at
  `~/.claude/projects` and not at the current repo's project slug only.
- No tests yet cover the hook install/scope behaviour (`tests: []`); regression
  on the user-vs-project scope bug would only be caught by the user noticing
  hooks resurfacing in `~/.claude/settings.json`.
