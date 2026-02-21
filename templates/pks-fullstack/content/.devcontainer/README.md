# PKSFullstack DevContainer - Universal Development Environment

PROJECT_DESCRIPTION

This project includes a comprehensive DevContainer setup that provides a consistent development environment across different machines and platforms.

## Features

- **Base Image**: ghcr.io/pksorensen/pks-fullstack-base:latest
- **Node.js 20 LTS**: JavaScript/TypeScript development
- **.NET 8 + .NET 10**: Latest .NET SDK versions
- **Playwright**: End-to-end testing framework
- **DevTunnel CLI**: Microsoft's secure tunneling service
- **Git Credential Manager**: Secure Git authentication
- **Aspire CLI**: .NET Aspire for cloud-native development
- **Claude Code**: AI-powered coding assistant
- **Docker-in-Docker**: Container development inside containers
- **Security Firewall**: Restrictive iptables-based firewall

## Optional Components

The devcontainer can be configured with the following optional components:

- **Python**: Python development support
- **PowerShell**: PowerShell scripting support
- **Additional VS Code Extensions**: Customizable extension set

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

After the container starts, the following initialization happens:

- **Firewall Configuration**: Automatic setup of security firewall
- **Docker-in-Docker**: Initialization of Docker daemon
- **Environment Variables**: Loading from `.devcontainer/.env`
- **Command History**: Restoration of previous terminal sessions

### Development Workflow

#### Node.js Development
- **Install**: `npm install`
- **Run**: `npm start` or `npm run dev`
- **Test**: `npm test`
- **Build**: `npm run build`

#### .NET Development
- **Build**: `dotnet build`
- **Run**: `dotnet run`
- **Test**: `dotnet test`
- **Debug**: Use VS Code debugging features (F5)

#### Docker Development
- **Build**: `docker build -t myapp .`
- **Run**: `docker run -p 3000:3000 myapp`
- **Compose**: `docker compose up`

#### Aspire Development
- **Create**: `dotnet new aspire`
- **Run**: `aspire run`

### Customization

You can customize the devcontainer by modifying:

- `.devcontainer/devcontainer.json` - Main configuration
- `.devcontainer/Dockerfile` - Container image customization
- `.devcontainer/init-firewall.sh` - Firewall allowed domains
- `.devcontainer/.env` - Environment variables

### Ports

Common development ports are automatically forwarded:

- `5000` - HTTP
- `5001` - HTTPS
- `3000` - Node.js/React
- `8080` - Alternative HTTP

Additional ports can be configured in `devcontainer.json`.

### Security Firewall

The DevContainer includes a restrictive firewall that blocks all outbound connections except to approved domains. See `.devcontainer/CLAUDE.md` for detailed information.

**To add new allowed domains:**
1. Edit `.devcontainer/init-firewall.sh`
2. Add your domain to the domain resolution loop
3. Rebuild the container (`F1` → "Dev Containers: Rebuild Container")

### Troubleshooting

#### Container won't start
- Ensure Docker Desktop is running
- Check that no other containers are using the same ports
- Try "Dev Containers: Rebuild Container"

#### Missing dependencies
- Run `npm install` or `dotnet restore` in the terminal
- Check the container logs for initialization errors

#### Permission issues
- The initialization script handles most permission setup
- For persistent issues, try rebuilding the container

#### Firewall blocking required domain
- Check `.devcontainer/CLAUDE.md` for firewall documentation
- Edit `.devcontainer/init-firewall.sh` to add the domain
- Rebuild the container for changes to take effect

#### Docker-in-Docker not working
- Ensure `--cap-add=NET_ADMIN` and `--cap-add=NET_RAW` are in runArgs
- Check that the docker-init.sh script ran successfully
- Try running `sudo /usr/local/share/docker-init.sh` manually

## Support

For issues related to this DevContainer template, please check:

- [PKS CLI Documentation](https://github.com/pksorensen/pks-cli)
- [DevContainers Documentation](https://containers.dev/)
- [VS Code DevContainers Guide](https://code.visualstudio.com/docs/devcontainers/containers)
