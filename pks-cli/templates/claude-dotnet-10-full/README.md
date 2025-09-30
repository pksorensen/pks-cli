# PKS Claude .NET 10 Full-Featured DevContainer Template

This template provides a full-featured Claude-optimized .NET 10 development container configuration for PKS CLI projects with advanced AI integration, comprehensive tooling, and enterprise-grade capabilities.

## Features

- **Claude-optimized**: Pre-configured for seamless Claude Code integration
- **.NET 10 Support**: Latest .NET 10 SDK with advanced features
- **Advanced AI Integration**: Full Claude Code tooling and MCP support
- **Comprehensive Tooling**: Enterprise-grade development tools and utilities
- **VS Code Integration**: Pre-configured extensions and settings optimized for AI-assisted development
- **Security**: Advanced security features and secure mount configurations
- **Full-featured**: All extras included for maximum productivity

## Installation

### Via PKS CLI (Recommended)
```bash
# Use the PKS CLI to initialize with this template
pks init MyProject --template pks-claude-dotnet10-full

# Interactive mode
pks init
# Then select "PKS Claude .NET 10 Full-Featured DevContainer" from the menu
```

### Via .NET Template Engine
```bash
# Install the template package
dotnet new install PKS.Templates.ClaudeDotNet10.Full

# Create a new devcontainer configuration
dotnet new pks-claude-dotnet10-full -n MyProject
```

## Template Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `projectName` | string | MyProject | The name of your project |
| `description` | string | A full-featured Claude-optimized .NET 10 project... | Project description |
| `enableDotNet` | bool | true | Include .NET 10 development tools |
| `enableNodeJs` | bool | false | Include Node.js development tools |
| `enablePython` | bool | false | Include Python development tools |
| `enableGo` | bool | false | Include Go development tools |
| `enableDocker` | bool | true | Include Docker-in-Docker support |

## Usage Examples

### Basic Usage
```bash
pks init MyAwesomeProject --template pks-claude-dotnet10-full
```

### With Custom Description
```bash
pks init MyProject --template pks-claude-dotnet10-full --description "Enterprise AI-powered application"
```

### From Custom NuGet Feed
```bash
pks init MyProject --template pks-claude-dotnet10-full --nuget-source https://my-feed.com/v3/index.json
```

## Structure

The template creates the following structure:
```
.devcontainer/
├── Dockerfile          # Container definition with .NET 10 and AI tools
├── devcontainer.json   # VS Code dev container configuration
├── docker-compose.yml  # Docker compose configuration
└── init-firewall.sh    # Security initialization script
```

## Customization

After creating the devcontainer, you can further customize:

1. **Extensions**: Add more VS Code extensions in `devcontainer.json`
2. **AI Tools**: Configure Claude Code and MCP settings
3. **Environment**: Add environment variables in the `remoteEnv` section
4. **Tools**: Modify the Dockerfile to include additional development tools
5. **Mounts**: Configure additional volume mounts for your workflow

## VS Code Integration

To use the created devcontainer:

1. Open your project folder in VS Code
2. When prompted, click "Reopen in Container"
3. Or use Command Palette (F1) → "Dev Containers: Reopen in Container"

## What Makes This "Full-Featured"?

This template includes everything you need for advanced .NET 10 development:

- **Complete .NET 10 toolchain**: SDK, runtime, and all workloads
- **AI Development Tools**: Claude Code, GitHub Copilot integration
- **Advanced Debugging**: Enhanced debugging capabilities
- **Performance Tools**: Profiling and diagnostics tools
- **Security Tools**: Security scanning and analysis
- **Database Tools**: SQL Server, PostgreSQL, MongoDB clients
- **Cloud Tools**: Azure CLI, AWS CLI, kubectl
- **And much more**: This is the kitchen-sink template with all extras included!

## Support

For issues or questions, please visit: https://github.com/pksorensen/pks-cli/issues
