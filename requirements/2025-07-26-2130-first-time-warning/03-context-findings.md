# Phase 3: Context Findings

## Codebase Analysis

### Current Architecture
- **Framework**: PKS CLI uses Spectre.Console.Cli for command-line interface
- **Entry Point**: `/workspace/pks-cli/src/Program.cs:373` - `DisplayWelcomeBanner()` function
- **Banner Logic**: Lines 44-48 in Program.cs handle banner display with skip conditions
- **UI Framework**: Spectre.Console for rich terminal interactions

### Banner Display Logic
The current banner display mechanism is located in Program.cs:44-48:
```csharp
// Display welcome banner with fancy ASCII art (unless we should skip it)
if (!isMcpStdio && !(isHooksCommand && (hasJsonFlag || isHookEventCommand)))
{
    DisplayWelcomeBanner();
}
```

**Current Skip Conditions:**
- MCP stdio transport (`isMcpStdio`)
- Hooks commands with JSON flag or specific hook events

### Configuration System
- **Service**: `IConfigurationService` in `/workspace/pks-cli/src/Infrastructure/Services.cs:42`
- **Implementation**: Currently uses in-memory dictionary (simulated)
- **Methods**: `GetAsync()`, `SetAsync()`, `GetAllAsync()`, `DeleteAsync()`
- **Global Settings**: Supported via `global` parameter in `SetAsync()`

### User Interaction Patterns
**Confirmation Pattern**: Found in PrdGenerateCommand.cs:
```csharp
var overwrite = AnsiConsole.Confirm($"PRD file already exists at [yellow]{settings.OutputPath}[/]. Overwrite?");
```

**User Input Pattern**: Found in InitCommand.cs and PrdGenerateCommand.cs:
```csharp
settings.ProjectName = AnsiConsole.Ask<string>("What's the [green]name[/] of your project?");
```

### Related Files to Modify

#### Primary Files:
1. **Program.cs** - Modify banner display logic and add first-time warning
2. **Infrastructure/Services.cs** - Use ConfigurationService to track acknowledgment

#### Files for Custom Attribute (Optional Enhancement):
- Create new attribute class for commands that should skip warnings
- Modify command registration logic in Program.cs

### Similar Features Analysis
- **Existing Skip Logic**: MCP stdio and hooks commands already have skip conditions
- **Configuration Persistence**: ConfigurationService supports global settings
- **User Confirmation**: Existing patterns use `AnsiConsole.Confirm()`

### Technical Constraints
- Must integrate with existing Spectre.Console UI framework
- Must respect existing banner skip conditions
- Configuration service currently uses in-memory storage (needs persistence)
- Must not interfere with automated scenarios (MCP stdio, hooks)

### Integration Points
1. **Banner Display**: Modify `DisplayWelcomeBanner()` or create new warning display function
2. **Configuration**: Use `IConfigurationService` to store user acknowledgment
3. **Command Detection**: Leverage existing command argument parsing
4. **Skip Logic**: Extend existing skip conditions for automated scenarios

### UI Consistency
- Use Spectre.Console markup for colored text
- Follow existing patterns: `[red]`, `[green]`, `[yellow]`, `[cyan]` colors
- Use `AnsiConsole.Confirm()` for user acknowledgment
- Use `AnsiConsole.MarkupLine()` for formatted output