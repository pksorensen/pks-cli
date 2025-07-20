using PKS.Infrastructure.Initializers.Base;
using PKS.Infrastructure.Initializers.Context;

namespace PKS.Infrastructure.Initializers.Implementations;

/// <summary>
/// Template-based initializer for creating comprehensive CLAUDE.md documentation
/// </summary>
public class ClaudeDocumentationInitializer : TemplateInitializer
{
    public override string Id => "claude-docs";
    public override string Name => "CLAUDE.md Documentation";
    public override string Description => "Creates comprehensive CLAUDE.md project documentation for AI assistants";
    public override int Order => 85; // Run after project files but before README

    protected override string TemplateDirectory => "claude-modular";

    public override IEnumerable<InitializerOption> GetOptions()
    {
        return new[]
        {
            InitializerOption.String("tech-stack", "Primary technology stack", "t", ""),
            InitializerOption.String("project-type", "Type of project (console, web, api, agent, library)", "p", "console"),
            InitializerOption.Flag("include-tdd", "Include TDD practices and testing guidance", "tdd"),
            InitializerOption.Flag("include-docker", "Include Docker development commands", "d"),
            InitializerOption.Flag("include-k8s", "Include Kubernetes deployment commands", "k"),
            InitializerOption.Flag("include-azure", "Include Azure deployment guidance", "a"),
            InitializerOption.String("test-framework", "Testing framework (xUnit, NUnit, MSTest)", "tf", "xUnit"),
            InitializerOption.String("architecture-pattern", "Architecture pattern (Clean, Onion, Hexagonal, Layered)", "arch", "Clean"),
            InitializerOption.Flag("include-ci-cd", "Include CI/CD pipeline guidance", "ci")
        };
    }

    public override Task<bool> ShouldRunAsync(InitializationContext context)
    {
        // Always run since we generate content inline when templates don't exist
        return Task.FromResult(true);
    }

    protected override async Task<InitializationResult> ExecuteInternalAsync(InitializationContext context)
    {
        var result = InitializationResult.CreateSuccess("Generated CLAUDE.md documentation");

        // Check if we have specific templates for the project type
        var projectType = context.GetOption("project-type", "console");
        var specificTemplatePath = Path.Combine(TemplatePath, $"CLAUDE-{projectType}.md");
        
        if (File.Exists(specificTemplatePath))
        {
            // Process only the specific template file
            var content = await File.ReadAllTextAsync(specificTemplatePath);
            var processedContent = await ProcessTemplateContentAsync(content, specificTemplatePath, "CLAUDE.md", context);
            var targetPath = Path.Combine(context.TargetDirectory, "CLAUDE.md");
            
            await WriteFileAsync(targetPath, processedContent, context);
            result.AffectedFiles.Add(targetPath);
            result.Message = $"Generated CLAUDE.md documentation from {projectType} template";
            
            return result;
        }
        else
        {
            // Fallback to inline generation
            var claudeContent = GenerateClaudeDocumentation(context);
            var claudePath = Path.Combine(context.TargetDirectory, "CLAUDE.md");

            await WriteFileAsync(claudePath, claudeContent, context);
            result.AffectedFiles.Add(claudePath);

            result.Message = "Generated comprehensive CLAUDE.md documentation";
            return result;
        }
    }

    protected override async Task<string> ProcessTemplateContentAsync(string content, string templateFile, string targetFile, InitializationContext context)
    {
        // Process file inclusions first
        content = await ProcessFileInclusionsAsync(content, context);
        
        // Add custom placeholder replacements for CLAUDE.md specific content
        var customPlaceholders = GetCustomPlaceholders(context);
        return ReplacePlaceholdersWithCustom(content, context, customPlaceholders);
    }

