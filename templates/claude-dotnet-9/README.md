# PKS Claude .NET 9 DevContainer Template

A Claude-optimized development container template for .NET 9 projects with Aspire support, designed for AI-assisted development workflows.

## Features

- **.NET 9 SDK** - Latest .NET runtime and SDK with C# DevKit
- **.NET Aspire** - Cloud-native application development tooling
- **Claude Code Integration** - Optimized for Claude Code workflows with persistent configuration
- **GitHub Copilot** - AI-powered code completion and chat
- **Docker-in-Docker** - Build and run containers inside the dev container
- **PowerShell** - Cross-platform scripting and automation
- **Persistent Volumes** - Command history, Claude config, and Playwright browsers
- **Git Configuration** - Automatically mounts your local git config

## What's Included

### VS Code Extensions
- C# DevKit (`ms-dotnettools.csdevkit`)
- GitHub Copilot (`GitHub.copilot`)
- GitHub Copilot Chat (`GitHub.copilot-chat`)

### Development Tools
- .NET 9 SDK (Bookworm-based container)
- Docker CLI with Docker-in-Docker support
- PowerShell 7+
- Git
- curl, wget, and other essential utilities

### Aspire Workload
The .NET Aspire workload is automatically installed on container creation, providing:
- Cloud-native application templates
- Service discovery and orchestration
- Distributed application development tools

## Usage

### With PKS CLI

```bash
# Create a new project with this devcontainer
pks init MyProject --devcontainer --template claude-dotnet9

# With custom parameters
pks init MyProject --devcontainer \
  --template claude-dotnet9 \
  --timezone "America/New_York" \
  --enable-aspire true
```

### Manual Installation

```bash
# Install the template
dotnet new install PKS.Templates.ClaudeDotNet9

# Use the template
dotnet new pks-claude-dotnet9 -n MyProject
```

## Configuration

### Template Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `ProjectName` | MyProject | Name of your project |
| `Description` | A .NET 9 project... | Project description |
| `TimeZone` | Europe/Copenhagen | Container timezone |
| `EnableAspire` | true | Install .NET Aspire tooling |
| `EnableGitHubCopilot` | true | Include Copilot extensions |
| `EnableDocker` | true | Include Docker-in-Docker |
| `EnablePowerShell` | true | Include PowerShell |
| `NodeMemoryLimit` | 4096 | Node.js memory limit (MB) |
| `GitHubPATToken` | (empty) | GitHub Personal Access Token |

### Environment Variables

Create a `.env` file in `.devcontainer/` to configure:

```env
github_pat_token=your_token_here
```

### Persistent Volumes

The following volumes are mounted for persistence:
- `claude-code-bashhistory` - Bash command history
- `claude-code-config` - Claude Code configuration
- `playwright-browsers` - Playwright browser binaries
- Your local `.gitconfig` (read-only bind mount)

## Post-Creation Setup

After the container is created:

1. **Verify Aspire Installation**
   ```bash
   dotnet workload list
   ```

2. **Trust HTTPS Certificates**
   ```bash
   dotnet dev-certs https --trust
   ```

3. **Verify Docker**
   ```bash
   docker --version
   ```

## Customization

### Adding VS Code Extensions

Edit `devcontainer.json` and add extensions to the `customizations.vscode.extensions` array:

```json
"extensions": [
  "ms-dotnettools.csdevkit",
  "GitHub.copilot-chat",
  "GitHub.copilot",
  "your.extension.id"
]
```

### Modifying Build Args

Update the `Dockerfile` or `devcontainer.json` `build.args` section:

```json
"build": {
  "dockerfile": "Dockerfile",
  "args": {
    "TZ": "${localEnv:TZ:Europe/Copenhagen}"
  }
}
```

## Troubleshooting

### Aspire Installation Failed
If Aspire doesn't install automatically:
```bash
curl -sSL https://aspire.dev/install.sh | bash
# or
dotnet workload install aspire
```

### Docker-in-Docker Issues
Ensure Docker is running on your host machine and the container has proper permissions.

### Git Config Not Mounted
Verify the path in `devcontainer.json` matches your system:
- Windows: `${localEnv:USERPROFILE}/.gitconfig`
- Linux/Mac: `${localEnv:HOME}/.gitconfig`

## Learn More

- [.NET Aspire Documentation](https://learn.microsoft.com/dotnet/aspire/)
- [VS Code Dev Containers](https://code.visualstudio.com/docs/devcontainers/containers)
- [PKS CLI Documentation](https://github.com/pksorensen/pks-cli)
- [Claude Code](https://claude.ai/code)

## License

MIT License - see LICENSE file for details

## Contributing

Contributions are welcome! Please see [CONTRIBUTING.md](../../CONTRIBUTING.md) for guidelines.
