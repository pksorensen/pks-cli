# PKS Fullstack DevContainer Template

A comprehensive fullstack development container template that provides a complete, production-ready development environment with modern tools and AI capabilities.

## Features

This template uses a **pre-built base image** for fast container setup and includes:

### Core Technologies
- **Node.js 20 LTS** - JavaScript/TypeScript runtime with npm
- **.NET 8 + .NET 10** - Latest .NET SDK versions for C# development
- **Python 3** - Python development support
- **Docker-in-Docker** - Container development inside containers

### Development Tools
- **Playwright** - End-to-end testing framework with browser automation
- **DevTunnel CLI** - Microsoft's secure tunneling service for exposing local services
- **Git Credential Manager (GCM)** - Secure Git authentication
- **Aspire CLI** - .NET Aspire for cloud-native application development
- **Claude Code** - AI-powered coding assistant by Anthropic

### Shell & Terminal
- **Oh My Zsh** - Enhanced shell with themes and plugins
- **Powerlevel10k** - Beautiful and fast Zsh theme
- **Git Delta** - Better git diff viewer with syntax highlighting
- **Command history persistence** - Preserves your command history across container rebuilds

### Security Features
- **Custom Firewall** - Restrictive iptables-based firewall with default-deny policy
- **Allowlist-based Access** - Only approved domains and IPs can be accessed
- **GitHub API Integration** - Automatically fetches and allows GitHub IP ranges
- **Azure AD Support** - Microsoft authentication services fully supported
- **CDN Access** - Controlled access to NPM, Docker Hub, and other essential CDNs

## Quick Start

### Installation

```bash
# Create a new project with this template
pks init MyFullstackProject --template pks-fullstack

# Or with custom parameters
pks init MyProject --template pks-fullstack \
  --description "My awesome fullstack app" \
  --timezone "America/New_York" \
  --base-image-tag "1.0.0"
```

### Parameters

| Parameter | Description | Default |
|-----------|-------------|---------|
| `ProjectName` | The name of your project | `MyProject` |
| `Description` | Project description | `A fullstack development project with Node.js and .NET` |
| `Timezone` | Container timezone (TZ env var) | `Europe/Copenhagen` |
| `BaseImageTag` | Base image tag to use | `latest` |

### First-Time Setup

1. **Configure GitHub Authentication**
   - Open `.devcontainer/.env`
   - Replace `<insert_your_github_pat_here>` with your GitHub Personal Access Token
   - Generate a token at: https://github.com/settings/tokens
   - Required scopes: `repo`, `read:org`, `workflow`

2. **Open in VS Code**
   ```bash
   code MyFullstackProject
   ```

3. **Reopen in Container**
   - VS Code will prompt: "Reopen in Container"
   - Or press `F1` → "Dev Containers: Reopen in Container"

4. **Wait for Container Build**
   - First build may take a few minutes
   - Subsequent rebuilds are much faster thanks to the pre-built base image

## Base Image

This template uses the **pks-fullstack-base** image hosted at:
```
ghcr.io/pksorensen/pks-fullstack-base:latest
```

### Base Image Contents
The base image pre-installs all major dependencies:
- Node.js 20 LTS with npm
- .NET 8 and .NET 10 SDKs
- Playwright with browser binaries
- DevTunnel CLI
- Git Credential Manager
- Aspire CLI
- Claude Code CLI
- Oh My Zsh with Powerlevel10k
- Git Delta
- Essential development tools (curl, wget, git, etc.)

### Why Use a Base Image?
- **Faster Setup** - Container builds in seconds instead of minutes
- **Consistency** - Everyone uses the same tested base environment
- **Reliability** - Pre-built images are tested and verified
- **Bandwidth Savings** - Download once, use many times

## Firewall Configuration

### Overview
The container includes a security firewall that blocks all outbound connections except to approved domains. This protects against:
- Accidental data exfiltration
- Malicious package supply chain attacks
- Unauthorized API calls
- Privacy leaks

### Default Allowed Domains
- **GitHub**: All GitHub services (web, API, Git)
- **NPM**: Package registry
- **Anthropic**: Claude API and related services
- **Microsoft**: Azure, NuGet, Aspire, DevTunnel, authentication services
- **Docker**: Docker Hub, image registries
- **Expo**: React Native development (expo.dev, api.expo.dev)
- **Development Tools**: VS Code Marketplace, fonts, CDNs

