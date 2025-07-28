# First-Time Warning System Architecture Design

## Executive Summary

This document outlines the complete system architecture for implementing the first-time warning system in PKS CLI. The system provides a one-time disclaimer about AI-generated code when users first interact with the CLI, while respecting existing skip conditions and allowing command-level exclusion.

## System Overview

### Architecture Principles

1. **Non-Invasive Integration**: Minimal changes to existing Program.cs flow
2. **Backward Compatibility**: Existing skip logic and patterns preserved
3. **Clean Separation**: Warning logic isolated in dedicated services
4. **Extensible Design**: Attribute-based system for future enhancements
5. **Error Resilience**: Graceful degradation when file operations fail

### Key Components

```
┌─────────────────────────────────────────────────────────────────┐
│                         PKS CLI Entry Point                    │
│                          (Program.cs)                          │
├─────────────────────────────────────────────────────────────────┤
│ 1. Command Line Parsing                                        │
│ 2. Skip Logic Evaluation (MCP, Hooks, Attributes)              │
│ 3. Welcome Banner Display                                      │
│ 4. First-Time Warning Check & Display ← NEW                    │
│ 5. Command Execution                                           │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                   First-Time Warning Service                   │
│                    (IFirstTimeWarningService)                  │
├─────────────────────────────────────────────────────────────────┤
│ • ShouldShowWarningAsync(command, commandArgs)                 │
│ • DisplayWarningAsync()                                        │
│ • MarkWarningAcknowledgedAsync()                               │
│ • Integration with skip logic and attributes                   │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                  Enhanced Configuration Service                │
│                    (IConfigurationService)                     │
├─────────────────────────────────────────────────────────────────┤
│ • File-based persistence (~/.pks-cli/settings.json)            │
│ • In-memory caching for performance                            │
│ • Atomic read/write operations                                 │
│ • Error handling and fallback mechanisms                      │
└─────────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌─────────────────────────────────────────────────────────────────┐
│                  Attribute System Integration                  │
│                   (SkipFirstTimeWarningAttribute)              │
├─────────────────────────────────────────────────────────────────┤
│ • Command-level exclusion mechanism                            │
│ • Reflection-based attribute detection                         │
│ • Integration with Spectre.Console.Cli command system         │
└─────────────────────────────────────────────────────────────────┘
```

## Component Specifications

### 1. Enhanced Configuration Service

#### Interface Definition
```csharp
public interface IConfigurationService
{
    // Existing methods (preserved for backward compatibility)
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value, bool global = false, bool encrypt = false);
    Task<Dictionary<string, string>> GetAllAsync();
    Task DeleteAsync(string key);
    
    // New methods for file-based persistence
    Task LoadSettingsAsync();
    Task SaveSettingsAsync();
    Task<bool> IsInitializedAsync();
}
```

#### Implementation Strategy
```csharp
public class ConfigurationService : IConfigurationService
{
    // File-based storage location
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".pks-cli",
        "settings.json"
    );
    
    // In-memory cache for performance
    private readonly Dictionary<string, string> _cache = new();
    private bool _isLoaded = false;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    
    // Implementation details below...
}
```

#### Key Features
- **File Location**: `~/.pks-cli/settings.json`
- **Thread Safety**: SemaphoreSlim for concurrent access protection
- **Performance**: In-memory caching with lazy loading
- **Error Handling**: Graceful fallback to in-memory storage
- **Atomic Operations**: Ensure file integrity during writes

### 2. First-Time Warning Service

#### Interface Definition
```csharp
public interface IFirstTimeWarningService
{
    Task<bool> ShouldShowWarningAsync(CommandContext context, string[] commandArgs);
    Task<bool> DisplayWarningAsync();
    Task MarkWarningAcknowledgedAsync();
}
```

#### Implementation Strategy
```csharp
public class FirstTimeWarningService : IFirstTimeWarningService
{
    private readonly IConfigurationService _configurationService;
    private const string WarningAcknowledgedKey = "cli.first-time-warning-acknowledged";
    
    public async Task<bool> ShouldShowWarningAsync(CommandContext context, string[] commandArgs)
    {
        // Check if already acknowledged
        var acknowledged = await _configurationService.GetAsync(WarningAcknowledgedKey);
        if (bool.TryParse(acknowledged, out var isAcknowledged) && isAcknowledged)
            return false;
            
        // Check existing skip conditions
        if (HasExistingSkipConditions(commandArgs))
            return false;
            
        // Check command-level attribute
        if (HasSkipFirstTimeWarningAttribute(context))
            return false;
            
        return true;
    }
    
    // Additional implementation details...
}
```

#### Integration Points
- **Skip Logic Integration**: Respects existing MCP stdio and hooks JSON conditions
- **Attribute Detection**: Uses reflection to check for `SkipFirstTimeWarningAttribute`
- **Configuration Service**: Leverages enhanced persistence for acknowledgment storage
- **UI Consistency**: Uses Spectre.Console patterns matching existing codebase

### 3. SkipFirstTimeWarningAttribute System

