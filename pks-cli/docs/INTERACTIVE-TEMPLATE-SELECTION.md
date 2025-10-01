# Interactive Template Selection

## Overview

The `pks init` command now features interactive template selection, allowing users to choose their project template through a user-friendly menu instead of defaulting to the console template.

## What Changed

### Before
```bash
pks init MyProject
# ❌ Automatically used "console" template without asking
```

### After
```bash
pks init MyProject
# ✅ Shows interactive menu to select template
```

## Interactive Flow

When running `pks init` without the `-t/--template` flag, users will see:

```
What's the name of your project?
> MyProject

Which template would you like to use?
❯ 📦 Console Application - Command-line application
  🌐 Web API - RESTful API service
  🖥️  Web Application - ASP.NET web app
  🤖 Agent - AI-powered agent application
  📚 Class Library - Reusable .NET library
(Move up and down to reveal more templates)

What's the description/objective of your project?
> My awesome project
```

## Usage Examples

### Interactive Mode (New Default)
```bash
# No template specified - shows menu
pks init MyNewProject
```

### Explicit Template (Skips Menu)
```bash
# Template specified - no menu, uses api template directly
pks init MyApiProject --template api

# Short form also works
pks init MyWebApp -t web
```

### Fully Non-Interactive
```bash
# All options provided - completely non-interactive
pks init MyProject --template console --description "My CLI tool"
```

## Available Templates

| Template | Icon | Description |
|----------|------|-------------|
| **console** | 📦 | Console Application - Command-line application |
| **api** | 🌐 | Web API - RESTful API service |
| **web** | 🖥️ | Web Application - ASP.NET web app |
| **agent** | 🤖 | Agent - AI-powered agent application |
| **library** | 📚 | Class Library - Reusable .NET library |

## Technical Details

### Implementation

The template selection is implemented using Spectre.Console's `SelectionPrompt`:

```csharp
// Only show prompt if template is not provided
if (string.IsNullOrEmpty(settings.Template))
{
    settings.Template = _console.Prompt(
        new SelectionPrompt<string>()
            .Title("Which [cyan]template[/] would you like to use?")
            .PageSize(10)
            .AddChoices(new[] { "console", "api", "web", "agent", "library" })
            .UseConverter(template => /* descriptive text */));
}
```

### Behavior

1. **No `-t` flag**: Shows interactive menu ✅
2. **With `-t` flag**: Uses specified template directly ✅
3. **CI/CD compatibility**: Can still use `--template` for automation ✅

## Benefits

1. **Better User Experience**: Users discover available templates
2. **No Surprises**: No automatic defaulting to console template
3. **Still Automatable**: Scripts can use `--template` flag
4. **Visual Guidance**: Icons and descriptions help users choose

## Backwards Compatibility

### Scripts/CI-CD
Scripts that explicitly specify `--template` are unaffected:
```bash
# Still works exactly the same
pks init AutomatedProject --template api
```

### Scripts Without Template
Scripts that relied on the default "console" template need to be updated:
```bash
# Before (implicitly used console)
pks init Project

# After (specify template explicitly for automation)
pks init Project --template console
```

## Future Enhancements

Potential improvements:
- Discover templates from NuGet packages
- Show template descriptions from package metadata
- Group templates by category (Web, Desktop, Mobile, etc.)
- Preview template structure before selection
- Recently used templates shown first

## Testing

### Manual Testing
```bash
# Test interactive selection
pks init TestProject
# Should show menu

# Test explicit template
pks init TestProject2 --template api
# Should skip menu

# Test with all options
pks init TestProject3 -t web -d "My web app"
# Should be fully non-interactive
```

### Automated Testing
```bash
# For CI/CD, always specify template
pks init CIProject --template console --description "Automated build"
```

## Related Documentation

- [InitCommand.cs](/workspace/pks-cli/src/Commands/InitCommand.cs) - Implementation
- [Template Creation Guide](/workspace/pks-cli/docs/TEMPLATE-CREATION.md) - Creating custom templates
- [Spectre.Console SelectionPrompt](https://spectreconsole.net/prompts/selection) - UI component used

## Feedback

If you have suggestions for improving template selection, please:
- Open an issue: https://github.com/pksorensen/pks-cli/issues
- Or use: `pks feedback "Your suggestion about template selection"`
