# Phase 2: Discovery Questions

Based on the analysis of GitHub Issue #4 and the PKS CLI codebase, here are the five most important questions to understand the problem space:

## Q1: Should the first-time warning be shown for every CLI command invocation?
**Default if unknown:** No (warning should only show once, not on every command)

The issue requests a "first time usage" warning, which implies it should only appear on the user's initial interaction with the CLI, not repeatedly.

## Q2: Should the warning require explicit user acknowledgment/acceptance before proceeding?
**Default if unknown:** Yes (important disclaimers should require explicit acknowledgment)

Since this is a disclaimer about AI-generated code and potential risks, user acknowledgment ensures they understand the implications.

## Q3: Should the warning be skippable for automated scenarios like MCP stdio transport or hook commands?
**Default if unknown:** Yes (automated tools shouldn't be interrupted by interactive warnings)

The codebase already has logic to skip banners for MCP stdio and hook commands, suggesting non-interactive scenarios should be handled gracefully.

## Q4: Should the warning display alongside the existing welcome banner or replace it?
**Default if unknown:** Replace it (the warning is more important than the welcome message for first-time users)

For first-time users, the safety disclaimer should take precedence over the decorative welcome banner.

## Q5: Should the user's acknowledgment be persisted across different project directories?
**Default if unknown:** Yes (user preference should be global, not per-project)

The warning is about the CLI tool itself, not specific projects, so acknowledgment should apply system-wide.