# Getting Started with PKS CLI

Welcome to PKS CLI! This guide will help you get started with the Professional Agentic Simplifier and create your first intelligent development project.

## Prerequisites

Before you begin, ensure you have:

- **.NET 8.0 SDK** or later installed
- **PowerShell** (Windows) or **Bash/Zsh** (macOS/Linux)
- **Git** for version control
- A code editor like **Visual Studio Code** or **Visual Studio**

## Installation

### Option 1: Install as .NET Global Tool (Recommended)

```bash
# Install PKS CLI globally
dotnet tool install -g pks-cli

# Verify installation
pks --version
```

### Option 2: Install from Source

```bash
# Clone the repository
git clone https://github.com/pksorensen/pks-cli
cd pks-cli/pks-cli/src

# Build and install locally
dotnet build --configuration Release
dotnet pack --configuration Release
dotnet tool install -g --add-source ./bin/Release pks-cli --force
```

## Your First Project

Let's create your first agentic project with PKS CLI:

### 1. Initialize a New Project

```bash
# Create a new API project with agentic capabilities
pks init my-agentic-api --template api --agentic --description "My first agentic API project"
```

This command will:
- Create a new directory `my-agentic-api`
- Set up a .NET API project structure
- Configure agentic capabilities
- Add necessary dependencies and templates
- Create documentation and configuration files

### 2. Explore the Generated Structure

```bash
cd my-agentic-api
ls -la
```

You'll see a project structure like:
```
my-agentic-api/
├── .gitignore
├── my-agentic-api.csproj
├── Program.cs
├── README.md
├── CLAUDE.md          # AI development guidance
├── .mcp.json          # Model Context Protocol config
└── ...
```

### 3. Build and Run

```bash
# Build the project
dotnet build

# Run the application
dotnet run
```

## Understanding Agentic Development

PKS CLI introduces the concept of **agentic development** - a workflow where AI agents assist with various development tasks:

### What are Agents?

Agents are specialized AI assistants that help with:
- **Code Generation**: Writing boilerplate and complex logic
- **Testing**: Creating and maintaining test suites  
- **Documentation**: Generating and updating documentation
- **Architecture**: Designing system components
- **DevOps**: Managing deployments and infrastructure

### Agent Management

```bash
# Create a new agent
pks agent create --name CodeMaster --type developer

# List all agents
pks agent list

# Check agent status
pks agent status CodeMaster
```

## Interactive Mode

PKS CLI supports interactive mode for a guided experience:

```bash
# Start interactive project creation
pks init

# Follow the prompts to configure your project:
# - Project name
# - Template selection  
# - Features and capabilities
# - Description and metadata
```

## Command Overview

Here are the main commands you'll use:

| Command | Description | Example |
|---------|-------------|---------|
| `pks init` | Create new projects | `pks init my-app --template web` |
| `pks agent` | Manage AI agents | `pks agent create --name TestBot` |
| `pks deploy` | Deploy applications | `pks deploy --environment prod` |
| `pks status` | System monitoring | `pks status --watch` |
| `pks ascii` | Generate ASCII art | `pks ascii "Hello" --style banner` |
| `pks mcp` | MCP server management | `pks mcp start --port 3000` |

## Configuration

PKS CLI uses a hierarchical configuration system:

1. **Command-line arguments** (highest priority)
2. **Environment variables** (`PKS_*`)
3. **User config** (`~/.pks/config.json`)
4. **Project config** (`./pks.config.json`)
5. **Built-in defaults** (lowest priority)

### Example Configuration

Create `pks.config.json` in your project:

```json
{
  "agents": {
    "autoSpawn": true,
    "defaultType": "developer",
    "learningEnabled": true
  },
  "ui": {
    "colorScheme": "cyan",
    "animations": true,
    "asciiArt": true
  },
  "deployment": {
    "defaultEnvironment": "dev",
    "aiOptimization": true
  }
}
```

## Next Steps

Now that you have PKS CLI set up:

1. **[Explore Commands](commands/overview.md)** - Learn about all available commands
2. **[Work with Agents](tutorials/working-with-agents.md)** - Dive deeper into agent capabilities  
3. **[Set up MCP](tutorials/mcp-setup.md)** - Enable AI tool integration
4. **[Deploy Your App](tutorials/deployment-workflows.md)** - Learn deployment strategies

## Getting Help

- **Built-in Help**: `pks --help` or `pks [command] --help`
- **Documentation**: You're reading it! 📖
- **GitHub Issues**: [Report bugs or request features](https://github.com/pksorensen/pks-cli/issues)
- **Discussions**: [Ask questions and share experiences](https://github.com/pksorensen/pks-cli/discussions)

Welcome to the future of agentic .NET development! 🚀