    private Dictionary<string, string> GetCustomPlaceholders(InitializationContext context)
    {
        var projectType = context.GetOption("project-type", "console");
        var techStack = context.GetOption("tech-stack", InferTechStack(context, projectType ?? "console")) ?? ".NET 8 Console Application";
        var testFramework = context.GetOption("test-framework", "xUnit");
        var architecturePattern = context.GetOption("architecture-pattern", "Clean");
        var includeTdd = context.GetOption("include-tdd", false);
        var includeDocker = context.GetOption("include-docker", false);
        var includeK8s = context.GetOption("include-k8s", false);
        var includeAzure = context.GetOption("include-azure", false);
        var includeCiCd = context.GetOption("include-ci-cd", false);

        // Get project identity information from context
        var projectId = context.GetMetadata<string>("ProjectId") ?? "not-set";
        var gitHubRepository = context.GetOption("remote-url", "Not configured");
        var mcpEnabled = context.GetOption("mcp", false);
        var agenticEnabled = context.GetOption("agentic", false);
        var hooksEnabled = context.GetOption("hooks", false);

        return new Dictionary<string, string>
        {
            { "{{TechStack}}", techStack },
            { "{{ProjectType}}", projectType ?? "console" },
            { "{{TestFramework}}", testFramework ?? "xUnit" },
            { "{{ArchitecturePattern}}", architecturePattern ?? "Clean" },
            { "{{BuildCommands}}", GenerateBuildCommands(context, projectType ?? "console", includeDocker) },
            { "{{TestingCommands}}", GenerateTestingCommands(context, testFramework ?? "xUnit", includeTdd) },
            { "{{ArchitectureSection}}", GenerateArchitectureSection(context, projectType ?? "console", architecturePattern ?? "Clean") },
            { "{{DevelopmentPatterns}}", GenerateDevelopmentPatterns(context, projectType ?? "console", includeTdd) },
            { "{{FileOrganization}}", GenerateFileOrganization(context, projectType ?? "console", architecturePattern ?? "Clean") },
            { "{{ConfigurationSection}}", GenerateConfigurationSection(context, projectType ?? "console") },
            { "{{DeploymentCommands}}", GenerateDeploymentCommands(context, includeDocker, includeK8s, includeAzure) },
            { "{{CiCdSection}}", includeCiCd ? GenerateCiCdSection(context) : "" },
            { "{{DependencyList}}", GenerateDependencyList(context, projectType ?? "console") },
            { "{{KeyComponents}}", GenerateKeyComponents(context, projectType ?? "console") },
            
            // Project Identity placeholders
            { "{{ProjectId}}", projectId },
            { "{{GitHubRepository}}", gitHubRepository ?? "Not configured" },
            { "{{CreatedAt}}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" },
            { "{{DateTime}}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" },
            
            // Integration status placeholders
            { "{{GitHubStatus}}", gitHubRepository != "Not configured" ? $"Connected to {gitHubRepository}" : "Not configured" },
            { "{{McpStatus}}", mcpEnabled ? "Enabled" : "Not configured" },
            { "{{McpServerId}}", mcpEnabled ? $"pks-{projectId}" : "Not configured" },
            { "{{McpTransport}}", mcpEnabled ? "stdio" : "N/A" },
            { "{{AgentCount}}", agenticEnabled ? "Multiple agents registered" : "0" },
            { "{{HooksEnabled}}", hooksEnabled ? "Yes" : "No" }
        };
    }

    private async Task<string> ProcessFileInclusionsAsync(string content, InitializationContext context)
    {
        // Process @FILENAME.md patterns for file inclusion
        var includePattern = @"@([A-Z]+\.md)";
        var matches = System.Text.RegularExpressions.Regex.Matches(content, includePattern);
        
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var fileName = match.Groups[1].Value;
            var includePath = Path.Combine(TemplatePath, fileName);
            
            if (File.Exists(includePath))
            {
                try
                {
                    var includeContent = await File.ReadAllTextAsync(includePath);
                    
                    // Process placeholders in the included content
                    var customPlaceholders = GetCustomPlaceholders(context);
                    includeContent = ReplacePlaceholdersWithCustom(includeContent, context, customPlaceholders);
                    
                    // Replace the @FILENAME.md with the actual content
                    content = content.Replace(match.Value, includeContent);
                }
                catch (Exception ex)
                {
                    // If file inclusion fails, replace with a comment
                    content = content.Replace(match.Value, $"<!-- Failed to include {fileName}: {ex.Message} -->");
                }
            }
            else
            {
                // If file doesn't exist, replace with a comment
                content = content.Replace(match.Value, $"<!-- {fileName} not found -->");
            }
        }
        
