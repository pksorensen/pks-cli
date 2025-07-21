# NuGet-Based Devcontainer Template System Architecture

## Executive Summary

This document outlines the architecture for a NuGet-based devcontainer template system that extends the PKS CLI with the ability to discover, install, and manage devcontainer templates from NuGet packages. The system will enable both consuming templates from NuGet and developing custom templates locally.

## Goals and Requirements

### Primary Goals
1. Enable discovery and installation of devcontainer templates from NuGet
2. Support local template development and testing
3. Provide an intuitive wizard UI/UX for template selection
4. Maintain backward compatibility with existing devcontainer infrastructure
5. Enable community contribution of templates via NuGet

### Key Requirements
- **Discovery**: Search and browse templates from NuGet repositories
- **Versioning**: Support multiple versions of templates with semantic versioning
- **Caching**: Local caching of downloaded templates for offline use
- **Validation**: Template validation before installation
- **Extensibility**: Allow template authors to define custom logic
- **Security**: Validate template packages and contents

## Architecture Overview

### Component Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                           PKS CLI                                    │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌───────────────────┐     ┌──────────────────────┐                │
│  │ Devcontainer      │     │ Template Package     │                │
│  │ Wizard Command    │────▶│ Manager              │                │
│  └───────────────────┘     └──────┬───────────────┘                │
│                                   │                                 │
│  ┌───────────────────┐           │                                 │
│  │ Template          │           ▼                                 │
│  │ Discovery Service │◀──────────┐                                 │
│  └──────┬────────────┘     ┌─────────────────────┐                │
│         │                  │ Template Repository  │                │
│         │                  │ Service              │                │
│         ▼                  └─────────┬───────────┘                │
│  ┌───────────────────┐              │                             │
│  │ Template Cache    │              │                             │
│  │ Manager           │              ▼                             │
│  └───────────────────┘     ┌─────────────────────┐                │
│                           │ Template Validator   │                │
│                           └─────────────────────┘                │
│                                                                      │
└──────────────────────┬──────────────────────────────────────────────┘
                       │
                       ▼
              ┌────────────────┐
              │ NuGet Gallery  │
              └────────────────┘
```

### Core Components

#### 1. Template Package Manager (`ITemplatePackageManager`)
Responsible for managing the lifecycle of template packages.

```csharp
public interface ITemplatePackageManager
{
    Task<TemplatePackage> InstallPackageAsync(string packageId, string version = null);
    Task<bool> UninstallPackageAsync(string packageId);
    Task<List<TemplatePackage>> GetInstalledPackagesAsync();
    Task<TemplatePackage> UpdatePackageAsync(string packageId, string version);
    Task<bool> ValidatePackageAsync(string packagePath);
}
```

#### 2. Template Discovery Service (`ITemplateDiscoveryService`)
Handles searching and browsing templates from NuGet.

```csharp
public interface ITemplateDiscoveryService
{
    Task<List<TemplateMetadata>> SearchTemplatesAsync(string query, int skip = 0, int take = 20);
    Task<TemplateMetadata> GetTemplateMetadataAsync(string packageId);
    Task<List<TemplateCategory>> GetCategoriesAsync();
    Task<List<TemplateMetadata>> GetPopularTemplatesAsync(int count = 10);
    Task<List<string>> GetAvailableVersionsAsync(string packageId);
}
```

#### 3. Template Repository Service (`ITemplateRepositoryService`)
Manages local template storage and retrieval.

```csharp
public interface ITemplateRepositoryService
{
    Task<DevcontainerTemplate> GetTemplateAsync(string templateId, string version = null);
    Task<List<DevcontainerTemplate>> GetLocalTemplatesAsync();
    Task<bool> RegisterLocalTemplateAsync(string path);
    Task<bool> UnregisterTemplateAsync(string templateId);
    string GetTemplateStoragePath();
}
```

#### 4. Template Cache Manager (`ITemplateCacheManager`)
Handles caching of downloaded templates.

```csharp
public interface ITemplateCacheManager
{
    Task<bool> IsCachedAsync(string packageId, string version);
    Task<string> GetCachedPathAsync(string packageId, string version);
    Task ClearCacheAsync();
    Task<long> GetCacheSizeAsync();
    Task PruneCacheAsync(TimeSpan maxAge);
}
```

#### 5. Template Validator (`ITemplateValidator`)
Validates template packages and contents.

```csharp
public interface ITemplateValidator
{
    Task<ValidationResult> ValidatePackageStructureAsync(string packagePath);
    Task<ValidationResult> ValidateTemplateManifestAsync(TemplateManifest manifest);
    Task<ValidationResult> ValidateTemplateContentAsync(string templatePath);
    Task<ValidationResult> ValidateSecurityAsync(string packagePath);
}
```

## Template Package Structure

### NuGet Package Layout

```
pks-devcontainer-template-{name}/
├── template.json              # Template manifest
├── devcontainer/             # Template files
│   ├── devcontainer.json     # Base devcontainer configuration
│   ├── Dockerfile            # Optional Dockerfile
│   └── features/             # Optional features
├── wizard/                   # Wizard customization
│   ├── prompts.json         # Custom prompts for wizard
│   └── validation.js        # Custom validation logic
├── hooks/                    # Template hooks
│   ├── pre-install.ps1      # Pre-installation script
│   └── post-install.ps1     # Post-installation script
├── docs/                     # Documentation
│   ├── README.md            # Template documentation
│   └── examples/            # Example configurations
└── tests/                    # Template tests
    └── validate.ps1          # Validation tests
