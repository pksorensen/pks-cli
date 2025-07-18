# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

PKS CLI is a .NET 8 console application built with Spectre.Console that provides an agentic CLI tool for .NET developers. It combines beautiful terminal UI with AI-powered development assistance and Kubernetes deployment capabilities.

## Development Commands

### Building and Running

```bash
# Build the project
cd pks-cli/src
dotnet build

# Run locally during development
dotnet run -- [command] [options]

# Build and install as global tool locally
dotnet build --configuration Release
dotnet pack --configuration Release
dotnet tool install -g --add-source ./bin/Release pks-cli --force

# Test installation
pks --help
```

### Using the Init Command

```bash
# Basic project initialization
pks init MyProject

# Interactive mode (prompts for project name and details)
pks init

# Create agentic project with MCP integration
pks init MyAgent --agentic --mcp --template agent

# API project with specific template
pks init MyApi --template api --description "REST API for my application"

# Force override existing directory
pks init ExistingProject --force

# Web application with agentic features
pks init MyWebApp --template web --agentic --description "Intelligent web application"

# Console application with custom description
pks init MyConsole --template console --description "Command-line utility"
```

### Installation Script

The repository includes `pks-cli/install.sh` that automates the build and global tool installation process.

### Testing

```bash
# Run tests (when test project exists)
dotnet test
```

## Architecture

### Core Structure

