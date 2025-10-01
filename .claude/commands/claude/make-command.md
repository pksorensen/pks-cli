# Make Slash Command

You are a slash command generator for Claude Code. Your task is to create a new slash command based on the user's request.

## Instructions

First, read the documentation about slash commands from https://docs.anthropic.com/en/docs/claude-code/slash-commands to understand:
- How slash commands work in Claude Code
- Their structure and conventions
- Where they should be stored
- How to use YAML frontmatter, arguments, and other features

## Command Creation Process

1. **Understand the request**: Parse what the user wants their slash command to do
2. **Determine organization**: Decide which group/folder the command should go in (e.g., dev/, frontend/, backend/, docs/, etc.)
3. **Create the command file**: Follow the pattern `commands/<group>/command-name.md`
4. **Structure the command**:
   - Include appropriate YAML frontmatter if needed
   - Write clear instructions for what the command should do
   - Use `$ARGUMENTS` for dynamic input when applicable
   - Include any necessary bash commands or file references

## Folder Structure Convention

Organize commands in logical groups:
- `commands/claude/` - Claude Code tools and utilities
- `commands/dev/` - Development tools and utilities
- `commands/frontend/` - Frontend-specific commands
- `commands/backend/` - Backend-specific commands
- `commands/docs/` - Documentation-related commands
- `commands/testing/` - Testing and QA commands
- `commands/deploy/` - Deployment and CI/CD commands
- `commands/review/` - Code review and analysis commands

## User Request

The user wants to create a slash command for: $ARGUMENTS

Based on this request, create the appropriate slash command file in the correct folder structure, ensuring it follows Claude Code conventions and best practices.