```

### Template Manifest Schema (`template.json`)

```json
{
  "$schema": "https://pks-cli.dev/schemas/devcontainer-template/v1.0.json",
  "id": "dotnet-microservices-advanced",
  "name": ".NET Microservices Advanced",
  "description": "Advanced microservices template with service mesh and observability",
  "version": "1.0.0",
  "author": {
    "name": "PKS Team",
    "email": "templates@pks-cli.dev"
  },
  "category": "microservices",
  "tags": ["dotnet", "microservices", "kubernetes", "service-mesh"],
  "minCliVersion": "1.0.0",
  "baseImage": "mcr.microsoft.com/dotnet/sdk:8.0",
  "requiredFeatures": [
    "ghcr.io/devcontainers/features/dotnet:2",
    "ghcr.io/devcontainers/features/docker-in-docker:2",
    "ghcr.io/devcontainers/features/kubectl-helm-minikube:1"
  ],
  "parameters": [
    {
      "id": "serviceMesh",
      "name": "Service Mesh",
      "description": "Choose service mesh technology",
      "type": "choice",
      "choices": ["none", "istio", "linkerd", "consul"],
      "default": "none"
    },
    {
      "id": "observability",
      "name": "Observability Stack",
      "description": "Include observability tools",
      "type": "boolean",
      "default": true
    }
  ],
  "files": {
    "devcontainer.json": "devcontainer/devcontainer.json",
    "Dockerfile": {
      "source": "devcontainer/Dockerfile",
      "condition": "parameters.customDockerfile"
    },
    "docker-compose.yml": {
      "source": "devcontainer/docker-compose.yml",
      "condition": "parameters.useCompose"
    }
  },
  "postInstall": {
    "commands": [
      "dotnet restore",
      "dotnet tool restore"
    ],
    "scripts": ["hooks/configure-service-mesh.sh"]
  }
}
```

## Wizard UI/UX Flow

### Template Selection Wizard Flow

```
1. Welcome Screen
   └─> 2. Template Source Selection
       ├─> Local Templates
       │   └─> 5. Template Details
       └─> NuGet Templates
           └─> 3. Search/Browse
               └─> 4. Template Selection
                   └─> 5. Template Details
                       └─> 6. Parameter Configuration
                           └─> 7. Review & Confirm
                               └─> 8. Installation Progress
                                   └─> 9. Success/Next Steps