#### Attribute Definition
```csharp
namespace PKS.Infrastructure.Attributes
{
    /// <summary>
    /// Attribute to mark commands that should skip the first-time warning display.
    /// Used for automated scenarios, hooks, and MCP integration commands.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class SkipFirstTimeWarningAttribute : Attribute
    {
        public string? Reason { get; set; }
        
        public SkipFirstTimeWarningAttribute() { }
        
        public SkipFirstTimeWarningAttribute(string reason)
        {
            Reason = reason;
        }
    }
}
```

#### Command Marking Strategy
Commands that should skip the warning:
- `McpCommand` - Automated MCP integration scenarios
- All hook commands (`PreToolUseCommand`, `PostToolUseCommand`, etc.) - Claude Code hook events
- Any future automated or background commands

#### Attribute Detection Logic
```csharp
private bool HasSkipFirstTimeWarningAttribute(CommandContext context)
{
    if (context.Data?.TryGetValue("command", out var commandObj) == true &&
        commandObj is ICommand command)
    {
        var commandType = command.GetType();
        return commandType.GetCustomAttribute<SkipFirstTimeWarningAttribute>() != null;
    }
    
    return false;
}
```

### 4. Program.cs Integration

#### Current Flow (Lines 44-48)
```csharp
// Display welcome banner with fancy ASCII art (unless we should skip it)
if (!isMcpStdio && !(isHooksCommand && (hasJsonFlag || isHookEventCommand)))
{
    DisplayWelcomeBanner();
}
```

#### Enhanced Flow (After Line 47)
```csharp
// Display welcome banner with fancy ASCII art (unless we should skip it)
if (!isMcpStdio && !(isHooksCommand && (hasJsonFlag || isHookEventCommand)))
{
    DisplayWelcomeBanner();
    
    // Check and display first-time warning
    await DisplayFirstTimeWarningIfNeededAsync(commandArgs);
}
```

#### Implementation Function
```csharp
private static async Task DisplayFirstTimeWarningIfNeededAsync(string[] commandArgs)
{
    // This will be implemented with proper DI access to warning service
    // The actual implementation will be added during development phase
    var serviceProvider = app.Configuration.Settings.Registrar.Build();
    var warningService = serviceProvider.GetService<IFirstTimeWarningService>();
    
    if (warningService != null)
    {
        // Note: CommandContext not available at this point in Program.cs
        // Will need to implement context-free checking or defer to command execution
        var shouldShow = await warningService.ShouldShowWarningAsync(null, commandArgs);
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
```

## Warning Content Specification

### Display Format
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

### Spectre.Console Implementation
```csharp
public async Task<bool> DisplayWarningAsync()
{
    var panel = new Panel(
        "[yellow]This CLI tool is powered by AI and generates code automatically.[/]\n" +
        "[yellow]The generated code has NOT been validated by humans.[/]\n\n" +
        "[red]AI may make mistakes - use at your own risk.[/]\n\n" +
        "Please review all generated code before use and report any issues at:\n" +
        "[cyan]https://github.com/pksorensen/pks-cli[/]"
    );
    
    panel.Header = new PanelHeader("[bold red]⚠️  IMPORTANT DISCLAIMER ⚠️[/]");
    panel.Border = BoxBorder.Double;
    panel.BorderStyle = Style.Parse("red");
    
    AnsiConsole.Write(panel);
    AnsiConsole.WriteLine();
    
    return AnsiConsole.Confirm("Do you acknowledge and accept these terms?", defaultValue: false);
}
```

## Error Handling Strategy

### Configuration Service Resilience
```csharp
public async Task<string?> GetAsync(string key)
{
    try
    {
        await EnsureLoadedAsync();
        return _cache.TryGetValue(key, out var value) ? value : null;
    }
    catch (Exception ex)
    {
        // Log error but don't fail - fallback to in-memory storage
        _logger?.LogWarning(ex, "Failed to load configuration from file, using in-memory cache");
        return _cache.TryGetValue(key, out var value) ? value : null;
    }
}

private async Task EnsureLoadedAsync()
{
    if (_isLoaded) return;
    
    await _fileLock.WaitAsync();
    try
    {
        if (_isLoaded) return;
        
        await LoadFromFileAsync();
        _isLoaded = true;
    }
    catch (Exception ex)
    {
        // Log but continue with empty cache - graceful degradation
        _logger?.LogWarning(ex, "Failed to load settings file: {SettingsPath}", _settingsPath);
        _isLoaded = true; // Mark as loaded to prevent retry loops
    }
    finally
    {
        _fileLock.Release();
    }
}
```

### Warning Service Fallbacks
1. **File Access Failure**: Show warning but don't persist acknowledgment
2. **Permission Issues**: Warn user but continue operation
3. **UI Interaction Failure**: Assume user declined and exit gracefully
4. **Reflection Failure**: Log warning and assume no skip attribute

## Testing Strategy

### Unit Test Coverage
1. **ConfigurationService Tests**
   - File loading and saving operations
   - Error handling and fallback scenarios
   - Thread safety and concurrent access
   - Backward compatibility with existing usage

