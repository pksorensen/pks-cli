# Hooks System for {{ProjectName}}

This directory contains hooks that integrate with Claude Code for intelligent automation and workflow management.

## Overview

The hooks system uses a **smart dispatcher pattern** to provide:
- ✨ Intelligent command routing
- ⚡ Performance optimization
- 🎯 Targeted validations
- 🤖 AI-powered automation

## Available Hooks

### Core System Hooks

#### Smart Dispatcher
- **`smart-dispatcher.sh`**: Main entry point that routes commands intelligently
- Analyzes commands and routes to appropriate validation scripts
- Skips simple commands to maintain performance
- Only executes relevant hooks based on command patterns

#### Build Hooks
- **`pre-build.sh`**: Validates build prerequisites and project state
- **`post-build.sh`**: Verifies build output and runs post-build tasks

#### Deployment Hooks
- **`pre-deploy.sh`**: Validates deployment prerequisites and environment
- **`post-deploy.sh`**: Verifies deployment success and runs post-deployment tasks

### Template-Specific Features

Based on your project template (**{{Template}}**), additional hooks may be available:

- **API Projects**: API-specific validations and health checks
- **Web Projects**: Web application build and deployment hooks
- **Console Projects**: Console application specific validations

## Configuration

### Claude Code Integration

The hooks are configured in `.hooks.json` and integrate with Claude Code through:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": "./hooks/smart-dispatcher.sh"
          }
        ]
      }
    ]
  }
}
```

### Smart Dispatcher Configuration

The smart dispatcher uses pattern matching to route commands:

- **Skip Patterns**: Simple commands like `ls`, `pwd`, `cd` are skipped
- **Execute Patterns**: Important commands like `build`, `deploy`, `test` trigger hooks
- **Performance Mode**: Optimizes execution to avoid unnecessary overhead

## Usage

### Automatic Execution

Hooks execute automatically when Claude Code runs matching commands:

```bash
# These commands will trigger hooks:
dotnet build          # → pre-build.sh
npm run deploy        # → pre-deploy.sh
docker build          # → Docker validation hooks

# These commands are skipped for performance:
ls -la               # → No hook execution
pwd                  # → No hook execution
cd src               # → No hook execution
```

### Manual Execution

You can run hooks manually for testing:

```bash
# Execute with context
echo '{"command": "dotnet build", "project": "{{ProjectName}}"}' | ./hooks/smart-dispatcher.sh

# Run specific hooks directly
./hooks/pre-build.sh
./hooks/post-deploy.sh
```

## Smart Dispatcher Pattern

The smart dispatcher provides several benefits:

### Performance Optimization
- **Early Exit**: Skips processing for irrelevant commands
- **Pattern Matching**: Only processes commands that need validation
- **Minimal Overhead**: Adds virtually no delay to simple operations

### Intelligent Routing
```bash
# Command analysis flow:
Input Command → Pattern Analysis → Route to Appropriate Hook → Execute Validation
```

### Error Handling
- Graceful failure handling
- Detailed error reporting
- Non-blocking for non-critical validations

## Customization

### Adding New Hooks

1. Create your hook script in the `hooks/` directory
2. Make it executable: `chmod +x hooks/your-hook.sh`
3. Add routing logic to `smart-dispatcher.sh`
4. Test with manual execution

### Modifying Patterns

Edit the smart dispatcher to customize routing:

```bash
# Add new command patterns
if echo "$command" | grep -q "your-pattern"; then
    echo "🎯 Running your custom validation..."
    ./hooks/your-custom-hook.sh <<< "$json_input"
    exit $?
fi
```

## Best Practices

### Hook Development
- ✅ Use meaningful exit codes (0 = success, 1 = error, 2 = warning)
- ✅ Provide clear, actionable error messages
- ✅ Include progress indicators for long-running operations
- ✅ Handle JSON input gracefully with `jq`

### Performance
- ⚡ Keep hooks fast and focused
- ⚡ Use early exit for invalid conditions
- ⚡ Avoid expensive operations in frequently-called hooks

### Debugging
- 🔍 Use `echo` statements for debugging
- 🔍 Test hooks independently before integration
- 🔍 Check JSON input parsing with `jq`

## Troubleshooting

### Common Issues

#### Hook Not Executing
```bash
# Check if hook is executable
ls -la hooks/your-hook.sh

# Make executable if needed
chmod +x hooks/your-hook.sh
```

#### JSON Parsing Errors
```bash
# Test JSON input manually
echo '{"command": "test"}' | jq .

# Validate hook input handling
echo '{"command": "test"}' | ./hooks/your-hook.sh
```

#### Performance Issues
```bash
# Check if simple commands are being skipped
echo '{"command": "ls"}' | ./hooks/smart-dispatcher.sh
```

## Integration with {{ProjectName}}

This hooks system is specifically configured for **{{ProjectName}}** with:

- **Project Name**: {{ProjectName}}
- **Template**: {{Template}}
- **Description**: {{Description}}

The hooks automatically reference your project context and can be customized based on your specific requirements.

## Support

For more information about hooks and Claude Code integration:
- [Claude Code Hooks Documentation](https://docs.anthropic.com/en/docs/claude-code/hooks)
- [PKS CLI Documentation](https://github.com/pksorensen/pks-cli)
- [Smart Dispatcher Pattern Guide](https://claudelog.com/mechanics/hooks/)

---

*Generated by PKS CLI on {{DateTime}}*