```

### Wizard Command Implementation

```csharp
public class DevcontainerTemplateWizardCommand : AsyncCommand<DevcontainerTemplateWizardSettings>
{
    private readonly ITemplateDiscoveryService _discoveryService;
    private readonly ITemplatePackageManager _packageManager;
    private readonly IAnsiConsole _console;

    public override async Task<int> ExecuteAsync(CommandContext context, DevcontainerTemplateWizardSettings settings)
    {
        // Step 1: Welcome
        ShowWelcomeBanner();

        // Step 2: Choose source
        var source = await PromptTemplateSourceAsync();

        // Step 3-4: Browse and select template
        var template = source == TemplateSource.NuGet
            ? await BrowseNuGetTemplatesAsync()
            : await BrowseLocalTemplatesAsync();

        // Step 5: Show template details
        await ShowTemplateDetailsAsync(template);

        // Step 6: Configure parameters
        var parameters = await ConfigureTemplateParametersAsync(template);

        // Step 7: Review configuration
        if (!await ConfirmConfigurationAsync(template, parameters))
            return 0;

        // Step 8-9: Install and show results
        await InstallTemplateAsync(template, parameters);
        
        return 0;
    }
}
```

## Integration with Existing Infrastructure

### Extended IDevcontainerTemplateService

```csharp
public interface IDevcontainerTemplateService
{
    // Existing methods...
    
    // New NuGet-specific methods
    Task<List<DevcontainerTemplate>> GetNuGetTemplatesAsync(string query = null);
    Task<DevcontainerTemplate> InstallNuGetTemplateAsync(string packageId, string version = null);
    Task<bool> IsNuGetTemplateInstalledAsync(string packageId);
    Task<List<DevcontainerTemplate>> GetInstalledNuGetTemplatesAsync();
    Task RefreshNuGetCacheAsync();
}
```

### Template Loading Priority

1. User-specified local templates
2. Installed NuGet templates
3. Built-in templates

## Local Template Development

### Project Structure for Template Development

```
my-devcontainer-template/
├── src/
│   ├── template.json
│   ├── devcontainer/
│   │   └── devcontainer.json
│   └── wizard/
│       └── prompts.json
├── tests/
│   └── template.tests.ps1
├── examples/
│   └── basic-usage/
├── .pks/
│   └── template-dev.json    # Development configuration
├── pack.ps1                  # Build script
└── README.md
```

### Development Commands

```bash
# Initialize new template project
pks devcontainer template init my-template

# Test template locally
pks devcontainer template test ./my-template

# Validate template
pks devcontainer template validate ./my-template

# Pack template for NuGet
pks devcontainer template pack ./my-template --output ./dist