### Customizing the Firewall

To add new domains, edit `.devcontainer/init-firewall.sh`:

```bash
# Find the domain resolution section (around line 211)
for domain in \
    "registry.npmjs.org" \
    "api.anthropic.com" \
    # Add your domain here
    "api.example.com" \
    "cdn.myservice.com"; do
```

**Important**: After editing the firewall script, you must **rebuild the container**:
1. Press `F1` in VS Code
2. Select "Dev Containers: Rebuild Container"
3. Wait for the rebuild to complete

### Firewall Details
- **Default Policy**: DROP all traffic not explicitly allowed
- **Verification**: Automatically tests firewall on container start
- **Dynamic IP Resolution**: GitHub and Azure AD IPs fetched automatically
- **Docker Network Support**: Internal Docker networking always allowed
- **Localhost Access**: Loopback interface always available

For detailed firewall documentation, see `.devcontainer/CLAUDE.md`.

## VS Code Extensions

The template includes pre-configured VS Code extensions:
- **Claude Code** - AI coding assistant
- **C# Dev Kit** - .NET development
- **ESLint + Prettier** - Code formatting and linting
- **GitLens** - Enhanced Git capabilities
- **GitHub Copilot** - AI pair programming (if enabled on your account)
- **Azure Repos** - Azure DevOps integration

## Directory Structure

```
MyProject/
├── .devcontainer/
│   ├── devcontainer.json       # DevContainer configuration
│   ├── Dockerfile              # Slim Dockerfile using base image
│   ├── init-firewall.sh        # Firewall configuration script
│   ├── CLAUDE.md               # AI agent instructions
│   ├── README.md               # DevContainer documentation
│   ├── .env                    # Environment variables (gitignored)
│   └── .gitignore              # Protects .env from commits
└── [Your project files]
```

## Environment Variables

The `.devcontainer/.env` file supports custom environment variables:

```env
# GitHub PAT (required)
github_pat_token=ghp_your_token_here

# Add custom variables
MY_API_KEY=your_api_key_here
DATABASE_URL=postgresql://user:pass@localhost:5432/dbname
```

**Security Note**: The `.env` file is gitignored to prevent committing secrets.

## Development Workflow

### Node.js Development
```bash
# Install dependencies
npm install

# Run development server
npm run dev

# Run tests
npm test
```

### .NET Development
```bash
# Restore packages
dotnet restore

# Build
dotnet build

# Run
dotnet run

# Test
dotnet test
```

### Docker Development
```bash
# Docker commands work inside the container
docker build -t myapp .
docker run -p 3000:3000 myapp
docker compose up
```

### Aspire Development
```bash
# Create new Aspire app
dotnet new aspire

# Run Aspire dashboard
aspire run
```

## Troubleshooting

### Container Won't Start
- Ensure Docker Desktop is running
- Check for port conflicts
- Review container logs in VS Code

### Firewall Blocking Required Domain
1. Identify the blocked domain from error messages
2. Edit `.devcontainer/init-firewall.sh`
3. Add domain to the allowed list
4. Rebuild container (`F1` → "Rebuild Container")

### Authentication Issues
- Verify GitHub PAT in `.devcontainer/.env`
- Check token scopes (needs repo, read:org, workflow)
- Regenerate token if expired

### Network Connection Problems
- Check if firewall is blocking required services
- Verify DNS resolution: `dig example.com`
- Check iptables rules: `sudo iptables -L -n -v`

### Slow Container Startup
- First build downloads base image (one-time cost)
- Subsequent rebuilds should be fast
- Clear Docker cache if needed: `docker system prune`

## Support & Resources

- **PKS CLI**: https://github.com/pksorensen/pks-cli
- **DevContainers**: https://containers.dev/
- **VS Code Docs**: https://code.visualstudio.com/docs/devcontainers/containers
- **Issue Tracker**: https://github.com/pksorensen/pks-cli/issues

## License

MIT License - see LICENSE file in the PKS CLI repository.

## Contributing

Contributions welcome! Please submit issues and pull requests to the PKS CLI repository.
