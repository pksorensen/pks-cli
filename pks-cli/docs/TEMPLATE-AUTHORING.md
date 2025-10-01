# PKS CLI DevContainer Template Authoring Guide

This guide explains how to create custom devcontainer templates that work with the PKS CLI's `devcontainer wizard` command.

## Overview

PKS CLI supports discovering and using devcontainer templates from:
- **NuGet packages** (both remote feeds and local folders)
- **Built-in templates** (included with PKS CLI)

## Creating a DevContainer Template Package

### 1. Project Structure

Your template should be structured as a .NET project that produces a NuGet package:

```
MyDevContainerTemplate/
├── MyDevContainerTemplate.csproj    # NuGet package configuration
├── .template.config/
│   ├── template.json                # Template metadata and parameters
│   └── icon.png                     # Optional template icon
├── content/                         # Template content files
│   ├── .devcontainer/
│   │   ├── devcontainer.json
│   │   ├── Dockerfile               # Optional
│   │   └── docker-compose.yml       # Optional
│   └── README.md
└── README.md                        # Package documentation
```

### 2. NuGet Package Configuration (.csproj)

Your `.csproj` file must include specific properties for PKS CLI discovery:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    
    <!-- Template Package Properties -->
    <PackageType>Template</PackageType>
    <PackageId>YourCompany.Templates.DevContainer</PackageId>
    <PackageVersion>1.0.0</PackageVersion>
    <Title>Your DevContainer Template</Title>
    <Authors>Your Company</Authors>
    <Description>Description of your devcontainer template</Description>
    
    <!-- REQUIRED: Tags for PKS CLI Discovery -->
    <PackageTags>devcontainer pks-devcontainers category:general</PackageTags>
    
    <!-- Template Specific Properties -->
    <IncludeContentInPack>true</IncludeContentInPack>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <ContentTargetFolders>content</ContentTargetFolders>
    <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
  </PropertyGroup>

  <!-- Include template content -->
  <ItemGroup>
    <Content Include="content/**/*" Pack="true" PackagePath="content/" />
    <Content Include=".template.config/**/*" Pack="true" PackagePath="content/.template.config/" />
  </ItemGroup>
</Project>
```

### 3. Required NuGet Tags

For PKS CLI to discover your template, your `<PackageTags>` **must** include:

| Tag | Required | Purpose |
|-----|----------|---------|
| `devcontainer` | Yes | Identifies this as a devcontainer template |
| `pks-devcontainers` | Yes | Primary discovery tag for PKS CLI |
| `category:CATEGORY` | Recommended | Controls which category the template appears in |

#### Available Categories

| Category | Description | Examples |
|----------|-------------|----------|
| `category:general` | General purpose templates | Universal, starter templates |
| `category:runtime` | Language/runtime specific | .NET, Node.js, Python, Go |
| `category:web` | Web development | React, Angular, Vue, ASP.NET |
| `category:microservices` | Microservice architectures | Docker Compose, Kubernetes |

If no `category:` tag is specified, the template will appear in the **"general"** category.

#### Example Tag Combinations

```xml
<!-- General purpose template -->
<PackageTags>devcontainer pks-devcontainers category:general docker</PackageTags>

<!-- .NET specific template -->
<PackageTags>devcontainer pks-devcontainers category:runtime dotnet csharp</PackageTags>

<!-- React web app template -->
<PackageTags>devcontainer pks-devcontainers category:web react nodejs typescript</PackageTags>

