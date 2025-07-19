# PKS CLI - The Agentic Development Platform

🤖 **The Next-Generation CLI for .NET Developers - Version 1.0.0**

PKS CLI is a comprehensive agentic development platform that revolutionizes how .NET developers create, manage, and deploy applications. Built with [Spectre.Console](https://spectreconsole.net/), it combines beautiful terminal UI with AI-powered development assistance, GitHub integration, and comprehensive project management capabilities.

## ✨ Core Features

### 🔧 Project Identity & Management
- **Unique Project IDs** - Every project gets a unique identifier for tracking
- **Cross-Component Integration** - Seamless integration between all PKS CLI tools
- **Configuration Persistence** - Project settings stored in `.pks` folder
- **Project Validation** - Comprehensive project health checks

### 🐙 GitHub Integration
- **Repository Management** - Create and configure GitHub repositories
- **Personal Access Token Management** - Secure, encrypted token storage
- **Issue Management** - Automated issue creation and tracking
- **Workflow Integration** - Support for GitHub Actions and CI/CD

### 🌐 Model Context Protocol (MCP)
- **Project-Specific MCP Servers** - Each project gets its own MCP server
- **Tool Exposure** - PKS CLI commands exposed as MCP tools
- **Resource Management** - Project resources accessible through MCP
- **AI Assistant Integration** - Seamless integration with Claude and other AI assistants

### 🤖 Agent Framework
- **Specialized Agents** - Development, testing, DevOps, documentation agents
- **Agent Lifecycle Management** - Create, start, stop, and monitor agents
- **Swarm Coordination** - Orchestrated multi-agent workflows
- **Human-AI Collaboration** - Perfect balance of automation and human oversight

### 🪝 Hooks System
- **Smart Dispatcher** - Intelligent hook execution based on context
- **Extensible Hooks** - Pre/post build, deploy, and custom hooks
- **Event-Driven Architecture** - React to project events automatically
- **Configuration Management** - Flexible hook configuration and management

### 📋 PRD Tools
- **Requirements Generation** - AI-powered requirements document creation
- **User Story Management** - Automated user story generation
- **Project Documentation** - Comprehensive project documentation tools
- **Template System** - Customizable document templates

### 🎨 Beautiful Terminal UI
- **Stunning ASCII Art** - Eye-catching welcome banners and logos
- **Rich Interactive Elements** - Progress bars, tables, spinners, and real-time updates
- **Color-Coded Output** - Intuitive color schemes for different information types
- **Professional Aesthetics** - Modern terminal experience that rivals web applications

## 🚀 Command Reference

### Project Initialization
```bash
# Basic project creation
pks init MyProject

# Interactive mode
pks init

# Full-featured agentic project
pks init MyProject --agentic --mcp --hooks --prd --github

# Create GitHub repository during init
pks init MyProject --github --create-repo --private-repo

# Specific templates
pks init MyApi --template api --agentic
pks init MyAgent --template agent --mcp
pks init MyWeb --template web --description "My web application"
```

### Agent Management
```bash
# List all agents
pks agent --list

# Create specialized agents
pks agent --create "dev-agent" --type development
pks agent --create "test-agent" --type testing
pks agent --create "devops-agent" --type devops

# Agent lifecycle
pks agent --start [agent-id]
pks agent --stop [agent-id]
pks agent --status [agent-id]
pks agent --remove [agent-id]
```

### MCP Server Management
```bash
# Start MCP server for project
pks mcp --start --project-id [project-id]

# Different transport modes
pks mcp --start --transport stdio
pks mcp --start --transport sse --port 8080

# Server management
pks mcp --status
pks mcp --logs
pks mcp --restart
pks mcp --stop
```

### GitHub Integration
```bash
# Create repository
pks github --create-repo MyRepo --private

# Check repository access
pks github --check-access

# Create issues
pks github --create-issue "Bug report" "Description of the bug"

# Configure integration
pks config github.token <your-token>
```

### Hooks Management
```bash
# List available hooks
pks hooks --list

# Execute specific hooks
pks hooks --execute pre-build
pks hooks --execute post-deploy

# Configure hooks
pks hooks --enable pre-build
pks hooks --configure post-deploy --script "custom-deploy.sh"
```

### PRD Tools
```bash
# Create PRD documents
pks prd --create

# Generate requirements
pks prd --generate-requirements

# Generate user stories
pks prd --generate-user-stories

# Validate PRD content
pks prd --validate
```

### System Status & Deployment
```bash
# Project status
pks status

# Deploy to environments
pks deploy --environment staging
pks deploy --environment production --replicas 3

# Configuration management
pks config --list
pks config --set key value
pks config --get github.token
```

## 🛠️ Installation

### Prerequisites
- .NET 8.0 SDK or later
- Git (for GitHub integration)
- Optional: Docker (for container features)

### Quick Install (Recommended)
```bash
# Install from NuGet (when published)
dotnet tool install -g pks-cli

# Verify installation
pks --version
```

### Development Install
```bash
# Clone the repository
git clone https://github.com/pksorensen/pks-cli
cd pks-cli/src

# Build and install locally
dotnet build --configuration Release
dotnet pack --configuration Release
dotnet tool install -g --add-source ./bin/Release pks-cli --force

# Verify installation
pks --help
```

### Using the Installation Script
```bash
# Quick install script (macOS/Linux)
curl -sSL https://raw.githubusercontent.com/pksorensen/pks-cli/main/install.sh | bash

# Or download and run manually
wget https://raw.githubusercontent.com/pksorensen/pks-cli/main/install.sh
chmod +x install.sh
./install.sh
```

## 🚀 Quick Start

### 1. Create Your First Project
```bash
# Initialize a new API project with all features
pks init MyAwesomeAPI --template api --agentic --mcp --github --hooks

# Follow the interactive prompts for GitHub integration
# Your project will be created with full PKS CLI integration
```

### 2. Start the MCP Server
```bash
# Start MCP server for AI integration
cd MyAwesomeAPI
pks mcp --start

# The server will expose PKS tools to AI assistants
```

### 3. Create Development Agents
```bash
# Create specialized agents for your project
pks agent --create "backend-dev" --type development
pks agent --create "api-tester" --type testing
pks agent --create "deployment" --type devops
```

### 4. Configure GitHub Integration
```bash
# Set up GitHub personal access token
pks config github.token <your-github-token>

# Create issues and manage your repository
pks github --create-issue "Implement user authentication" "Add JWT-based auth system"
```

## 🏗️ Architecture

### Core Architecture
PKS CLI is built with enterprise-grade .NET patterns:

- **Modular Initializer System** - Extensible project initialization
- **Project Identity Service** - Unique project tracking and management
- **GitHub Integration Service** - Secure repository and API management
- **MCP Server Integration** - AI assistant communication protocol
- **Agent Framework** - Multi-agent coordination and management
- **Hooks System** - Event-driven automation and workflows

### Technology Stack
- **.NET 8** - Latest framework with performance optimizations
- **Spectre.Console** - Rich terminal UI framework
- **Dependency Injection** - Microsoft.Extensions.DependencyInjection
- **HTTP Client** - Modern HTTP client for GitHub API integration
- **JSON Serialization** - System.Text.Json for configuration
- **File System Abstractions** - Cross-platform file operations

### Integration Points
- **GitHub API** - Repository management, issues, workflows
- **Model Context Protocol (MCP)** - AI assistant integration
- **Docker** - Container support and deployment
- **Kubernetes** - Orchestration and scaling
- **Azure** - Cloud deployment and services

## 🎯 Use Cases

### For Individual Developers
- **Agentic Project Setup** - AI-powered project initialization with GitHub integration
- **Development Agents** - Specialized agents for coding, testing, and documentation
- **MCP Integration** - Seamless AI assistant integration for enhanced productivity
- **Intelligent Hooks** - Automated workflows triggered by development events

### For Development Teams
- **Project Identity Management** - Centralized project tracking and coordination
- **GitHub Workflow Integration** - Automated repository management and issue tracking
- **Agent Swarm Coordination** - Multi-agent development workflows
- **PRD-Driven Development** - Requirements-first development methodology

### For Enterprise
- **Secure Token Management** - Encrypted storage of sensitive credentials
- **Cross-Platform Compatibility** - Works seamlessly across Windows, macOS, and Linux
- **Modular Architecture** - Extensible initializer and agent systems
- **Comprehensive Documentation** - Auto-generated Claude-friendly documentation

## 🚀 Version 1.0.0 Release Notes

### ✅ Core Features Implemented
- **Project Identity Service** - Unique project tracking with .pks configuration
- **GitHub Integration** - Complete repository management and API integration
- **MCP Server** - Project-specific Model Context Protocol servers
- **Agent Framework** - Multi-agent coordination and lifecycle management
- **Hooks System** - Event-driven automation and smart dispatching
- **PRD Tools** - Requirements and user story generation
- **Modular CLAUDE.md** - File inclusion system for AI assistant documentation

### ✅ Technical Achievements
- **Comprehensive DI System** - Full dependency injection throughout
- **Async/Await Patterns** - Non-blocking operations for better performance
- **Error Handling** - Robust error handling and recovery mechanisms
- **Configuration Management** - Hierarchical configuration with encryption
- **Cross-Component Integration** - Seamless integration between all components

### ✅ Developer Experience
- **Rich Terminal UI** - Beautiful Spectre.Console-based interface
- **Interactive Initialization** - Smart prompts and validation
- **Comprehensive Logging** - Detailed operation logging and debugging
- **Template System** - Flexible template-based and code-based generation

## 🔮 Future Roadmap

### Version 1.1: Enhanced AI Integration
- [ ] Advanced agent personalities and specializations
- [ ] Natural language command parsing
- [ ] Code generation and review agents
- [ ] Performance optimization recommendations

### Version 1.2: Enterprise Features
- [ ] Team collaboration tools
- [ ] Advanced security scanning
- [ ] Multi-repository management
- [ ] Enterprise authentication integration

### Version 1.3: Cloud Integration
- [ ] Azure DevOps integration
- [ ] AWS CodeCommit support
- [ ] Cloud deployment automation
- [ ] Cost optimization analytics

### Version 2.0: Ecosystem Platform
- [ ] Plugin marketplace
- [ ] Community agent sharing
- [ ] Visual Studio Code extension
- [ ] Advanced analytics dashboard

## 🤝 Contributing

We welcome contributions from the .NET community! PKS CLI is built with enterprise-grade architecture and welcomes contributors at all levels.

### Development Setup
```bash
git clone https://github.com/pksorensen/pks-cli
cd pks-cli/src
dotnet restore
dotnet build
```

### Project Structure
```
pks-cli/
├── src/                          # Main source code
│   ├── Commands/                 # Command implementations
│   ├── Infrastructure/           # Core services and DI
│   │   ├── Initializers/        # Project initialization system
│   │   └── Services/            # Business logic services
│   ├── Templates/               # Template files for generation
│   └── Program.cs               # Application entry point
├── tests/                        # Test projects
├── docs/                         # Documentation
└── install.sh                   # Installation script
```

### Running Tests
```bash
dotnet test
```

### Code Style
- Follow standard .NET conventions
- Use Spectre.Console for all terminal output
- Maintain ASCII art quality standards
- Include comprehensive XML documentation

## 📚 Documentation

- **[Getting Started Guide](docs/getting-started.md)** - Quick start tutorial
- **[Command Reference](docs/commands.md)** - Detailed command documentation
- **[AI Agents Guide](docs/agents.md)** - Working with agentic features
- **[ASCII Art Guide](docs/ascii-art.md)** - Creating beautiful terminal art
- **[Plugin Development](docs/plugins.md)** - Extending PKS CLI

## 🔧 Configuration

PKS CLI uses a hierarchical configuration system:

1. **Command Line Arguments** - Highest priority
2. **Environment Variables** - PKS_*
3. **User Configuration** - ~/.pks/config.json
4. **Project Configuration** - ./pks.config.json
5. **Defaults** - Built-in sensible defaults

Example configuration:
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
    "aiOptimization": true,
    "autoWatch": false
  }
}
```

## 🎨 ASCII Art Styles

PKS CLI includes multiple ASCII art styles:

- **Banner** - Classic bordered text
- **Block** - Large block letters
- **Digital** - Retro computer terminal style
- **StarWars** - Epic space-themed text
- **Custom** - User-defined patterns

All styles support:
- Gradient coloring
- Animation effects
- Custom colors
- Size scaling

## 🔒 Security

- **Code Signing** - All releases are digitally signed
- **Dependency Scanning** - Automated vulnerability detection
- **Secure Defaults** - Safe configuration out of the box
- **Audit Logging** - Track all agent activities

## 📊 Performance

PKS CLI is optimized for performance:

- **Fast Startup** - < 500ms typical launch time
- **Memory Efficient** - < 50MB typical usage
- **Responsive UI** - Non-blocking operations
- **Caching** - Intelligent caching for repeated operations

## 📄 License

MIT License - see [LICENSE](LICENSE) file for details.

## 🙏 Acknowledgments

- **Spectre.Console** - For the amazing terminal UI framework
- **.NET Team** - For the excellent platform and tooling
- **Community** - For feedback and contributions
- **Open Source** - Standing on the shoulders of giants

---

**Ready to revolutionize your .NET development experience?**

```bash
dotnet tool install -g pks-cli
pks init my-agentic-project --template api
```

🚀 **Welcome to the future of .NET development!**