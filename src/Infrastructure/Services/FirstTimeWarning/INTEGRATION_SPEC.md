# First-Time Warning Integration Specification

## Backend Agent Tasks

### 1. Enhance ConfigurationService in Services.cs

**Target Lines**: 72-74 in `/Infrastructure/Services.cs`

**Required Changes**:
- Add file-based persistence using `~/.pks-cli/settings.json`
- Implement `LoadSettingsAsync()` and `SaveSettingsAsync()` methods
- Add thread-safe file operations with SemaphoreSlim
- Maintain backward compatibility with existing patterns

**Configuration Service Enhancement Pattern**:
```csharp
public class ConfigurationService : IConfigurationService
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".pks-cli",
        "settings.json"
    );
    
    private readonly Dictionary<string, string> _cache = new();
    private bool _isLoaded = false;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    
    // Existing methods preserved for backward compatibility
    // New methods for file persistence
}
```

### 2. Create FirstTimeWarningService Implementation

**Target File**: `/Infrastructure/Services/FirstTimeWarningService.cs`

**Key Implementation Points**:
- Dependency on `IConfigurationService` for persistence
- Skip logic integration with existing Program.cs conditions
- Attribute detection via reflection for `SkipFirstTimeWarningAttribute`
- Spectre.Console UI integration matching existing patterns

**Warning Content Constants**:
```csharp
private const string WarningAcknowledgedKey = "cli.first-time-warning-acknowledged";
private const string WarningText = "[yellow]This CLI tool is powered by AI and generates code automatically.[/]\n" +
    "[yellow]The generated code has NOT been validated by humans.[/]\n\n" +
    "[red]AI may make mistakes - use at your own risk.[/]\n\n" +
    "Please review all generated code before use and report any issues at:\n" +
    "[cyan]https://github.com/pksorensen/pks-cli[/]";
```

### 3. Service Registration

**Target**: Service registration section in `Services.cs`

**Add Registration**:
```csharp
services.AddSingleton<IFirstTimeWarningService, FirstTimeWarningService>();
```

## Frontend Agent Tasks

### 1. Program.cs Integration

**Target Lines**: Around line 47 in `/Program.cs`

**Current Code** (approximate):
```csharp
// Display welcome banner with fancy ASCII art (unless we should skip it)
if (!isMcpStdio && !(isHooksCommand && (hasJsonFlag || isHookEventCommand)))
{
    DisplayWelcomeBanner();
}
```

**Enhanced Code**:
```csharp
// Display welcome banner with fancy ASCII art (unless we should skip it)
if (!isMcpStdio && !(isHooksCommand && (hasJsonFlag || isHookEventCommand)))
{
    DisplayWelcomeBanner();
    
    // Check and display first-time warning
    await DisplayFirstTimeWarningIfNeededAsync(args, context);
}
```

**New Method to Add**:
```csharp
private static async Task DisplayFirstTimeWarningIfNeededAsync(string[] commandArgs, CommandContext? context = null)
{
    try
    {
        var serviceProvider = app.Configuration.Settings.Registrar.Build();
        var warningService = serviceProvider.GetService<IFirstTimeWarningService>();
        
        if (warningService != null)
        {
            var shouldShow = await warningService.ShouldShowWarningAsync(context, commandArgs);
            if (shouldShow)
            {
                var acknowledged = await warningService.DisplayWarningAsync();
                if (acknowledged)
                {
                    await warningService.MarkWarningAcknowledgedAsync();
                }
                else
                {
                    Environment.Exit(1); // User declined terms
                }
            }
        }
    }
    catch (Exception ex)
    {
        // Log error but don't block CLI execution
        Console.WriteLine($"Warning: Could not process first-time warning: {ex.Message}");
    }
}
```

### 2. Command Attribute Application

**Commands to Mark with `[SkipFirstTimeWarning]`**:

#### MCP Commands
**File**: `/Commands/Mcp/McpCommand.cs`
```csharp
[SkipFirstTimeWarning("Automated MCP integration")]
public class McpCommand : Command<McpSettings>
```

#### Hook Commands
**Files in** `/Commands/Hooks/`:
- `PreToolUseCommand.cs` - `[SkipFirstTimeWarning("Claude Code hook event")]`
- `PostToolUseCommand.cs` - `[SkipFirstTimeWarning("Claude Code hook event")]`
- `PreCompactCommand.cs` - `[SkipFirstTimeWarning("Claude Code hook event")]`
- `StopCommand.cs` - `[SkipFirstTimeWarning("Claude Code hook event")]`
- `SubagentStopCommand.cs` - `[SkipFirstTimeWarning("Claude Code hook event")]`
- `UserPromptSubmitCommand.cs` - `[SkipFirstTimeWarning("Claude Code hook event")]`
- `NotificationCommand.cs` - `[SkipFirstTimeWarning("Claude Code hook event")]`

