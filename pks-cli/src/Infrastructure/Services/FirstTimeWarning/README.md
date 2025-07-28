# First-Time Warning System Architecture

## Overview

The First-Time Warning System provides a one-time disclaimer to users about AI-generated code when they first interact with PKS CLI. The system is designed to be non-invasive, respecting existing skip conditions and allowing command-level exclusion.

## System Components

### 1. SkipFirstTimeWarningAttribute
**Location**: `/Infrastructure/Attributes/SkipFirstTimeWarningAttribute.cs`  
**Purpose**: Marks commands that should skip the warning display  
**Status**: ✅ Implemented

```csharp
[SkipFirstTimeWarning("Automated MCP integration")]
public class McpCommand : Command<McpSettings>
{
    // Command implementation
}
```

### 2. IFirstTimeWarningService
**Location**: `/Infrastructure/Services/IFirstTimeWarningService.cs`  
**Purpose**: Contract for warning system operations  
**Status**: ✅ Implemented

**Key Methods**:
- `ShouldShowWarningAsync()` - Determines if warning should be displayed
- `DisplayWarningAsync()` - Shows the warning dialog
- `MarkWarningAcknowledgedAsync()` - Persists user acknowledgment
- `IsWarningAcknowledgedAsync()` - Quick acknowledgment check

### 3. FirstTimeWarningService (Implementation)
**Location**: `/Infrastructure/Services/FirstTimeWarningService.cs`  
**Purpose**: Concrete implementation of warning logic  
**Status**: 🔄 To be implemented by Backend Agent

**Responsibilities**:
- Integrates with existing skip logic (MCP stdio, hooks with JSON flags)
- Detects SkipFirstTimeWarningAttribute via reflection
- Displays warning using Spectre.Console UI patterns
- Manages user acknowledgment persistence

### 4. Enhanced ConfigurationService
**Location**: `/Infrastructure/Services.cs`  
**Purpose**: File-based persistence for user settings  
**Status**: 🔄 To be enhanced by Backend Agent

**Enhancements**:
- File-based storage in `~/.pks-cli/settings.json`
- In-memory caching for performance
- Thread-safe operations with proper locking
- Graceful fallback to memory-only storage

### 5. Program.cs Integration
**Location**: `/Program.cs`  
**Purpose**: Integration point for warning display  
**Status**: 🔄 To be integrated by Frontend Agent

**Integration Point**: After `DisplayWelcomeBanner()` call (around line 47)

## Warning Content

The warning displays a clear disclaimer about AI-generated code:

```
┌─────────────────────────────────────────────────────────────────┐
│                      ⚠️  IMPORTANT DISCLAIMER ⚠️                     │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│ This CLI tool is powered by AI and generates code automatically.│
│ The generated code has NOT been validated by humans.            │
│                                                                 │
│ AI may make mistakes - use at your own risk.                   │
│                                                                 │
│ Please review all generated code before use and report any     │
│ issues at: https://github.com/pksorensen/pks-cli               │
│                                                                 │
│ Do you acknowledge and accept these terms?                     │
└─────────────────────────────────────────────────────────────────┘
```

## Skip Conditions

The warning is skipped in these scenarios:

1. **Already Acknowledged**: User has accepted terms in previous session
2. **MCP Stdio Transport**: `--transport stdio` commands
3. **Hooks with JSON**: Hook commands with `--json` flag or event detection
4. **Attribute Marked**: Commands decorated with `[SkipFirstTimeWarning]`

## Commands to Mark with Skip Attribute

Based on the architecture analysis, these commands should be marked:

### MCP Commands
- `McpCommand` - Automated MCP integration scenarios

### Hook Commands
- `PreToolUseCommand` - Claude Code pre-tool hooks
- `PostToolUseCommand` - Claude Code post-tool hooks  
- `PreCompactCommand` - Pre-compact hooks
- `StopCommand` - Stop hooks
- `SubagentStopCommand` - Subagent stop hooks
- `UserPromptSubmitCommand` - User prompt submission hooks
- `NotificationCommand` - Notification hooks

### Other Automated Commands
- Any future background or automated commands

## Integration Flow

```
CLI Startup
    ↓
Command Line Parsing
    ↓
Skip Logic Evaluation
    ↓
Welcome Banner Display
    ↓
First-Time Warning Check ← NEW INTEGRATION POINT
    ↓
Command Execution
```

## Error Handling Strategy

1. **Configuration Service Failures**: Graceful fallback to in-memory storage
2. **File Permission Issues**: Log warning but continue operation
3. **UI Interaction Failures**: Assume user declined and exit gracefully
4. **Reflection Failures**: Log warning and assume no skip attribute

## Testing Strategy

### Unit Tests Required
- Configuration service file operations
- Warning service skip logic evaluation
- Attribute detection mechanisms
- Error handling scenarios

### Integration Tests Required
- End-to-end warning flow
- Skip condition integration
- Cross-session persistence

## Implementation Dependencies

- **Spectre.Console**: UI components and user interaction
- **System.Text.Json**: Settings file serialization
- **System.Reflection**: Attribute detection
- **Microsoft.Extensions.DependencyInjection**: Service registration

## Service Registration

The FirstTimeWarningService will be registered in the DI container:

```csharp
// In Infrastructure/Services.cs
services.AddSingleton<IFirstTimeWarningService, FirstTimeWarningService>();
```

## Agent Responsibilities

| Agent | Responsibilities | Status |
|-------|-----------------|--------|
| **Architecture Agent** | Interface definitions, attribute class, documentation | ✅ Complete |
| **Backend Agent** | Service implementation, configuration enhancement | 🔄 In Progress |
| **Frontend Agent** | Program.cs integration, command attribute marking | 🔄 Pending |
| **Test Agent** | Comprehensive test coverage | 🔄 Pending |

## File Locations Summary

```
src/
├── Infrastructure/
│   ├── Attributes/
│   │   └── SkipFirstTimeWarningAttribute.cs         ✅ Complete
│   └── Services/
│       ├── IFirstTimeWarningService.cs              ✅ Complete
│       ├── FirstTimeWarningService.cs               🔄 Backend Agent
│       └── FirstTimeWarning/
│           └── README.md                            ✅ Complete
├── Program.cs                                       🔄 Frontend Agent
└── Infrastructure/Services.cs                      🔄 Backend Agent
```

## Configuration Keys

- `cli.first-time-warning-acknowledged`: Boolean flag for acknowledgment status
- Settings stored in: `~/.pks-cli/settings.json`

## Security Considerations

- Settings file location validated within user home directory
- JSON content sanitized before deserialization
- Atomic file operations to prevent corruption
- No sensitive user information stored

This architecture provides a clean, extensible foundation for the first-time warning system while maintaining backward compatibility and following established patterns in the PKS CLI codebase.