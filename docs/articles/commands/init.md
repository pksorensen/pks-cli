# pks init - Project Initialization

The `pks init` command is the cornerstone of PKS CLI, designed to create intelligent, well-structured projects with agentic capabilities built-in from day one.

## Syntax

```bash
pks init [<PROJECT_NAME>] [OPTIONS]
```

## Description

`pks init` creates a new project with:
- **Smart Templates** - Choose from predefined project templates
- **Agentic Integration** - Optional AI agent capabilities
- **MCP Configuration** - Model Context Protocol for AI tools
- **Best Practices** - Modern project structure and tooling
- **Documentation** - Comprehensive README and development guides

## Arguments

| Argument | Description |
|----------|-------------|
| `PROJECT_NAME` | Name of the project (optional - will prompt if not provided) |

## Options

### Basic Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--template <TYPE>` | `-t` | Project template to use | `console` |
| `--description <TEXT>` | `-d` | Project description | |
| `--force` | `-f` | Overwrite existing directory | `false` |
| `--no-interactive` | | Skip interactive prompts | `false` |

### Advanced Options

| Option | Description | Default |
|--------|-------------|---------|
| `--agentic` | Enable agentic capabilities | `false` |
| `--mcp` | Include MCP (Model Context Protocol) setup | `false` |
| `--git-init` | Initialize Git repository | `true` |
| `--install-deps` | Install NuGet dependencies after creation | `true` |
| `--open-editor` | Open project in default editor | `false` |

### Template-Specific Options

| Option | Applies To | Description |
|--------|------------|-------------|
| `--framework <VERSION>` | All | .NET framework version (default: net8.0) |
| `--auth <TYPE>` | api, web | Authentication type (none, jwt, oauth) |
| `--database <TYPE>` | api, web | Database provider (none, sqlite, sqlserver, postgres) |
| `--frontend <TYPE>` | web | Frontend framework (none, blazor, react, vue) |

## Templates

### Available Templates

| Template | Description | Best For |
|----------|-------------|----------|
| `console` | Console application | CLI tools, utilities, background services |
| `api` | REST API with controllers | Web APIs, microservices, data services |
| `web` | Web application | Full-stack applications, websites |
| `agent` | Specialized agent project | AI agents, automation tools |
| `library` | Class library | Reusable components, NuGet packages |

### Template Features

#### Console Template
```bash
pks init my-console --template console --agentic
```
**Generated Structure:**
```
my-console/
├── Program.cs              # Entry point with dependency injection
├── Commands/               # Command pattern structure
├── Services/               # Business logic services
├── my-console.csproj      # Project file with common packages
├── README.md              # Comprehensive documentation
├── .gitignore             # .NET specific ignores
└── CLAUDE.md              # AI development guidance (if --agentic)
```

#### API Template
```bash
pks init my-api --template api --auth jwt --database postgres --agentic
```
**Generated Structure:**
```
my-api/
├── Controllers/           # API controllers
├── Models/               # Data models and DTOs
├── Services/             # Business logic
├── Data/                 # Database context and migrations
├── Program.cs            # Startup configuration
├── appsettings.json      # Configuration
├── my-api.csproj         # Project with API packages
└── ...
```

#### Web Template
```bash
pks init my-web --template web --frontend blazor --auth oauth --agentic
```
**Generated Structure:**
```
my-web/
├── Pages/                # Razor pages or components
├── Components/           # Reusable UI components
├── wwwroot/             # Static files
├── Controllers/          # MVC controllers
├── Models/              # View models and entities
├── Services/            # Application services
├── Program.cs           # Web host configuration
└── ...
```

## Interactive Mode

When you run `pks init` without arguments, it enters interactive mode:

```bash
pks init
```

**Interactive Prompts:**
```
🤖 PKS CLI Project Initialization

? What is your project name? my-awesome-project
? Choose a template: 
  > console
    api
    web
    agent
    library

? Project description: A revolutionary CLI tool
? Enable agentic capabilities? (Y/n) Y
? Include MCP integration? (Y/n) Y
? Initialize Git repository? (Y/n) Y
? Open in editor after creation? (Y/n) n

✨ Creating project with the following configuration:
  • Name: my-awesome-project
  • Template: console
  • Agentic: enabled
  • MCP: enabled
  • Git: enabled

🚀 Project created successfully!
```

## Examples

### Basic Project Creation

```bash
# Simple console application
pks init calculator --template console

# API with description
pks init todo-api --template api --description "A simple todo API"

# Web application with authentication
pks init my-blog --template web --auth jwt --frontend blazor
```

### Agentic Projects

```bash
# Console app with AI capabilities
pks init smart-cli --template console --agentic --mcp

# AI-powered API
pks init intelligent-api --template api --agentic --description "API with AI agents"

# Full-stack agentic application
pks init agentic-web --template web --agentic --mcp --frontend react
```

### Advanced Scenarios

```bash
# Override existing directory
pks init existing-project --force --template api

# Custom framework version
pks init legacy-app --template console --framework net6.0

# No interactive prompts (CI/CD friendly)
pks init ci-project --template api --description "CI build" --no-interactive

# Complete setup with all features
pks init full-stack \
  --template web \
  --agentic \
  --mcp \
  --auth oauth \
  --database postgres \
  --frontend react \
  --description "Full-featured application" \
  --open-editor
```

