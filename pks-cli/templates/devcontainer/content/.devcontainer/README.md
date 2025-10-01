# PKSDevContainer - Universal Development Environment

PROJECT_DESCRIPTION

This project includes a comprehensive DevContainer setup that provides a consistent development environment across different machines and platforms.

## Features

- **Base Image**: ${containerRegistry}/dotnet/sdk:8.0
- **Workspace Folder**: ${workspaceFolder}
- **Pre-configured Extensions**: Essential .NET and VS Code extensions
- **Port Forwarding**: Common development ports (5000, 5001)
- **Environment Variables**: Optimized for .NET development

## Optional Components

The devcontainer can be configured with the following optional components:

- **Node.js**: JavaScript/TypeScript development support (enabled: ${enableNodeJs})
- **Python**: Python development support (enabled: ${enablePython})
- **Go**: Go development support (enabled: ${enableGo})

## Getting Started

### Prerequisites

- [Visual Studio Code](https://code.visualstudio.com/)
- [Dev Containers extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers)
- [Docker Desktop](https://www.docker.com/products/docker-desktop)

### Using the DevContainer

1. Open this project in Visual Studio Code
2. When prompted, select "Reopen in Container"
3. Or manually: Press `F1` → "Dev Containers: Reopen in Container"

### Initial Setup

After the container starts, the following commands will be run automatically:

- `dotnet restore` - Restore NuGet packages
- Container initialization script for environment setup

### Development Workflow

- **Build**: `dotnet build`
- **Run**: `dotnet run`
- **Test**: `dotnet test`
- **Debug**: Use VS Code debugging features (F5)

### Customization

You can customize the devcontainer by modifying:

- `.devcontainer/devcontainer.json` - Main configuration
- `.devcontainer/Dockerfile` - Container image customization
- `.devcontainer/docker-compose.yml` - Multi-service setups

### Ports

The following ports are automatically forwarded:

- `5000` - HTTP
- `5001` - HTTPS

Additional ports can be configured in `devcontainer.json`.

### Troubleshooting

#### Container won't start
- Ensure Docker Desktop is running
- Check that no other containers are using the same ports
- Try "Dev Containers: Rebuild Container"

#### Missing dependencies
- Run `dotnet restore` in the terminal
- Check the container logs for initialization errors

#### Permission issues
- The initialization script handles most permission setup
- For persistent issues, try rebuilding the container

## Support

For issues related to this DevContainer template, please check:

- [PKS CLI Documentation](https://github.com/pksorensen/pks-cli)
- [DevContainers Documentation](https://containers.dev/)
- [VS Code DevContainers Guide](https://code.visualstudio.com/docs/devcontainers/containers)