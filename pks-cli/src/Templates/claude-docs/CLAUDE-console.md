# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

{{ProjectName}} is a {{TechStack}} console application built with modern .NET 8 practices and command-line interface patterns. {{Description}}

## Development Commands

### Building and Running
```bash
# Build the project
cd {{ProjectName}}
dotnet build

# Run locally during development
dotnet run -- [command] [options]

# Build in release mode
dotnet build --configuration Release

# Build and install as global tool (if configured)
dotnet build --configuration Release
dotnet pack --configuration Release
dotnet tool install -g --add-source ./bin/Release {{project_name}} --force

# Test global tool installation
{{project_name}} --help
```

### Testing
```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test project
dotnet test tests/{{ProjectName}}.Tests

# Watch mode for continuous testing
dotnet watch test
```

## Architecture

### Core Structure
- **{{ProjectName}}/** - Main source code
  - **Commands/** - Individual command implementations
  - **Services/** - Business logic and application services
  - **Infrastructure/** - Configuration and dependency injection
  - **Models/** - Data models and DTOs

### Key Components

#### Console Application with Command Pattern
- **Program.cs** - Entry point with command configuration and setup
- **Commands/** - Command implementations using Spectre.Console.Cli pattern
- **Services/** - Core business logic and operations
- **Infrastructure/** - Configuration, dependency injection, and external integrations

### Available Commands
- `help` - Display available commands and options
- `version` - Show application version information
- `config` - Manage application configuration

### Key Dependencies
- **Microsoft.Extensions.Hosting** - Generic host and dependency injection
- **Microsoft.Extensions.Configuration** - Configuration management
- **Spectre.Console** - Rich terminal UI framework (if using)
- **Serilog** - Structured logging

## Development Patterns

### Command Implementation
Each command follows this pattern:
1. Inherit from `Command<T>` where T is the settings class (if using Spectre.Console.Cli)
2. Define settings class with command arguments and options
3. Implement execute method with business logic
4. Use dependency injection for services

### Console UI Conventions
- Use consistent color schemes for output
- Implement progress indicators for long operations
- Provide clear error messages and help text
- Support both interactive and non-interactive modes

### Service Implementation
Services use async/await patterns and dependency injection. Replace simulation code with actual implementations when integrating with real systems.

## File Organization

```
{{ProjectName}}/
├── Commands/            # Command implementations
├── Services/           # Business logic services
├── Infrastructure/     # Configuration and DI setup
├── Models/             # Data models and DTOs
├── Tests/             # Unit and integration tests
├── appsettings.json   # Configuration settings
├── Program.cs         # Entry point and command setup
└── {{ProjectName}}.csproj   # Project configuration
```

## Configuration

The application uses .NET configuration system with appsettings.json:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  },
  "ApplicationSettings": {
    "Version": "1.0.0",
    "Environment": "{{PROJECT_NAME}}_ENV"
  }
}
```

Configuration is loaded from:
- appsettings.json (base configuration)
- appsettings.{Environment}.json (environment-specific)
- Environment variables
- Command line arguments

## Global Tool Configuration (Optional)

If packaging as a .NET Global Tool:

```xml
<PropertyGroup>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>{{project_name}}</ToolCommandName>
  <PackageOutputPath>./nupkg</PackageOutputPath>
  <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
</PropertyGroup>
```

## Important Instructions

### Development Guidelines
- NEVER create files unless absolutely necessary for achieving the goal
- ALWAYS prefer editing existing files to creating new ones
- NEVER proactively create documentation files (*.md) or README files unless explicitly requested
- Follow command pattern for new features
- Use dependency injection for loose coupling
- Implement proper async/await patterns
- Provide meaningful help text and error messages

### Command Design Principles
- Each command should have a single, clear responsibility
- Use descriptive command names and aliases
- Provide comprehensive help documentation
- Support both verbose and quiet modes
- Implement proper error handling and exit codes

### User Experience Guidelines
- Provide immediate feedback for long-running operations
- Use progress bars or spinners for visual feedback
- Implement graceful cancellation with Ctrl+C
- Support both interactive prompts and non-interactive modes
- Use consistent styling and formatting

---

Generated on {{Date}} using PKS CLI Template System