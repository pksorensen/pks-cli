# PKS CLI Project - {{ProjectName}}

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

@COMMANDS.md @FLAGS.md @PRINCIPLES.md @RULES.md 
@MCP.md @PERSONAS.md @ORCHESTRATOR.md @MODES.md

## Project Overview

{{ProjectName}} is a {{TechStack}} {{ProjectType}} application built with modern .NET 8 practices. {{Description}}

**Project ID**: `{{ProjectId}}`  
**Repository**: {{GitHubRepository}}  
**Created**: {{CreatedAt}}

## Development Commands

{{BuildCommands}}

{{TestingCommands}}

### Package Management
```bash
# Restore dependencies
dotnet restore

# Add a package
dotnet add package [PackageName]

# Update packages
dotnet list package --outdated
dotnet add package [PackageName] --version [Version]
```

{{DeploymentCommands}}

## Architecture

{{ArchitectureSection}}

### Key Components

{{KeyComponents}}

### Available {{ProjectType}} Features
- Health monitoring and diagnostics
- Configuration management
- Dependency injection setup

### Key Dependencies
{{DependencyList}}

## Development Patterns

{{DevelopmentPatterns}}

### Service Implementation
Services should be stateless and use async/await patterns for I/O operations. Implement interfaces for better testability and follow the dependency inversion principle.

## File Organization

```
{{ProjectName}}/
{{FileOrganization}}
```

## Configuration

{{ConfigurationSection}}

{{CiCdSection}}

## PKS CLI Integration

### Project Identity
This project is managed by PKS CLI with the following integrations:
- **Project ID**: `{{ProjectId}}`
- **GitHub Integration**: {{GitHubStatus}}
- **MCP Server**: {{McpStatus}}
- **Active Agents**: {{AgentCount}}
- **Hooks Enabled**: {{HooksEnabled}}

### CLI Commands
```bash
# Check project status
pks status

# Manage agents
pks agent --list
pks agent --create [name] --type [type]

# MCP server management
pks mcp --status
pks mcp --restart

# Hooks management
pks hooks --list
pks hooks --execute [hook-name]

# PRD tools
pks prd --status
pks prd --generate-requirements
```

---

Generated on {{DateTime}} using PKS CLI Template System v1.0.0