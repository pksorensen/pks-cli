# Commands Overview

PKS CLI provides a comprehensive set of commands designed to streamline your agentic development workflow. Each command is built with beautiful terminal UI and intelligent automation.

## Command Categories

### 🏗️ Project Management
- **[`pks init`](init.md)** - Initialize new projects with intelligent templates

### 🤖 Agent Operations  
- **[`pks agent`](agent.md)** - Create and manage AI development agents

### 🚀 Deployment & Operations
- **[`pks deploy`](deploy.md)** - Deploy applications with AI optimization
- **[`pks status`](status.md)** - Monitor system status with real-time insights

### 🎨 Utilities
- **[`pks ascii`](ascii.md)** - Generate beautiful ASCII art and animations

### 🔌 Integration
- **[`pks mcp`](mcp.md)** - Model Context Protocol server for AI tool connectivity

## Global Options

All commands support these global options:

| Option | Short | Description |
|--------|-------|-------------|
| `--help` | `-h` | Show help information |
| `--version` | `-v` | Display version information |
| `--verbose` | | Enable verbose logging |
| `--quiet` | `-q` | Suppress non-essential output |
| `--no-logo` | | Skip the ASCII banner |
| `--config <file>` | `-c` | Use specific configuration file |

## Common Patterns

### Interactive Mode
Most commands support interactive mode when required parameters are missing:

```bash
# These will prompt for missing information
pks init
pks agent create
pks deploy
```

### Output Formatting
Commands support different output formats:

```bash
# JSON output for scripts
pks status --format json

# Table format for humans (default)
pks agent list --format table

# Minimal output for CI/CD
pks deploy --format minimal
```

### Configuration Precedence
All commands follow this configuration hierarchy:

1. **Command-line arguments** (highest priority)
2. **Environment variables** (`PKS_*`)
3. **Project configuration** (`./pks.config.json`)
4. **User configuration** (`~/.pks/config.json`)
5. **System defaults** (lowest priority)

## Quick Reference

### Essential Commands

```bash
# Create a new project
pks init my-app --template api --agentic

# Create and start an agent
pks agent create --name DevBot --type developer
pks agent start DevBot

# Deploy your application
pks deploy --environment staging --watch

# Monitor system status
pks status --watch --ai-insights

# Generate ASCII art for your README
pks ascii "My Project" --style banner > header.txt
```

### Advanced Usage

```bash
# Initialize with custom configuration
pks init --config ./custom-config.json --template web --agentic --mcp

# Deploy with AI optimization
pks deploy --ai-optimize --auto-rollback --health-check-timeout 300

# Start MCP server with custom port
pks mcp start --port 4000 --enable-cors --log-level debug

# Create specialized agents
pks agent create --name TesterBot --type testing --specialization "API testing"
pks agent create --name DocsBot --type documentation --focus "technical writing"
```

## Environment Variables

Configure PKS CLI behavior with environment variables:

```bash
# Global settings
export PKS_DEFAULT_TEMPLATE=api
export PKS_ENABLE_TELEMETRY=false  
export PKS_CONFIG_PATH=~/.pks/custom-config.json

# Agent settings
export PKS_AGENT_AUTO_SPAWN=true
export PKS_AGENT_LEARNING_ENABLED=true
export PKS_AGENT_DEFAULT_TYPE=developer

# UI settings
export PKS_COLOR_SCHEME=cyan
export PKS_DISABLE_ANIMATIONS=false
export PKS_ASCII_ART_ENABLED=true

# Deployment settings  
export PKS_DEFAULT_ENVIRONMENT=dev
export PKS_AI_OPTIMIZATION=true
export PKS_AUTO_WATCH=false
```

## Command Chaining

PKS CLI supports command chaining for complex workflows:

```bash
# Create, configure, and deploy in one line
pks init my-api --template api && cd my-api && pks agent create --name ApiBot && pks deploy --environment dev
```

## Error Handling

PKS CLI provides clear error messages and suggestions:

```bash
# Example error output
❌ Error: Project directory 'my-app' already exists
💡 Suggestion: Use --force to overwrite or choose a different name
🔧 Alternative: pks init my-app-v2 --template api
```

## Help System

Get help for any command:

```bash
# General help
pks --help

# Command-specific help  
pks init --help
pks agent create --help

# Show examples
pks deploy --examples

# Show all available templates
pks init --list-templates
```

## Tab Completion

Enable tab completion in your shell:

### Bash
```bash
# Add to ~/.bashrc
eval "$(pks completion bash)"
```

### Zsh  
```bash
# Add to ~/.zshrc
eval "$(pks completion zsh)"
```

### PowerShell
```powershell
# Add to PowerShell profile
pks completion powershell | Out-String | Invoke-Expression
```

## Configuration File

Create a global configuration file at `~/.pks/config.json`:

```json
{
  "defaults": {
    "template": "api",
    "enableAgentic": true,
    "enableMcp": true
  },
  "agents": {
    "autoSpawn": true,
    "defaultType": "developer",
    "learningEnabled": true,
    "maxConcurrentAgents": 3
  },
  "ui": {
    "colorScheme": "cyan",
    "animations": true,
    "asciiArt": true,
    "progressBars": true
  },
  "deployment": {
    "defaultEnvironment": "dev",
    "aiOptimization": true,
    "autoWatch": false,
    "healthCheckTimeout": 300
  },
  "mcp": {
    "defaultPort": 3000,
    "enableCors": true,
    "logLevel": "info"
  }
}
```

## Performance Tips

### Faster Command Execution
```bash
# Disable non-essential features for CI/CD
export PKS_DISABLE_ANIMATIONS=true
export PKS_ASCII_ART_ENABLED=false
export PKS_QUIET=true
```

### Parallel Operations
```bash
# Run multiple agents concurrently
pks agent create --name DevBot1 &
pks agent create --name DevBot2 &
pks agent create --name TestBot &
wait
```

## Integration Examples

### CI/CD Pipeline
```yaml
# GitHub Actions example
- name: Initialize project
  run: pks init ${{ github.event.repository.name }} --template api --no-interactive

- name: Deploy
  run: pks deploy --environment staging --format json --wait
```

### Development Scripts
```bash
#!/bin/bash
# Development setup script
set -e

pks init "$1" --template "${2:-api}" --agentic
cd "$1"
pks agent create --name DevBot --type developer
pks mcp start --daemon
echo "✅ Development environment ready!"
```

## Next Steps

- **[pks init](init.md)** - Learn about project initialization
- **[pks agent](agent.md)** - Discover agent management capabilities
- **[pks deploy](deploy.md)** - Master deployment workflows
- **[Tutorials](../tutorials/first-project.md)** - Follow step-by-step guides

Ready to dive deeper into specific commands? Pick one from the navigation menu! 🚀