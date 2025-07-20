# PKS Universal DevContainer Template

This template provides a comprehensive development container configuration for PKS CLI projects.

## Features

- **Multi-language support**: Configurable support for .NET, Node.js, Python, and Go
- **VS Code integration**: Pre-configured extensions and settings
- **Security**: Firewall initialization and secure mount configurations
- **Customizable**: Template parameters allow project-specific configurations

## Installation

### Via PKS CLI (Recommended)
```bash
# Use the PKS CLI devcontainer wizard
pks devcontainer wizard

# Or directly create with template
pks devcontainer init MyProject --template universal
```

### Via .NET Template Engine
```bash
# Install the template package
dotnet new install PKS.Templates.DevContainer

# Create a new devcontainer configuration
dotnet new pks-universal-devcontainer -n MyProject
```

## Template Parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `projectName` | string | MyProject | The name of your project |
| `enableNodeJs` | bool | true | Include Node.js development tools |
| `enableDotNet` | bool | true | Include .NET development tools |
| `enablePython` | bool | false | Include Python development tools |
| `enableGo` | bool | false | Include Go development tools |
| `containerRegistry` | string | mcr.microsoft.com | Container registry for base images |
| `workspaceFolder` | string | /workspace | Default workspace folder in container |

## Usage Examples

### Basic Usage
```bash
dotnet new pks-universal-devcontainer -n MyAwesomeProject
```

### With Python Support
```bash
dotnet new pks-universal-devcontainer -n MyPythonProject --enablePython true
```

### Custom Registry
```bash
dotnet new pks-universal-devcontainer -n MyProject --containerRegistry myregistry.azurecr.io
```

## Structure

The template creates the following structure:
```
.devcontainer/
├── Dockerfile          # Container definition
├── devcontainer.json   # VS Code dev container configuration
├── docker-compose.yml  # Docker compose configuration
└── init-firewall.sh    # Security initialization script
```

## Customization

After creating the devcontainer, you can further customize:

1. **Extensions**: Add more VS Code extensions in `devcontainer.json`
2. **Environment**: Add environment variables in the `remoteEnv` section
3. **Tools**: Modify the Dockerfile to include additional development tools
4. **Mounts**: Configure additional volume mounts for your workflow

## VS Code Integration

To use the created devcontainer:

1. Open your project folder in VS Code
2. When prompted, click "Reopen in Container"
3. Or use Command Palette (F1) → "Dev Containers: Reopen in Container"

## Support

For issues or questions, please visit: https://github.com/pksorensen/pks-cli/issues