**Pattern for each command**:
```csharp
using PKS.Infrastructure.Attributes;

[SkipFirstTimeWarning("Claude Code hook event")]
public class PreToolUseCommand : BaseHookCommand<PreToolUseSettings>
```

## Test Agent Tasks

### 1. Unit Tests Required

**ConfigurationService Tests** (`/tests/Infrastructure/Services/ConfigurationServiceTests.cs`):
- File-based persistence operations
- Thread safety and concurrent access
- Error handling and fallback scenarios
- Backward compatibility verification

**FirstTimeWarningService Tests** (`/tests/Infrastructure/Services/FirstTimeWarningServiceTests.cs`):
- Skip condition evaluation logic
- Attribute detection mechanisms
- Configuration service integration
- UI interaction scenarios

**Attribute System Tests** (`/tests/Infrastructure/AttributeSystemTests.cs`):
- Reflection-based attribute detection
- Command marking verification
- Edge cases and error scenarios

### 2. Integration Tests Required

**End-to-End Tests** (`/tests/Integration/FirstTimeWarningIntegrationTests.cs`):
- Complete warning flow from CLI startup
- Cross-session persistence verification
- Skip condition integration testing

## Skip Logic Integration Points

### Existing Skip Conditions (Lines 23-45 in Program.cs)
```csharp
// Existing conditions to preserve:
var isMcpStdio = args.Any(arg => arg.Equals("--transport", StringComparison.OrdinalIgnoreCase)) &&
                 args.Any(arg => arg.Equals("stdio", StringComparison.OrdinalIgnoreCase));

var isHooksCommand = args.Length > 0 && args[0].Equals("hooks", StringComparison.OrdinalIgnoreCase);
var hasJsonFlag = args.Any(arg => arg.Equals("--json", StringComparison.OrdinalIgnoreCase));
var isHookEventCommand = args.Length >= 2 && new[] { "pre-tool-use", "post-tool-use", "user-prompt-submit", "stop", "subagent-stop", "notification", "pre-compact" }
    .Contains(args[1], StringComparer.OrdinalIgnoreCase);
```

### New Warning Skip Logic
The FirstTimeWarningService should respect these existing conditions plus:
- Command-level `SkipFirstTimeWarningAttribute`
- Previously acknowledged warning status

## Configuration File Structure

**Location**: `~/.pks-cli/settings.json`

**Example Content**:
```json
{
  "cluster.endpoint": "https://k8s.production.com",
  "namespace.default": "myapp-production",
  "cli.first-time-warning-acknowledged": "true"
}
```

## Error Handling Requirements

### File Operations
- Create directory if `~/.pks-cli/` doesn't exist
- Handle permission errors gracefully
- Use atomic write operations (temp file + move)
- Implement proper file locking

### Warning Display
- Handle Spectre.Console interaction failures
- Provide fallback text-based confirmation if needed
- Exit gracefully if user declines terms

### Service Resolution
- Handle DI container resolution failures
- Log errors but don't block CLI operation
- Provide meaningful error messages

## UI Patterns to Follow

### Color Scheme
- Use `[yellow]` for warnings
- Use `[red]` for critical information
- Use `[cyan]` for URLs and links
- Use `[green]` for success messages

### Panel Formatting
```csharp
var panel = new Panel(warningText)
{
    Header = new PanelHeader("[bold red]⚠️  IMPORTANT DISCLAIMER ⚠️[/]"),
    Border = BoxBorder.Double,
    BorderStyle = Style.Parse("red")
};
```

### User Confirmation
```csharp
return AnsiConsole.Confirm("Do you acknowledge and accept these terms?", defaultValue: false);
```

## Validation Checklist

### Backend Agent Completion Criteria
- [ ] ConfigurationService enhanced with file persistence
- [ ] FirstTimeWarningService implemented with all required methods
- [ ] Service properly registered in DI container
- [ ] Error handling implemented for all failure scenarios
- [ ] Backward compatibility maintained

### Frontend Agent Completion Criteria
- [ ] Program.cs integration point added after banner display
- [ ] All MCP commands marked with skip attribute
- [ ] All hook commands marked with skip attribute
- [ ] Integration respects existing skip logic
- [ ] Proper error handling in integration code

### Test Agent Completion Criteria
- [ ] Unit tests cover all service methods
- [ ] Integration tests verify end-to-end flow
- [ ] Error scenario testing implemented
- [ ] Cross-session persistence verified
- [ ] Attribute detection tested

## Implementation Dependencies

### Required Using Statements
```csharp
using PKS.Infrastructure.Attributes;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Reflection;
using System.Text.Json;
```

### NuGet Packages
- No new packages required
- Existing dependencies sufficient:
  - Spectre.Console (UI components)
  - System.Text.Json (configuration serialization)
  - Microsoft.Extensions.DependencyInjection (service registration)

This specification provides detailed guidance for each agent to implement their portion of the first-time warning system while maintaining coordination and avoiding conflicts.