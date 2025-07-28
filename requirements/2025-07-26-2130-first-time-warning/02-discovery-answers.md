# Phase 2: Discovery Answers

## Q1: Should the first-time warning be shown for every CLI command invocation?
**Answer:** No - Just the first time a user uses the CLI

## Q2: Should the warning require explicit user acknowledgment/acceptance before proceeding?
**Answer:** Yes - Make them accept it

## Q3: Should the warning be skippable for automated scenarios like MCP stdio transport or hook commands?
**Answer:** Yes - There are commands where it should not be shown. Can we add an attribute that can be added to commands to indicate if the warning can be omitted

## Q4: Should the warning display alongside the existing welcome banner or replace it?
**Answer:** Show it together with the banner as the last thing

## Q5: Should the user's acknowledgment be persisted across different project directories?
**Answer:** Yes - It's a warning per CLI tool itself