- **pks-cli/src/** - Main source code
  - **Program.cs** - Entry point with command configuration and ASCII banner
  - **Commands/** - Individual command implementations
  - **Infrastructure/** - Services and dependency injection setup

### Key Components

#### Command Pattern with Spectre.Console.Cli

The application uses Spectre.Console.Cli's command pattern with:

- Base commands in `Commands/` directory
- Each command has a `Settings` class for command-line arguments/options
- Commands are registered in `Program.cs` via `app.Configure()`

#### Dependency Injection

- Uses Microsoft.Extensions.DependencyInjection
- Custom `TypeRegistrar` and `TypeResolver` in `Infrastructure/`
- Services defined in `Infrastructure/Services.cs`

#### Core Services

- **IKubernetesService** - Kubernetes deployment operations
- **IConfigurationService** - Application configuration management
- **IDeploymentService** - Deployment workflow management
- **IInitializationService** - Project initialization orchestration
- **IInitializerRegistry** - Initializer discovery and management

### Available Commands

- `pks init` - Project initialization with templates and intelligent features
  - Supports multiple templates: console, api, web, agent, library
  - Agentic features with `--agentic` flag for AI automation
  - MCP integration with `--mcp` flag for AI tool connectivity
  - Interactive mode when no project name provided
  - Force overwrite with `--force` flag
- `pks agent` - AI agent management (create, list, status, remove)
- `pks deploy` - Intelligent deployment orchestration
- `pks status` - System monitoring with real-time insights
- `pks ascii` - ASCII art generation with animations

### Key Dependencies

- **Spectre.Console** (v0.47.0) - Rich terminal UI framework
- **Spectre.Console.Cli** (v0.47.0) - Command-line interface framework
- **Microsoft.Extensions.DependencyInjection** (v8.0.0) - DI container

## Development Patterns

### Command Implementation

Each command follows this pattern:

1. Inherit from `Command<T>` where T is the settings class
2. Define settings class with `CommandArgument` and `CommandOption` attributes
3. Implement `Execute(CommandContext context, T settings)` method
4. Use interactive prompts when required parameters are missing

### UI Conventions

- Use Spectre.Console for all terminal output
- Consistent color scheme: cyan for primary, green for success, red for errors
- Progress indicators with spinners for long operations
- Tables with rounded borders for data display
- Panels with double borders for important information

### Service Implementation

Services use async/await patterns with simulated delays to represent real operations. Replace simulation code with actual implementations when integrating with real systems.

## File Organization

```
pks-cli/
├── src/
│   ├── Commands/          # Command implementations
│   │   └── InitCommand.cs # Project initialization command
│   ├── Infrastructure/    # Services and DI setup
│   │   ├── Initializers/  # Initializer system
│   │   │   ├── Base/      # Base classes for initializers
│   │   │   │   ├── BaseInitializer.cs
│   │   │   │   ├── TemplateInitializer.cs
│   │   │   │   └── CodeInitializer.cs
│   │   │   ├── Context/   # Context and models
│   │   │   │   ├── InitializationContext.cs
│   │   │   │   ├── InitializationResult.cs
│   │   │   │   └── InitializerOption.cs
│   │   │   ├── Implementations/ # Concrete initializers
│   │   │   │   ├── DotNetProjectInitializer.cs
│   │   │   │   ├── AgenticFeaturesInitializer.cs
│   │   │   │   ├── McpConfigurationInitializer.cs
│   │   │   │   ├── ClaudeDocumentationInitializer.cs
│   │   │   │   └── ReadmeInitializer.cs
│   │   │   ├── Registry/   # Initializer discovery and management
│   │   │   │   ├── IInitializerRegistry.cs
│   │   │   │   └── InitializerRegistry.cs
│   │   │   ├── Service/    # Orchestration service
│   │   │   │   ├── IInitializationService.cs
│   │   │   │   ├── InitializationService.cs
│   │   │   │   └── Models.cs
│   │   │   └── IInitializer.cs # Core initializer interface
│   │   └── Services.cs    # DI service registration
│   ├── Templates/         # Template files for initializers
│   │   ├── mcp/          # MCP configuration templates
│   │   ├── claude/       # Claude documentation templates
│   │   └── docs/         # Additional documentation templates
│   ├── Program.cs         # Entry point
│   └── pks-cli.csproj     # Project configuration
├── tests/                 # Test projects (empty)
├── docs/                  # Documentation (empty)
├── README.md              # Project documentation
└── install.sh             # Installation script
```

## Configuration

The application is designed to be packaged as a .NET Global Tool with:

- Tool command name: `pks`
- Package ID: `pks-cli`
- Target framework: .NET 8.0
- Output configured for global tool packaging

## Initializer System

The PKS CLI features a sophisticated initializer system that enables modular, extensible project initialization. The system supports both template-based and code-based initializers that can be combined to create rich project scaffolding.

### Architecture Components

#### IInitializer Interface
Core interface that all initializers must implement:
- **Id** - Unique identifier for the initializer
- **Name** - Human-readable display name
- **Description** - What the initializer does
- **Order** - Execution priority (lower numbers run first)
- **ShouldRunAsync()** - Conditional execution logic
- **ExecuteAsync()** - Main initialization logic
- **GetOptions()** - Command-line options contributed

#### Initializer Types

**Template-Based Initializers (TemplateInitializer)**
- Work with file templates containing placeholders
- Support recursive directory processing
- Handle binary files and text files differently
- Built-in placeholder replacement: `{{ProjectName}}`, `{{Description}}`, etc.
- Support for custom placeholders and post-processing

**Code-Based Initializers (CodeInitializer)**
- Generate files and content programmatically
- Full control over file creation and modification
- Ideal for complex logic and conditional content generation
- Support for modifying existing files

#### InitializationService
Orchestrates the entire initialization process:
- Validates target directories
- Creates initialization contexts
- Coordinates initializer execution
- Provides progress tracking and error handling
- Generates comprehensive summary reports

#### InitializerRegistry
Manages initializer discovery and execution:
- Automatic discovery and registration
- Dependency injection integration
- Order-based execution
- Conditional initializer filtering

### Available Initializers

| Initializer | Type | Order | Description |
|-------------|------|-------|-------------|
| **DotNetProjectInitializer** | Code | 10 | Creates .NET project structure (.csproj, Program.cs, .gitignore) |
| **AgenticFeaturesInitializer** | Code | 50 | Adds AI automation capabilities and agent framework |
| **McpConfigurationInitializer** | Template | 75 | Configures Model Context Protocol for AI tool integration |
| **ClaudeDocumentationInitializer** | Template | 80 | Generates Claude-specific documentation (CLAUDE.md) |
| **ReadmeInitializer** | Code | 90 | Creates comprehensive README.md with project details |

### Template System

Templates are stored in the `Templates/` directory with the following structure:
```
Templates/
├── mcp/                    # MCP configuration templates
│   ├── .mcp.json          # MCP server configuration
│   └── mcp-config.yml     # Additional MCP settings
├── claude/                 # Claude documentation templates
│   └── CLAUDE.md          # Project-specific Claude guidance
└── docs/                   # Additional documentation templates
    └── README.template.md  # README template
```

#### Placeholder System
Templates support placeholder replacement using `{{PlaceholderName}}` syntax:
- `{{ProjectName}}` - The project name
- `{{Description}}` - Project description
- `{{Template}}` - Selected template type
- `{{DateTime}}` - Current timestamp
- Custom placeholders defined by individual initializers

## MCP Integration

The Model Context Protocol (MCP) integration enables seamless AI tool connectivity:

### Configuration
- **Transport Modes**: stdio (local) and SSE (remote)
- **Authentication**: OAuth 2.0 support for secure connections
- **Tool Discovery**: Automatic exposure of PKS CLI commands as MCP tools
- **Environment Variables**: Secure configuration through env vars

### Setup Examples
```bash
# Enable MCP with stdio transport
pks init MyProject --mcp --enable-stdio

# Enable MCP with remote SSE transport
pks init MyProject --mcp --enable-sse --server-url https://api.example.com

# MCP with authentication
pks init MyProject --mcp --enable-auth
```

### Generated Files
- `.mcp.json` - MCP server configuration
- `mcp-config.yml` - Extended MCP settings
- Environment variable templates for secure credential management

## Template Development

### Creating Custom Initializers

**Template-Based Initializer:**
```csharp
public class MyTemplateInitializer : TemplateInitializer
{
    public override string Id => "my-template";
    public override string Name => "My Template";
    public override string Description => "Creates my custom template";
    public override int Order => 60;
    
    protected override string TemplateDirectory => "my-template";
    
    public override async Task<bool> ShouldRunAsync(InitializationContext context)
    {
        return context.GetOption("my-feature", false);
    }
}
```

**Code-Based Initializer:**
```csharp
public class MyCodeInitializer : CodeInitializer
{
    public override string Id => "my-code";
    public override string Name => "My Code Generator";
    public override string Description => "Generates custom code";
    public override int Order => 40;
    
    protected override async Task ExecuteCodeLogicAsync(
        InitializationContext context, 
        InitializationResult result)
    {
        var content = GenerateCustomCode(context);
        await CreateFileAsync("CustomFile.cs", content, context, result);
    }
}
```

### Registration
Initializers are automatically discovered and registered through reflection. Place custom initializers in the `Infrastructure/Initializers/Implementations/` directory.

## Orchestrator

You should work as the coordinator of the work we are doing.
assign sub tasks/agents to do the work and dont do things your self.
Do TDD so you setup the test first when you have made a design and then implment.
