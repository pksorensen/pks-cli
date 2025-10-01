# Requirements Specification: First-Time Warning System

## Problem Statement
PKS CLI needs to display a first-time usage warning to inform users that the CLI is made by AI, the generated code has not been validated by humans, AI may make mistakes, and users should run at their own risk. Users should be encouraged to examine the code and report issues on GitHub.

## Solution Overview
Implement a first-time warning system that:
1. Displays a concise disclaimer after the welcome banner
2. Requires explicit user acknowledgment before proceeding
3. Persists acknowledgment in user settings file
4. Respects existing skip conditions for automated scenarios
5. Uses attribute-based command exclusion system

## Functional Requirements

### FR1: Warning Display
- Display warning message after the existing `DisplayWelcomeBanner()` function
- Show only on first-time CLI usage (not every command invocation)
- Include disclaimer about AI-generated code and potential risks
- Include GitHub repository URL for issue reporting: `https://github.com/pksorensen/pks-cli`
- Use consistent Spectre.Console formatting with existing UI patterns

### FR2: User Acknowledgment
- Require explicit user acceptance via `AnsiConsole.Confirm()`
- Continue CLI execution only after acknowledgment
- Store acknowledgment persistently using configuration service

### FR3: Persistence
- Extend ConfigurationService to use file-based storage in user home directory
- Store acknowledgment with key: `"cli.first-time-warning-acknowledged"`
- Settings file location: `~/.pks-cli/settings.json` or similar
- Load settings on CLI startup

### FR4: Skip Conditions
- Respect existing skip logic for MCP stdio transport and hooks commands
- Create `[SkipFirstTimeWarning]` attribute for command-level exclusion
- Apply attribute to appropriate commands (MCP, hooks, automated scenarios)

### FR5: Warning Content
```
⚠️  IMPORTANT DISCLAIMER ⚠️

This CLI tool is powered by AI and generates code automatically.
The generated code has NOT been validated by humans.

AI may make mistakes - use at your own risk.

Please review all generated code before use and report any issues at:
https://github.com/pksorensen/pks-cli

Do you acknowledge and accept these terms? (y/N)
```

## Technical Requirements

### TR1: File Modifications Required

**Program.cs** (`/workspace/pks-cli/src/Program.cs`):
- Line 47: Add first-time warning check after `DisplayWelcomeBanner()`
- Integrate with existing skip logic (lines 23-45)
- Add command attribute detection logic

**Infrastructure/Services.cs** (`/workspace/pks-cli/src/Infrastructure/Services.cs`):
- Extend `ConfigurationService` (lines 50-85) to use file-based persistence
- Implement settings file loading/saving in user home directory
- Maintain backward compatibility with existing configuration patterns

**New Attribute Class**:
- Create `SkipFirstTimeWarningAttribute` class
- Inherit from `System.Attribute`
- Place in appropriate namespace (e.g., `PKS.Infrastructure.Attributes`)

### TR2: Integration Points
- **Configuration Service**: Lines 72-74 in Services.cs - modify `IConfigurationService.SetAsync()`
- **Banner Display**: Line 47 in Program.cs - add warning after `DisplayWelcomeBanner()`
- **Command Registration**: Lines 208-369 in Program.cs - detect attribute presence
- **Skip Logic**: Lines 44-48 in Program.cs - extend existing conditions

### TR3: Implementation Patterns to Follow
- **User Confirmation**: Use `AnsiConsole.Confirm()` pattern from PrdGenerateCommand.cs:103
- **Colored Output**: Use markup patterns: `[red]`, `[yellow]`, `[cyan]` from existing commands
- **Configuration Keys**: Follow dot-notation pattern like `"cluster.endpoint"` from Services.cs:54
- **File Operations**: Follow async patterns used throughout codebase

## Acceptance Criteria

### AC1: First-Time Experience
- [ ] User sees warning on very first CLI command execution
- [ ] Warning displays after welcome banner, before command execution
- [ ] User must explicitly accept terms to proceed
- [ ] Subsequent CLI invocations do not show warning

### AC2: Automated Scenarios
- [ ] MCP stdio transport commands skip warning entirely
- [ ] Hooks commands with JSON flag skip warning
- [ ] Commands marked with `[SkipFirstTimeWarning]` skip warning
- [ ] Existing banner skip logic continues to work

### AC3: Settings Persistence
- [ ] User acknowledgment persists across CLI sessions
- [ ] Settings file created in user home directory
- [ ] Configuration service loads from file on startup
- [ ] Global settings supported as specified

### AC4: Error Handling
- [ ] Graceful handling if settings file cannot be created
- [ ] Fallback behavior for permission issues
- [ ] Warning still displays if configuration fails

### AC5: Content Requirements
- [ ] Warning mentions AI-generated code
- [ ] Warning mentions lack of human validation
- [ ] Warning includes risk disclaimer
- [ ] Warning includes GitHub repository URL
- [ ] Text is concise and clear

## Assumptions

### A1: User Environment
- User has write permissions to home directory
- Standard file system operations are available
- .NET environment provides access to user home directory path

### A2: Configuration
- JSON format acceptable for settings file
- Single configuration file sufficient for all PKS CLI settings
- Backward compatibility maintained for existing configuration usage

### A3: Implementation
- Spectre.Console.Cli attribute system supports custom attributes
- Command reflection can detect custom attributes at runtime
- Existing dependency injection container can resolve enhanced configuration service

## Implementation Hints

### File Structure
```
~/.pks-cli/
└── settings.json
```

### Configuration Service Enhancement
- Use `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)`
- Implement `LoadSettingsAsync()` and `SaveSettingsAsync()` methods
- Maintain in-memory cache for performance

### Attribute Implementation
```csharp
[AttributeUsage(AttributeTargets.Class)]
public class SkipFirstTimeWarningAttribute : Attribute { }
```

### Integration with Program.cs
```csharp
// After DisplayWelcomeBanner() call
if (ShouldShowFirstTimeWarning(commandContext))
{
    await DisplayFirstTimeWarning();
}
```