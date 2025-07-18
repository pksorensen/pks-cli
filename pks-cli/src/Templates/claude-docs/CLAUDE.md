# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

{{ProjectName}} is a {{TechStack}} {{ProjectType}} application built with modern .NET 8 practices. {{Description}}

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

## Important Instructions

### Development Guidelines
- NEVER create files unless absolutely necessary for achieving the goal
- ALWAYS prefer editing existing files to creating new ones
- NEVER proactively create documentation files (*.md) or README files unless explicitly requested
- Follow the existing code patterns and architecture decisions
- Use the established naming conventions and folder structure
- Write unit tests for new functionality using {{TestFramework}}
- Update this CLAUDE.md file when making architectural changes

### Code Quality Standards
- Follow C# coding conventions and StyleCop rules
- Maintain minimum 80% code coverage
- Use meaningful variable and method names
- Write XML documentation for public APIs
- Implement proper async/await patterns
- Use dependency injection for loose coupling

### Security Considerations
- Never hardcode secrets in code
- Use HTTPS for all external communications
- Implement proper authentication and authorization
- Validate all inputs and sanitize outputs
- Follow OWASP security guidelines

### Performance Guidelines
- Use async/await for I/O operations
- Implement proper caching strategies
- Monitor memory usage and implement proper disposal patterns
- Use efficient LINQ operations and avoid N+1 queries
- Profile performance critical paths

---

Generated on {{Date}} using PKS CLI Template System