<!-- Microservices template -->
<PackageTags>devcontainer pks-devcontainers category:microservices docker-compose kubernetes</PackageTags>
```

### 4. Template Configuration (.template.config/template.json)

The `template.json` file defines template metadata, parameters, and conditional logic:

```json
{
  "$schema": "http://json.schemastore.org/template",
  "author": "Your Company",
  "classifications": ["DevContainer", "Docker", "Your Technology"],
  "name": "Your Template Name",
  "description": "Description shown in the PKS CLI wizard",
  "identity": "YourCompany.Templates.DevContainer.YourTemplate",
  "groupIdentity": "YourCompany.Templates.DevContainer",
  "shortName": "your-template",
  "tags": {
    "language": "JSON",
    "type": "item"
  },
  "sourceName": "TemplateName",
  "preferNameDirectory": true,
  "symbols": {
    "ProjectName": {
      "type": "parameter",
      "datatype": "string",
      "defaultValue": "MyProject",
      "replaces": "TemplateName",
      "description": "The name of the project"
    },
    "Description": {
      "type": "parameter",
      "datatype": "string", 
      "defaultValue": "A project with DevContainer support",
      "replaces": "PROJECT_DESCRIPTION",
      "description": "Project description"
    },
    "EnableFeature": {
      "type": "parameter",
      "datatype": "bool",
      "defaultValue": "true",
      "description": "Enable optional feature"
    }
  },
  "sources": [
    {
      "modifiers": [
        {
          "condition": "(!EnableFeature)",
          "exclude": [
            "**/optional-feature/**"
          ]
        }
      ]
    }
  ]
}
```

### 5. Template Content Structure

#### Basic DevContainer Configuration

Your `content/.devcontainer/devcontainer.json` should follow standard devcontainer specifications:

```json
{
  "name": "PROJECT_DESCRIPTION",
  "image": "mcr.microsoft.com/vscode/devcontainers/base:ubuntu",
  "features": {
    "ghcr.io/devcontainers/features/docker-in-docker:2": {}
  },
  "customizations": {
    "vscode": {
      "extensions": [
        "ms-vscode.vscode-json"
      ],
      "settings": {
        "terminal.integrated.defaultProfile.linux": "bash"
      }
    }
  },
  "forwardPorts": [3000],
  "postCreateCommand": "echo 'Setup complete!'",
  "remoteUser": "vscode"
}
```

#### Using Template Parameters

Use replacement tokens in your content files:

```json
{
  "name": "PROJECT_DESCRIPTION",
  "image": "mcr.microsoft.com/vscode/devcontainers/base:ubuntu"
}
```

These will be replaced based on the `symbols` defined in `template.json`.

## Building and Testing

### 1. Build the Package

```bash
cd YourTemplate
dotnet pack --configuration Release
```

### 2. Test Locally

```bash
# Test with local folder source
pks devcontainer wizard --from-templates --sources ./bin/Release --debug