# Publish to NuGet
pks devcontainer template publish ./dist/my-template.1.0.0.nupkg
```

### Template Development Service

```csharp
public interface ITemplateDevService
{
    Task<string> InitializeTemplateProjectAsync(string name, string outputPath);
    Task<ValidationResult> ValidateTemplateProjectAsync(string projectPath);
    Task<TestResult> TestTemplateAsync(string projectPath, Dictionary<string, object> parameters);
    Task<string> PackTemplateAsync(string projectPath, string outputPath);
    Task<bool> PublishTemplateAsync(string packagePath, string apiKey);
}
```

## Security Considerations

### Template Security Model

1. **Package Signing**: Require NuGet packages to be signed
2. **Content Validation**: Scan templates for malicious content
3. **Script Sandboxing**: Execute hooks in restricted environment
4. **Permission Model**: Define what templates can modify
5. **Audit Trail**: Log all template installations and executions

### Security Service

```csharp
public interface ITemplateSecurityService
{
    Task<bool> VerifyPackageSignatureAsync(string packagePath);
    Task<SecurityScanResult> ScanTemplateContentAsync(string templatePath);
    Task<bool> ValidatePermissionsAsync(TemplateManifest manifest);
    Task<ExecutionContext> CreateSandboxedContextAsync();
    Task AuditTemplateActionAsync(string action, string templateId, Dictionary<string, object> metadata);
}
```

## Caching and Performance

### Cache Strategy

1. **Package Cache**: Store downloaded NuGet packages
2. **Metadata Cache**: Cache template metadata for quick browsing
3. **Search Cache**: Cache search results with TTL
4. **Extracted Cache**: Keep extracted templates for reuse

### Cache Configuration

```json
{
  "cache": {
    "enabled": true,
    "directory": "~/.pks/cache/devcontainer-templates",
    "maxSize": "500MB",
    "ttl": {
      "packages": "30d",
      "metadata": "1d",
      "search": "1h"
    },
    "pruning": {
      "enabled": true,
      "schedule": "weekly",
      "keepRecent": 10
    }
  }
}
```

## Template Categories and Discovery

### Standard Categories

- **runtime**: Basic language runtimes
- **web**: Web development templates
- **api**: API development templates
- **microservices**: Microservice architectures
- **data**: Data-focused development
- **ml**: Machine learning environments
- **iot**: IoT development
- **mobile**: Mobile app development
- **desktop**: Desktop application development
- **infrastructure**: Infrastructure as Code

### Discovery Features

1. **Search**: Full-text search across name, description, tags
2. **Filtering**: By category, author, features, ratings
3. **Sorting**: By downloads, ratings, updated date
4. **Recommendations**: Based on project type and history

## Extensibility Model

### Template Extensions

Templates can provide extensions for:

1. **Custom Prompts**: Define wizard prompts
2. **Validation Rules**: Custom parameter validation
3. **Generation Logic**: Custom file generation
4. **Post-Processing**: Transform generated files
5. **Integration Hooks**: Integrate with external tools

### Extension Interface

```csharp
public interface ITemplateExtension
{
    string Id { get; }
    string TemplateId { get; }
    
    Task<List<WizardPrompt>> GetCustomPromptsAsync();
    Task<ValidationResult> ValidateParametersAsync(Dictionary<string, object> parameters);
    Task<List<GeneratedFile>> GenerateFilesAsync(TemplateContext context);
    Task PostProcessAsync(List<string> generatedFiles, TemplateContext context);
}
```

## Migration Status

### ✅ Completed
- **Core Template Structure**: Universal devcontainer template migrated to `/templates/devcontainer/`
- **Template Configuration**: Proper `.template.config/template.json` configuration file created
- **Package Structure**: NuGet package structure with proper csproj configuration
- **Build Cleanup**: Removed legacy pack.sh scripts and old .nuspec files
- **Documentation**: Updated README files for both template and content

### 🚧 In Progress
- NuGet Package Manager integration with CLI commands
- Template Discovery Service implementation
- Enhanced wizard UI integration

### 📋 Planned
- **Phase 1: Enhanced CLI Integration** (1 week)
  - Complete NuGet Template Discovery Service
  - Integrate Template Package Manager with wizard commands
  - Add template validation and testing
  
- **Phase 2: Advanced Features** (1 week)  
  - Implement security scanning
  - Add package signing verification
  - Performance optimization and caching improvements

## Testing Strategy

### Unit Tests
- Template discovery and search
- Package installation and management
- Cache operations
- Validation logic

### Integration Tests
- NuGet API integration
- Full wizard flow
- Template installation end-to-end
- Cache persistence

### Template Tests
- Validate all built-in templates
- Test template parameters
- Verify generated configurations
- Docker build validation

## Conclusion

This architecture provides a robust, extensible system for managing devcontainer templates through NuGet while maintaining compatibility with the existing PKS CLI infrastructure. The design emphasizes security, performance, and developer experience, enabling both template consumers and creators to work efficiently.

The phased implementation approach allows for iterative development and testing, ensuring each component is thoroughly validated before moving to the next phase.