        return content;
    }

    private string GenerateClaudeDocumentation(InitializationContext context)
    {
        var projectType = context.GetOption("project-type", "console");
        var techStack = context.GetOption("tech-stack", InferTechStack(context, projectType ?? "console")) ?? ".NET 8 Console Application";
        var testFramework = context.GetOption("test-framework", "xUnit");
        var architecturePattern = context.GetOption("architecture-pattern", "Clean");
        var includeTdd = context.GetOption("include-tdd", false);
        var includeDocker = context.GetOption("include-docker", false);
        var includeK8s = context.GetOption("include-k8s", false);
        var includeAzure = context.GetOption("include-azure", false);
        var includeCiCd = context.GetOption("include-ci-cd", false);

        var tddSection = includeTdd ? GenerateTddSection(testFramework ?? "xUnit") : "";
        var dockerSection = includeDocker ? GenerateDockerSection() : "";
        var k8sSection = includeK8s ? GenerateKubernetesSection() : "";
        var azureSection = includeAzure ? GenerateAzureSection() : "";
        var ciCdSection = includeCiCd ? GenerateCiCdSection(context) : "";

        return $@"# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

{context.ProjectName} is a {techStack} {projectType} application built with modern .NET 8 practices. {context.Description ?? $"This project implements {architecturePattern} architecture patterns and follows industry best practices for maintainable, scalable code."}

## Development Commands

### Building and Running
```bash
# Build the project
cd {context.ProjectName}
dotnet build

# Run locally during development
dotnet run{(projectType == "console" ? " -- [command] [options]" : "")}

# Build in release mode
dotnet build --configuration Release
{(includeDocker ? @"
# Build with Docker
docker build -t {{project_name}} .

# Run with Docker
docker run -p 8080:8080 {{project_name}}" : "")}
```

{dockerSection}

### Testing
```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test --collect:""XPlat Code Coverage""

# Run specific test project
dotnet test tests/{context.ProjectName}.Tests

# Watch mode for continuous testing
dotnet watch test
{(includeTdd ? @"
# Run tests in TDD mode with detailed output
dotnet test --logger ""console;verbosity=detailed""

# Run only failing tests
dotnet test --filter Category=Integration" : "")}
```

{tddSection}

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

{k8sSection}

{azureSection}

## Architecture

### Core Structure
- **{context.ProjectName}/** - Main source code
{GenerateProjectStructure(projectType ?? "console", architecturePattern ?? "Clean")}

### Key Components

{GenerateKeyComponents(context, projectType ?? "console")}

### Available {((projectType ?? "console") == "console" ? "Commands" : "Endpoints")}
{GenerateAvailableFeatures(projectType ?? "console")}

### Key Dependencies
{GenerateDependencyList(context, projectType ?? "console")}

## Development Patterns

### {architecturePattern ?? "Clean"} Architecture
{GenerateArchitectureGuidance(architecturePattern ?? "Clean")}

### Code Organization
{GenerateCodeOrganizationGuidance(projectType ?? "console")}

### Error Handling
- Use custom exceptions for business logic errors
- Implement global exception handling middleware{(projectType == "web" || projectType == "api" ? "" : " patterns")}
- Log errors with structured logging using Serilog or built-in ILogger

### Configuration Management
- Use strongly-typed configuration with IOptions<T>
- Separate configuration by environment (Development, Staging, Production)
- Store sensitive data in Azure Key Vault or environment variables
- Validate configuration on startup

{tddSection}

### Service Implementation
{GenerateServiceImplementationGuidance(projectType ?? "console")}

## File Organization

```
{context.ProjectName}/
{GenerateFileOrganization(context, projectType ?? "console", architecturePattern ?? "Clean")}
```

## Configuration

{GenerateConfigurationSection(context, projectType ?? "console")}

{ciCdSection}

## Important Instructions

### Development Guidelines
- NEVER create files unless absolutely necessary for achieving the goal
- ALWAYS prefer editing existing files to creating new ones
- NEVER proactively create documentation files (*.md) or README files unless explicitly requested
- Follow the existing code patterns and architecture decisions
- Use the established naming conventions and folder structure
- Write unit tests for new functionality using {testFramework}
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
";
    }

    private string InferTechStack(InitializationContext context, string projectType)
    {
        var template = context.Template.ToLowerInvariant();
        
        return projectType.ToLowerInvariant() switch
        {
            "web" => ".NET 8 Web Application with ASP.NET Core",
            "api" => ".NET 8 Web API with ASP.NET Core",
            "agent" => ".NET 8 Console Application with AI/ML capabilities",
            "library" => ".NET 8 Class Library",
            "blazor" => ".NET 8 Blazor Application",
            "maui" => ".NET 8 Multi-platform App (MAUI)",
            _ => ".NET 8 Console Application"
        };
    }

    private string GenerateProjectStructure(string projectType, string architecturePattern)
    {
        return projectType.ToLowerInvariant() switch
        {
            "web" or "api" => $@"  - **Controllers/** - API controllers and endpoints
  - **Services/** - Business logic and application services
  - **Models/** - Data models and DTOs
  - **Infrastructure/** - Data access and external integrations
  - **Middleware/** - Custom middleware components
  - **Configuration/** - Startup and configuration classes",
            
            "console" => $@"  - **Commands/** - Individual command implementations
  - **Services/** - Business logic and application services
  - **Infrastructure/** - Configuration and dependency injection
  - **Models/** - Data models and DTOs",
            
            _ => $@"  - **Core/** - Domain models and business logic
  - **Infrastructure/** - Data access and external services
  - **Application/** - Use cases and application services
  - **Presentation/** - UI/API layer"
        };
    }

    private string GenerateKeyComponents(InitializationContext context, string projectType)
    {
        return projectType.ToLowerInvariant() switch
        {
            "web" => @"#### ASP.NET Core Web Application
- **Startup.cs** - Application configuration and service registration
- **Controllers** - MVC controllers handling HTTP requests
- **Views** - Razor views for UI rendering
- **Services** - Business logic and data access services
- **Middleware** - Custom middleware for cross-cutting concerns",

            "api" => @"#### ASP.NET Core Web API
- **Controllers** - API controllers with RESTful endpoints
- **Services** - Business logic and application services
- **DTOs** - Data transfer objects for API contracts
- **Middleware** - Authentication, validation, and error handling
- **Swagger** - API documentation and testing interface",

            "console" => @"#### Console Application with Command Pattern
- **Program.cs** - Entry point with command configuration
- **Commands** - Individual command implementations
- **Services** - Core business logic and operations
- **Configuration** - Application settings and dependency injection",

            _ => @"#### Application Core Components
- **Domain Models** - Core business entities and value objects
- **Application Services** - Use case implementations
- **Infrastructure Services** - Data access and external integrations
- **Configuration** - Dependency injection and application settings"
        };
    }

    private string GenerateAvailableFeatures(string projectType)
    {
        return projectType.ToLowerInvariant() switch
        {
            "web" => @"- `GET /` - Home page
- `GET /Health` - Health check endpoint
- `GET /About` - Application information",

            "api" => @"- `GET /api/health` - Health check endpoint
- `GET /api/version` - Application version information
- `GET /swagger` - API documentation",

            "console" => @"- `help` - Display available commands and options
- `version` - Show application version information
- `config` - Manage application configuration",

            _ => @"- Core functionality based on project requirements
- Health monitoring and diagnostics
- Configuration management"
        };
    }

    private string GenerateBuildCommands(InitializationContext context, string projectType, bool includeDocker)
    {
        var commands = $@"```bash
# Build the project
dotnet build

# Build in release mode
dotnet build --configuration Release

# Publish for deployment
dotnet publish --configuration Release --output ./publish
```";

        if (includeDocker)
        {
            commands += @"

### Docker Commands
```bash
# Build Docker image
docker build -t {{project_name}} .

# Run Docker container
docker run -p 8080:8080 {{project_name}}

# Docker Compose (if available)
docker-compose up -d
```";
        }

        return commands;
    }

    private string GenerateTestingCommands(InitializationContext context, string testFramework, bool includeTdd)
    {
        var commands = $@"```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test --collect:""XPlat Code Coverage""

# Run tests in watch mode
dotnet watch test
```";

        if (includeTdd)
        {
            commands += @"

### TDD Workflow
```bash
# Run tests in verbose mode
dotnet test --logger ""console;verbosity=detailed""

# Run specific test category
dotnet test --filter Category=Unit

# Run integration tests
dotnet test --filter Category=Integration
```";
        }

        return commands;
    }

    private string GenerateArchitectureSection(InitializationContext context, string projectType, string architecturePattern)
    {
        return $@"### {architecturePattern} Architecture

{GenerateArchitectureGuidance(architecturePattern)}

### Project Structure
{GenerateProjectStructure(projectType, architecturePattern)}";
    }

    private string GenerateArchitectureGuidance(string architecturePattern)
    {
        return architecturePattern.ToLowerInvariant() switch
        {
            "clean" => @"This project follows Clean Architecture principles:
- **Domain Layer**: Core business logic and entities
- **Application Layer**: Use cases and application services
- **Infrastructure Layer**: Data access and external services
- **Presentation Layer**: Controllers, APIs, or UI components

Dependencies flow inward toward the domain, with the domain having no dependencies on external layers.",

            "onion" => @"This project follows Onion Architecture principles:
- **Core**: Domain models and interfaces
- **Services**: Application services and business logic
- **Infrastructure**: Data access and external integrations
- **Web/API**: Controllers and presentation logic

The architecture ensures that business logic is independent of external concerns.",

            "hexagonal" => @"This project follows Hexagonal (Ports and Adapters) Architecture:
- **Application Core**: Business logic and domain models
- **Ports**: Interfaces defining how the application interacts with the outside world
- **Adapters**: Implementations of ports for specific technologies
- **Configuration**: Wiring of ports and adapters

This allows the application to be equally driven by users, programs, automated tests, or batch scripts.",

            _ => @"This project follows Layered Architecture principles:
- **Presentation Layer**: UI components and controllers
- **Business Layer**: Core business logic and rules
- **Data Access Layer**: Database and external service interactions
- **Cross-Cutting Concerns**: Logging, security, and configuration"
        };
    }

    private string GenerateDevelopmentPatterns(InitializationContext context, string projectType, bool includeTdd)
    {
        var patterns = @"### Code Organization
- Use dependency injection for loose coupling
- Implement the Repository pattern for data access
- Apply the SOLID principles in design decisions
- Use async/await for I/O operations

### Error Handling
- Create custom exceptions for business logic errors
- Implement global exception handling
- Use structured logging for troubleshooting";

        if (includeTdd)
        {
            patterns += @"

### Test-Driven Development (TDD)
- Write tests before implementing functionality
- Follow the Red-Green-Refactor cycle
- Maintain high code coverage (minimum 80%)
- Use test doubles (mocks, stubs) for dependencies";
        }

        return patterns;
    }

    private string GenerateFileOrganization(InitializationContext context, string projectType, string architecturePattern)
    {
        return projectType.ToLowerInvariant() switch
        {
            "web" => @"├── Controllers/           # MVC controllers
├── Views/                # Razor views
├── Models/               # View models and DTOs
├── Services/             # Business logic services
├── Infrastructure/       # Data access and configuration
├── wwwroot/             # Static files (CSS, JS, images)
├── appsettings.json     # Configuration
├── Program.cs           # Application entry point
└── Startup.cs           # Service configuration",

            "api" => @"├── Controllers/          # API controllers
├── Models/              # DTOs and request/response models
├── Services/            # Business logic services
├── Infrastructure/      # Data access and external services
├── Middleware/          # Custom middleware
├── Configuration/       # Startup and service configuration
├── appsettings.json    # Application configuration
└── Program.cs          # Application entry point",

            "console" => @"├── Commands/            # Command implementations
├── Services/           # Business logic services
├── Infrastructure/     # Configuration and DI setup
├── Models/             # Data models and DTOs
├── appsettings.json   # Configuration settings
└── Program.cs         # Entry point and command setup",

            _ => @"├── Core/               # Domain models and business logic
├── Application/        # Use cases and application services
├── Infrastructure/     # Data access and external services
├── Presentation/       # Controllers or UI components
├── Tests/             # Unit and integration tests
├── Configuration/     # Startup and dependency injection
└── Program.cs        # Application entry point"
        };
    }

    private string GenerateConfigurationSection(InitializationContext context, string projectType)
    {
        return projectType.ToLowerInvariant() switch
        {
            "web" or "api" => @"The application uses ASP.NET Core configuration system with appsettings.json:

```json
{
  ""Logging"": {
    ""LogLevel"": {
      ""Default"": ""Information"",
      ""Microsoft.AspNetCore"": ""Warning""
    }
  },
  ""ConnectionStrings"": {
    ""DefaultConnection"": ""Server=localhost;Database={{ProjectName}};Trusted_Connection=true;""
  },
  ""ApplicationSettings"": {
    ""ApiVersion"": ""v1"",
    ""EnableSwagger"": true
  }
}
```

Environment-specific configuration files:
- `appsettings.Development.json` - Development settings
- `appsettings.Production.json` - Production settings",

            _ => @"The application uses .NET configuration system with appsettings.json:

```json
{
  ""Logging"": {
    ""LogLevel"": {
      ""Default"": ""Information""
    }
  },
  ""ApplicationSettings"": {
    ""Version"": ""1.0.0"",
    ""Environment"": ""Development""
  }
}
```

Configuration is loaded from:
- appsettings.json (base configuration)
- appsettings.{Environment}.json (environment-specific)
- Environment variables
- Command line arguments"
        };
    }

    private string GenerateDependencyList(InitializationContext context, string projectType)
    {
        return projectType.ToLowerInvariant() switch
        {
            "web" => @"- **Microsoft.AspNetCore.App** - ASP.NET Core framework
- **Microsoft.EntityFrameworkCore** - Entity Framework Core (if using)
- **Serilog.AspNetCore** - Structured logging
- **Swashbuckle.AspNetCore** - Swagger/OpenAPI documentation",

            "api" => @"- **Microsoft.AspNetCore.App** - ASP.NET Core Web API framework
- **Swashbuckle.AspNetCore** - Swagger/OpenAPI documentation
- **FluentValidation** - Input validation
- **AutoMapper** - Object-to-object mapping
- **Microsoft.EntityFrameworkCore** - Data access (if using)",

            "console" => @"- **Microsoft.Extensions.Hosting** - Generic host and dependency injection
- **Microsoft.Extensions.Configuration** - Configuration management
- **Serilog** - Structured logging
- **CommandLineParser** - Command line argument parsing (if needed)",

            _ => @"- **Microsoft.Extensions.DependencyInjection** - Dependency injection container
- **Microsoft.Extensions.Configuration** - Configuration management
- **Microsoft.Extensions.Logging** - Logging abstraction
- **Newtonsoft.Json** - JSON serialization"
        };
    }

    private string GenerateCodeOrganizationGuidance(string projectType)
    {
        return @"Each component follows single responsibility principle with clear separation of concerns. Use dependency injection for loose coupling and testability. Follow established naming conventions and folder structure.";
    }

    private string GenerateServiceImplementationGuidance(string projectType)
    {
        return @"Services should be stateless and use async/await patterns for I/O operations. Implement interfaces for better testability and follow the dependency inversion principle. Use scoped lifetime for services that maintain state per request.";
    }

    private string GenerateTddSection(string testFramework)
    {
        return $@"
### Test-Driven Development (TDD)

This project follows TDD practices using {testFramework}:

#### TDD Cycle
1. **Red**: Write a failing test that defines the desired functionality
2. **Green**: Write the minimum code needed to make the test pass
3. **Refactor**: Improve the code while keeping tests green

#### Testing Strategy
- **Unit Tests**: Test individual components in isolation
- **Integration Tests**: Test component interactions and external dependencies
- **End-to-End Tests**: Test complete user workflows

#### Test Organization
```
tests/
├── Unit/                 # Unit tests for individual components
├── Integration/          # Integration tests for component interactions
├── EndToEnd/            # End-to-end tests for complete workflows
└── Common/              # Shared test utilities and helpers
```

#### Testing Best Practices
- Use descriptive test names that explain the scenario
- Follow the Arrange-Act-Assert (AAA) pattern
- Keep tests independent and isolated
- Use test doubles (mocks, stubs) for external dependencies
- Maintain high code coverage (minimum 80%)";
    }

    private string GenerateDockerSection()
    {
        return @"
### Docker Development

```bash
# Build Docker image
docker build -t {{project_name}} .

# Run container locally
docker run -p 8080:8080 {{project_name}}

# Run with environment variables
docker run -e ASPNETCORE_ENVIRONMENT=Development -p 8080:8080 {{project_name}}

# Docker Compose for development
docker-compose up -d

# View logs
docker logs {{project_name}}

# Stop and remove containers
docker-compose down
```";
    }

    private string GenerateKubernetesSection()
    {
        return @"
### Kubernetes Deployment

```bash
# Deploy to Kubernetes
kubectl apply -f k8s/

# Check deployment status
kubectl get deployments
kubectl get pods
kubectl get services

# View logs
kubectl logs -l app={{project_name}}

# Port forward for local testing
kubectl port-forward service/{{project_name}} 8080:80

# Scale deployment
kubectl scale deployment {{project_name}} --replicas=3

# Update deployment
kubectl set image deployment/{{project_name}} {{project_name}}=new-image:tag
```";
    }

    private string GenerateAzureSection()
    {
        return @"
### Azure Deployment

```bash
# Login to Azure CLI
az login

# Create resource group
az group create --name {{project_name}}-rg --location eastus

# Deploy to Azure Container Instances
az container create --resource-group {{project_name}}-rg --name {{project_name}} --image {{project_name}}

# Deploy to Azure App Service
az webapp create --resource-group {{project_name}}-rg --plan {{project_name}}-plan --name {{project_name}}

# Deploy using Azure DevOps
# (Requires azure-pipelines.yml configuration)

# Monitor with Application Insights
az monitor app-insights component create --app {{project_name}} --location eastus --resource-group {{project_name}}-rg
```";
    }

    private string GenerateCiCdSection(InitializationContext context)
    {
        return @"
## Continuous Integration/Deployment

### GitHub Actions Workflow

The project includes automated CI/CD pipeline with GitHub Actions:

```yaml
# .github/workflows/ci-cd.yml
name: CI/CD Pipeline

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v3
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: 8.0.x
    - name: Restore dependencies
      run: dotnet restore
    - name: Build
      run: dotnet build --no-restore
    - name: Test
      run: dotnet test --no-build --verbosity normal
```

### Quality Gates
- All tests must pass
- Code coverage minimum 80%
- Security scan with no high vulnerabilities
- Code quality checks with SonarQube";
    }

    private string GenerateDeploymentCommands(InitializationContext context, bool includeDocker, bool includeK8s, bool includeAzure)
    {
        var commands = @"```bash
# Standard .NET deployment
dotnet publish --configuration Release --output ./publish

# Deploy with PKS CLI (if configured)
pks deploy --environment production
```";

        if (includeDocker)
        {
            commands += @"

### Docker Deployment
```bash
# Build and push to registry
docker build -t {{project_name}}:latest .
docker push {{project_name}}:latest
```";
        }

        if (includeK8s)
        {
            commands += @"

### Kubernetes Deployment
```bash
# Deploy to cluster
kubectl apply -f k8s/
kubectl rollout status deployment/{{project_name}}
```";
        }

        if (includeAzure)
        {
            commands += @"

### Azure Deployment
```bash
# Deploy to Azure App Service
az webapp deployment source config-zip --resource-group rg --name {{project_name}} --src deploy.zip
```";
        }

        return commands;
    }
}