## Configuration

### Project Configuration File

The init command can use a configuration file for defaults:

**pks.init.json:**
```json
{
  "defaults": {
    "template": "api",
    "enableAgentic": true,
    "enableMcp": true,
    "framework": "net8.0",
    "auth": "jwt",
    "database": "postgres"
  },
  "templates": {
    "api": {
      "packages": [
        "Microsoft.AspNetCore.Authentication.JwtBearer",
        "Swashbuckle.AspNetCore"
      ]
    }
  }
}
```

Use with:
```bash
pks init my-project --config pks.init.json
```

### Environment Variables

```bash
export PKS_DEFAULT_TEMPLATE=api
export PKS_ENABLE_AGENTIC=true
export PKS_DEFAULT_AUTH=jwt
export PKS_AUTO_OPEN_EDITOR=true
```

## Generated Files

### Core Files (All Templates)

| File | Description |
|------|-------------|
| `{ProjectName}.csproj` | Project file with dependencies |
| `Program.cs` | Application entry point |
| `.gitignore` | Git ignore rules for .NET |
| `README.md` | Project documentation |

### Agentic Files (--agentic flag)

| File | Description |
|------|-------------|
| `CLAUDE.md` | AI development guidance |
| `agents/` | Agent configuration directory |
| `pks.config.json` | PKS CLI configuration |

### MCP Files (--mcp flag)

| File | Description |
|------|-------------|
| `.mcp.json` | MCP server configuration |
| `mcp-config.yml` | Extended MCP settings |
| `mcp/` | MCP tools and prompts |

## Initializer System

PKS CLI uses a sophisticated initializer system to create projects:

### Execution Order

1. **DotNetProjectInitializer** (Order: 10) - Creates basic .NET structure
2. **AgenticFeaturesInitializer** (Order: 50) - Adds AI capabilities  
3. **McpConfigurationInitializer** (Order: 75) - Configures MCP
4. **ClaudeDocumentationInitializer** (Order: 80) - Generates AI docs
5. **ReadmeInitializer** (Order: 90) - Creates comprehensive README

### Custom Initializers

You can create custom initializers for specific needs:

```bash
# List available initializers
pks init --list-initializers

# Run specific initializers only
pks init my-project --initializers dotnet,agentic --template api
```

## Troubleshooting

### Common Issues

#### Directory Already Exists
```
❌ Error: Directory 'my-project' already exists
💡 Use --force to overwrite or choose a different name
```

**Solutions:**
```bash
# Option 1: Use force flag
pks init my-project --force

# Option 2: Different name
pks init my-project-v2

# Option 3: Remove existing directory
rm -rf my-project && pks init my-project
```

#### Template Not Found
```
❌ Error: Template 'custom' not found
💡 Available templates: console, api, web, agent, library
```

**Solution:**
```bash
# List available templates
pks init --list-templates

# Use valid template
pks init my-project --template api
```

#### Missing Dependencies
```
❌ Error: Failed to restore NuGet packages
💡 Ensure internet connection and valid NuGet sources
```

**Solutions:**
```bash
# Skip dependency installation
pks init my-project --no-install-deps

# Manually restore later
cd my-project && dotnet restore

# Clear NuGet cache
dotnet nuget locals all --clear
```

## Best Practices

### Naming Conventions
- Use **kebab-case** for project names: `my-awesome-api`
- Avoid special characters except hyphens
- Keep names concise but descriptive

### Template Selection
- **console** - CLI tools, services, utilities
- **api** - REST APIs, web services, microservices  
- **web** - Full applications with UI
- **agent** - AI agents and automation tools
- **library** - Shared code and NuGet packages

### Agentic Features
- Enable `--agentic` for projects that will benefit from AI assistance
- Use `--mcp` when integrating with AI tools like Claude or GitHub Copilot
- Consider the learning curve for team members new to agentic development

## Integration

### CI/CD Pipelines

```yaml
# GitHub Actions
- name: Initialize Project
  run: |
    pks init ${{ github.event.repository.name }} \
      --template api \
      --no-interactive \
      --description "Auto-generated API project"

# Azure DevOps  
- script: |
    pks init $(Build.Repository.Name) --template web --no-interactive
  displayName: 'Initialize Project'
```

### Development Workflows

```bash
# Team development setup
pks init team-project --template api --agentic --mcp
cd team-project
pks agent create --name TeamBot --type developer --shared
git add . && git commit -m "Initial project setup"
git push origin main
```

## Next Steps

After creating a project with `pks init`:

1. **[Explore the generated structure](../tutorials/first-project.md)**
2. **[Set up agents](../commands/agent.md)** for AI assistance
3. **[Configure MCP](../tutorials/mcp-setup.md)** for tool integration
4. **[Deploy your application](../commands/deploy.md)** when ready

Ready to create your first agentic project? 🚀

```bash
pks init my-first-agentic-app --template api --agentic --mcp
```