2. **FirstTimeWarningService Tests**
   - Skip condition evaluation logic
   - Attribute detection mechanisms
   - Integration with configuration service
   - User interaction scenarios

3. **Attribute System Tests**
   - Attribute detection via reflection
   - Command marking and exclusion logic
   - Edge cases and error scenarios

### Integration Test Coverage
1. **End-to-End Warning Flow**
   - First-time user experience
   - Acknowledgment persistence
   - Subsequent invocation behavior

2. **Skip Logic Integration**
   - MCP stdio transport scenarios
   - Hooks command with JSON flag
   - Attribute-marked command exclusion

3. **Error Scenario Testing**
   - File permission issues
   - Disk space limitations
   - Corrupted settings file

## File Structure Changes

### New Files to Create
```
src/
├── Infrastructure/
│   ├── Attributes/
│   │   └── SkipFirstTimeWarningAttribute.cs      ← NEW
│   └── Services/
│       ├── FirstTimeWarningService.cs            ← NEW
│       └── IFirstTimeWarningService.cs           ← NEW
└── Commands/
    └── [Mark appropriate commands with attributes] ← MODIFIED

tests/
├── Infrastructure/
│   └── Services/
│       ├── ConfigurationServiceTests.cs          ← ENHANCED
│       ├── FirstTimeWarningServiceTests.cs       ← NEW
│       └── AttributeSystemTests.cs               ← NEW
└── Integration/
    └── FirstTimeWarningIntegrationTests.cs       ← NEW
```

### Files to Modify
```
src/
├── Program.cs                                     ← Line 47: Add warning check
├── Infrastructure/Services.cs                    ← Lines 72-74: Enhance ConfigurationService
└── Commands/
    ├── Mcp/McpCommand.cs                         ← Add [SkipFirstTimeWarning] attribute
    └── Hooks/[All hook commands]                 ← Add [SkipFirstTimeWarning] attribute
```

## Implementation Dependencies

### Required NuGet Packages
- **Existing Dependencies**: No new packages required
- **System.Text.Json**: Already available for JSON serialization
- **System.Reflection**: Part of .NET standard library
- **Spectre.Console**: Already integrated for UI components

### Service Registration
```csharp
// In Infrastructure/Services.cs
services.AddSingleton<IFirstTimeWarningService, FirstTimeWarningService>();
```

## Migration and Backward Compatibility

### Existing Configuration Usage
The enhanced ConfigurationService maintains full backward compatibility:
- All existing method signatures preserved
- In-memory cache behavior unchanged for existing code
- File persistence is additive, not breaking

### Gradual Rollout Strategy
1. **Phase 1**: Implement enhanced ConfigurationService with file persistence
2. **Phase 2**: Add FirstTimeWarningService and attribute system
3. **Phase 3**: Integrate warning display in Program.cs
4. **Phase 4**: Mark appropriate commands with skip attributes

### Settings File Migration
```json
{
  "cluster.endpoint": "https://k8s.production.com",
  "namespace.default": "myapp-production",
  "cli.first-time-warning-acknowledged": "true"
}
```

## Performance Considerations

### Configuration Service Optimizations
- **Lazy Loading**: Settings loaded only when first accessed
- **In-Memory Caching**: Avoid repeated file I/O operations
- **Atomic Writes**: Use temporary files and atomic moves
- **File Locking**: Prevent concurrent access issues

### Warning Service Optimizations
- **Early Exit**: Skip expensive operations when warning already acknowledged
- **Reflection Caching**: Cache attribute detection results
- **Command Line Parsing**: Minimal overhead for skip condition evaluation

## Security Considerations

### File System Security
- **Path Validation**: Ensure settings path is within user home directory
- **Permission Checks**: Verify write access before attempting file operations
- **Sanitization**: Validate JSON content before deserialization
- **Atomic Operations**: Prevent partial writes and corruption

### User Privacy
- **Minimal Data**: Only store acknowledgment flag, no user information
- **Local Storage**: All data remains on user's machine
- **Encryption Ready**: Infrastructure supports encrypted values for future use

## Monitoring and Observability

### Logging Strategy
```csharp
// Configuration Service
_logger.LogInformation("Loading settings from {SettingsPath}", _settingsPath);
_logger.LogWarning("Failed to save settings: {Error}", ex.Message);

// Warning Service  
_logger.LogInformation("First-time warning displayed to user");
_logger.LogInformation("User acknowledged first-time warning");
```

### Metrics Collection
- Configuration service load/save operations
- Warning display frequency
- User acknowledgment rates
- Skip condition activation patterns

## Conclusion

This architecture design provides a robust, extensible foundation for the first-time warning system. The design emphasizes:

1. **Clean Integration**: Minimal impact on existing Program.cs flow
2. **Extensible Architecture**: Attribute-based system for future enhancements  
3. **Error Resilience**: Comprehensive fallback mechanisms
4. **Performance**: Optimized file operations and caching
5. **Maintainability**: Clear separation of concerns and testable components

The modular design allows for incremental implementation and testing, ensuring each component can be validated independently before integration into the larger system.