# Or copy to a test directory
cp ./bin/Release/*.nupkg /path/to/test-artifacts/
pks devcontainer wizard --from-templates --sources /path/to/test-artifacts --debug
```

### 3. Debug Template Discovery

Use the `--debug` flag to see detailed information about template discovery:

```bash
pks devcontainer wizard --from-templates --sources ./artifacts --debug
```

The debug output will show:
- Which sources are being searched
- Which .nupkg files are found
- Package metadata extraction
- Tag matching results

## Distribution

### Publishing to NuGet.org

```bash
dotnet nuget push ./bin/Release/YourTemplate.1.0.0.nupkg --api-key YOUR_API_KEY --source https://api.nuget.org/v3/index.json
```

### Using Private NuGet Feeds

```bash
# Add your private feed
dotnet nuget add source https://your-feed.com/v3/index.json --name YourFeed

# Use with PKS CLI
pks devcontainer wizard --from-templates --sources https://your-feed.com/v3/index.json
```

## Template Environment Variables

### Overview

Templates can define required environment variables that PKS CLI will prompt users to configure during template setup. This is useful for templates that need credentials, API tokens, or other configuration values.

### Defining Required Environment Variables

Add required environment variables to your NuGet package metadata using the `requiredEnv:` prefix:

#### In .csproj File

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Standard package properties -->
    <PackageId>YourCompany.Templates.DevContainer.Github</PackageId>
    <PackageTags>devcontainer pks-devcontainers category:general</PackageTags>
    
    <!-- Define required environment variables -->
    <requiredEnv_GITHUB_TOKEN>GitHub personal access token for repository access</requiredEnv_GITHUB_TOKEN>
    <requiredEnv_API_KEY>API key for external service integration</requiredEnv_API_KEY>
    <requiredEnv_DATABASE_URL>Database connection string</requiredEnv_DATABASE_URL>
  </PropertyGroup>
</Project>
```

#### Alternative: Using PackageMetadata

```xml
<ItemGroup>
  <PackageMetadata Include="requiredEnv:GITHUB_TOKEN" Value="GitHub personal access token for repository access" />
  <PackageMetadata Include="requiredEnv:API_KEY" Value="API key for external service integration" />
  <PackageMetadata Include="requiredEnv:DATABASE_URL" Value="Database connection string" />
</ItemGroup>
```

### Environment Variable Naming Convention

| Variable Type | Example | Description |
|---------------|---------|-------------|
| **Tokens/Secrets** | `GITHUB_TOKEN`, `API_KEY`, `SECRET_KEY` | Will be prompted with secure input (masked) |
| **URLs/Endpoints** | `DATABASE_URL`, `API_ENDPOINT` | Standard text input |
| **Configuration** | `LOG_LEVEL`, `ENVIRONMENT` | Standard text input |

### Secure Input Detection

PKS CLI automatically detects sensitive environment variables and uses secure input (masked) for:
- Variables containing `TOKEN`, `SECRET`, `KEY`, `PASSWORD`, `PASS`
- Variables ending with `_TOKEN`, `_SECRET`, `_KEY`, `_PASSWORD`

### User Experience

When a template with required environment variables is selected:

1. **Environment Variable Prompt**: Users are prompted for each required variable
2. **Descriptions**: The description from metadata is shown as help text
3. **Secure Input**: Sensitive values are masked during input
4. **Validation**: Users can see configured variables (with sensitive values masked)

#### Example User Flow

```bash
$ pks devcontainer wizard --from-templates --sources ./artifacts

✓ Found 1 templates from NuGet packages

Select a template:
  GitHub Development Environment (YourCompany.Templates.DevContainer.Github)

✓ Selected template: GitHub Development Environment

Configure Environment Variables:
? GITHUB_TOKEN (GitHub personal access token for repository access): ****
? API_KEY (API key for external service integration): ****
? DATABASE_URL (Database connection string): postgresql://localhost:5432/mydb

Environment Variables:
┌─────────────────┬─────────────────────────────────────┐
│ Variable        │ Value                               │
├─────────────────┼─────────────────────────────────────┤
│ GITHUB_TOKEN    │ ****                                │
│ API_KEY         │ ****                                │
│ DATABASE_URL    │ postgresql://localhost:5432/mydb    │
└─────────────────┴─────────────────────────────────────┘

✓ DevContainer created successfully!
```

### Using Environment Variables in Templates

#### In devcontainer.json

```json
{
  "name": "PROJECT_DESCRIPTION",
  "image": "mcr.microsoft.com/vscode/devcontainers/base:ubuntu",
  "remoteEnv": {
    "GITHUB_TOKEN": "${localEnv:GITHUB_TOKEN}",
    "API_KEY": "${localEnv:API_KEY}",
    "DATABASE_URL": "${localEnv:DATABASE_URL}"
  },
  "postCreateCommand": "echo 'Environment configured with ${localEnv:GITHUB_TOKEN:+GitHub token}'"
}
```

#### In Dockerfile

```dockerfile
FROM mcr.microsoft.com/vscode/devcontainers/base:ubuntu

# Accept build args from environment variables
ARG GITHUB_TOKEN
ARG API_KEY

# Configure git with token (if provided)
RUN if [ -n "$GITHUB_TOKEN" ]; then \
    git config --global url."https://${GITHUB_TOKEN}@github.com/".insteadOf "https://github.com/"; \
    fi
```

### Best Practices

1. **Use Clear Descriptions**: Provide helpful descriptions that explain what the variable is used for
2. **Follow Naming Conventions**: Use standard environment variable naming (UPPER_CASE with underscores)
3. **Security First**: Ensure sensitive variables are detected as secure (contain TOKEN, SECRET, KEY, etc.)
4. **Provide Examples**: Include example values in descriptions when appropriate
5. **Document Usage**: Explain in your template's README how the environment variables are used

### Local Folder Distribution

For development or enterprise scenarios:

```bash
# Copy packages to a shared folder
cp *.nupkg \\shared\devcontainer-templates\

# Use the shared folder
pks devcontainer wizard --from-templates --sources \\shared\devcontainer-templates\
```

## Troubleshooting

### Template Not Found

1. **Verify required tags**:
   ```xml
   <PackageTags>devcontainer pks-devcontainers</PackageTags>
   ```

2. **Check package structure**:
   ```bash
   unzip -l YourTemplate.1.0.0.nupkg | grep -E "(nuspec|template.json)"
   ```

3. **Use debug mode**:
   ```bash
   pks devcontainer wizard --from-templates --sources ./artifacts --debug
   ```

### Template in Wrong Category

Add or modify the category tag:
```xml
<PackageTags>devcontainer pks-devcontainers category:runtime</PackageTags>
```

### Local vs Remote Sources

| Source Type | Example | Search Method |
|-------------|---------|---------------|
| **Local Folder** | `C:\artifacts`, `/home/user/packages` | Direct .nupkg file scanning |
| **Remote Feed** | `https://api.nuget.org/v3/index.json` | NuGet V3 Search API |

**Local folders** are automatically detected and use direct file scanning, while **remote feeds** use the NuGet search API.

## Example Templates

See the PKS CLI repository for complete examples:
- `templates/devcontainer/` - Universal DevContainer template
- `templates/` - Other template types

## Best Practices

1. **Always include both required tags**: `devcontainer` and `pks-devcontainers`
2. **Use descriptive category tags**: `category:runtime`, `category:web`, etc.
3. **Provide clear descriptions** in both `.csproj` and `template.json`
4. **Test with both local and remote sources** before publishing
5. **Use semantic versioning** for your package versions
6. **Include comprehensive documentation** in your template's README.md
7. **Use conditional logic** in `template.json` for optional features
8. **Follow devcontainer specifications** for maximum compatibility

## Advanced Features

### Conditional Content

Use the `sources.modifiers` section to include/exclude files based on parameters:

```json
{
  "sources": [
    {
      "modifiers": [
        {
          "condition": "(Framework == 'net8.0')",
          "include": ["**/net8/**"]
        },
        {
          "condition": "(Framework != 'net8.0')",
          "exclude": ["**/net8/**"]
        }
      ]
    }
  ]
}
```

### Post-Actions

Define actions to run after template instantiation:

```json
{
  "postActions": [
    {
      "condition": "OS == 'Windows'",
      "description": "Set execute permissions",
      "actionId": "3A7C4B45-1F5D-4A30-960B-2576915CF100",
      "args": {
        "executable": "chmod",
        "args": "+x scripts/setup.sh"
      }
    }
  ]
}
```

### Multiple Template Variants

Use `groupIdentity` to group related templates:

```json
{
  "identity": "YourCompany.Templates.DevContainer.DotNet.WebApi",
  "groupIdentity": "YourCompany.Templates.DevContainer.DotNet",
  "shortName": "dotnet-webapi"
}
```

## Support

For questions or issues with template authoring:
1. Check the PKS CLI documentation
2. Review existing templates in the PKS CLI repository
3. File issues on the PKS CLI GitHub repository
4. Use `--debug` mode for troubleshooting template discovery