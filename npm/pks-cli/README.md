# PKS CLI

The next agentic CLI for .NET developers - combining beautiful terminal UI with AI-powered development assistance and Kubernetes deployment capabilities.

## Installation

### Quick Install (npm)

```bash
npm install -g @pks-cli/cli
```

### Quick Install (.NET Global Tool)

```bash
dotnet tool install -g pks-cli
```

## Usage

After installation, use the `pks` command:

```bash
# See all available commands
pks --help

# Initialize a new project
pks init MyProject

# Interactive mode (prompts for details)
pks init

# Create agentic project with MCP integration
pks init MyAgent --agentic --mcp --template agent

# API project with specific template
pks init MyApi --template api --description "REST API for my application"

# Web application with agentic features
pks init MyWebApp --template web --agentic --description "Intelligent web application"

# Generate ASCII art
pks ascii "Hello PKS"

# View system status
pks status
```

## Available Commands

- **`pks init`** - Project initialization with templates and intelligent features
  - Supports multiple templates: console, api, web, agent, library
  - Agentic features with `--agentic` flag for AI automation
  - MCP integration with `--mcp` flag for AI tool connectivity
  - Interactive mode when no project name provided
  - Force overwrite with `--force` flag

- **`pks agent`** - AI agent management (create, list, status, remove)

- **`pks deploy`** - Intelligent deployment orchestration

- **`pks status`** - System monitoring with real-time insights

- **`pks ascii`** - ASCII art generation with animations

## Platform Support

PKS CLI supports the following platforms:

- Linux x64
- Linux ARM64
- macOS x64 (Intel)
- macOS ARM64 (Apple Silicon)
- Windows x64
- Windows ARM64

The correct binary for your platform is automatically selected during installation.

## Requirements

- Node.js 18.0.0 or higher (for npm installation)
- .NET 10.0 or higher (for .NET tool installation)

## Features

- Beautiful terminal UI with Spectre.Console
- AI-powered development assistance
- Model Context Protocol (MCP) integration
- Kubernetes deployment capabilities
- Project scaffolding with multiple templates
- Interactive prompts for ease of use
- Development container support
- Git hooks integration
- Comprehensive documentation generation

## Troubleshooting

### Binary Not Found

If you see "PKS CLI binary not found" after installation:

```bash
# Try forcing reinstall
npm install -g @pks-cli/cli --force

# Or install the platform-specific package manually
npm install -g @pks-cli/cli-linux-x64  # for Linux x64
npm install -g @pks-cli/cli-osx-arm64  # for macOS Apple Silicon
npm install -g @pks-cli/cli-win-x64    # for Windows x64
```

### Unsupported Platform

If your platform is not supported, please use the .NET global tool installation method:

```bash
dotnet tool install -g pks-cli
```

## Documentation

- [GitHub Repository](https://github.com/pksorensen/pks-cli)
- [Issue Tracker](https://github.com/pksorensen/pks-cli/issues)
- [Architecture Guide](https://github.com/pksorensen/pks-cli/blob/main/docs/ARCHITECTURE.md)
- [MCP Integration](https://github.com/pksorensen/pks-cli/blob/main/docs/MCP.md)

## License

MIT License - see [LICENSE](./LICENSE) for details.

## Contributing

Contributions are welcome! Please see the [GitHub repository](https://github.com/pksorensen/pks-cli) for more information.

## Support

For issues and questions:
- File an issue on [GitHub Issues](https://github.com/pksorensen/pks-cli/issues)
- Check the [documentation](https://github.com/pksorensen/pks-cli/tree/main/docs)

---

Built with love by the PKS Team
