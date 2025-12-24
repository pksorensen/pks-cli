# PKS CLI - Agentic Development Project Initialization

## Overview

PKS CLI has been refactored to focus on its **core purpose**: Setting up agentic development environments with devcontainer support. The `pks init` command now **discovers templates from NuGet** dynamically, supporting custom feeds and tags.

## Philosophy Change

### Before ❌
- Asked for "project type" (console, api, web, etc.)
- Mixed concerns: project scaffolding + devcontainer setup
- Hardcoded template list
- Competed with `dotnet new`

### After ✅
- Discovers **devcontainer templates** from NuGet
- Focused purpose: **agentic development environment setup**
- Dynamic template discovery
- Supports custom NuGet feeds
- Complementary to `dotnet new`

## User Flow

### Basic Usage

```bash
pks init MyProject
```

**What happens:**
1. Discovers available templates from NuGet (tag: `pks-templates`)
2. Shows interactive menu with discovered templates
3. User selects template (e.g., "Claude .NET 9 + Aspire")
4. Extracts devcontainer to project directory
5. Ready for development in VS Code!

### With Custom NuGet Feed

```bash
pks init MyProject --nuget-source https://my-company-nuget.com/v3/index.json
```

### With Custom Tag

```bash
pks init MyProject --tag my-company-templates
```

### Non-Interactive (CI/CD)

```bash
pks init MyProject --template pks-claude-dotnet9 --description "My project"
```

## Template Discovery

### How It Works

1. **Query NuGet**: Searches for packages with specified tag (default: `pks-templates`)
2. **Parse Metadata**: Extracts template information from package metadata
3. **Display Options**: Shows available templates with icons and descriptions
4. **Install Template**: Downloads and extracts selected NuGet package

### Template Package Requirements

For a NuGet package to be discovered as a PKS template:

1. **Must have tag**: `pks-templates` (or custom tag specified)
2. **Should include**: Devcontainer configuration files
3. **Package structure**:
   ```
   content/
   └── .devcontainer/
       ├── devcontainer.json
       ├── Dockerfile
       └── ... other devcontainer files
   ```

### Example Template Package (.csproj)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageType>Template</PackageType>
    <PackageId>MyCompany.Templates.DevContainer</PackageId>
    <PackageTags>pks-templates devcontainer docker</PackageTags>
    <Description>My custom devcontainer template</Description>
    <!-- ... other properties ... -->
  </PropertyGroup>

  <ItemGroup>
    <Content Include="content/**/*" Pack="true" PackagePath="content/" />
  </ItemGroup>
</Project>
```

## Available Options

| Option | Description | Example |
|--------|-------------|---------|
| `--template` | Template short name or package ID | `--template pks-claude-dotnet9` |
| `--nuget-source` | Custom NuGet feed URL | `--nuget-source https://my-feed.com/v3/index.json` |
| `--tag` | NuGet tag to filter templates | `--tag company-templates` |
| `--description` | Project description | `--description "My awesome project"` |
| `--force` | Overwrite existing directory | `--force` |
| `--agentic` | Enable agentic features | `--agentic` |
| `--mcp` | Enable MCP integration | `--mcp` |

## Built-in Templates

PKS CLI includes two built-in templates:

### 1. PKS Universal DevContainer
- **Package**: `PKS.Templates.DevContainer`
- **Short Name**: `pks-devcontainer`
- **Description**: Multi-language devcontainer (NET, Node.js, Python, Go)
- **Icon**: 🐳

### 2. PKS Claude .NET 9 + Aspire
- **Package**: `PKS.Templates.ClaudeDotNet9`
- **Short Name**: `pks-claude-dotnet9`
- **Description**: Claude-optimized .NET 9 with Aspire support
- **Features**:
  - .NET 9 SDK
  - .NET Aspire tooling
  - Claude Code integration
  - GitHub Copilot
  - Docker-in-Docker
  - PowerShell
- **Icon**: 🤖

## Creating Custom Templates

### 1. Create Template Structure

```bash
mkdir my-template/content/.devcontainer
cd my-template
```

### 2. Add Devcontainer Files

```json
// content/.devcontainer/devcontainer.json
{
  "name": "My Custom Template",
  "image": "mcr.microsoft.com/devcontainers/base:ubuntu",
  "features": {
    "ghcr.io/devcontainers/features/docker-in-docker:2": {}
  }
}
```

### 3. Create Package Project

```xml
<!-- MyTemplate.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <PackageType>Template</PackageType>
    <PackageId>MyCompany.Templates.CustomDev</PackageId>
    <PackageTags>pks-templates devcontainer custom</PackageTags>
    <Description>My custom development environment</Description>
    <IncludeContentInPack>true</IncludeContentInPack>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <ContentTargetFolders>content</ContentTargetFolders>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="content/**/*" Pack="true" PackagePath="content/" />
  </ItemGroup>
</Project>
```

### 4. Build and Publish

```bash
# Build package
dotnet pack --configuration Release

# Publish to NuGet
dotnet nuget push bin/Release/MyCompany.Templates.CustomDev.1.0.0.nupkg \
  --api-key YOUR_API_KEY \
  --source https://api.nuget.org/v3/index.json
```

### 5. Use Your Template

```bash
pks init MyProject --template custom-dev
# or
pks init MyProject  # Will appear in interactive menu!
```

## Integration with Existing Workflows

### With dotnet new

```bash
# 1. Create .NET project structure with dotnet new
dotnet new console -n MyApp
cd MyApp

# 2. Add PKS devcontainer configuration
pks init . --template pks-claude-dotnet9 --force
```

### With Existing Projects

```bash
cd existing-project
pks init . --template pks-devcontainer --force
```

## Custom NuGet Feeds

### Azure DevOps Artifacts

```bash
pks init MyProject \
  --nuget-source https://pkgs.dev.azure.com/myorg/_packaging/myfeed/nuget/v3/index.json \
  --tag myorg-templates
```

### GitHub Packages

```bash
pks init MyProject \
  --nuget-source https://nuget.pkg.github.com/myorg/index.json \
  --tag myorg-templates
```

### Private NuGet Server

```bash
pks init MyProject \
  --nuget-source https://nuget.company.com/v3/index.json \
  --tag company-templates
```

## Template Discovery Process

```
┌─────────────────────────┐
│ pks init MyProject      │
└──────────┬──────────────┘
           │
           ▼
┌─────────────────────────────────┐
│ Query NuGet with tag            │
│ (default: pks-templates)        │
└──────────┬──────────────────────┘
           │
           ▼
┌─────────────────────────────────┐
│ Parse Package Metadata          │
│ - Title, Description            │
│ - Tags, Classifications         │
│ - ShortNames                    │
└──────────┬──────────────────────┘
           │
           ▼
┌─────────────────────────────────┐
│ Display Interactive Menu        │
│ 🤖 Claude .NET 9 + Aspire       │
│ 🐳 PKS Universal DevContainer   │
│ 📦 Your Custom Template         │
└──────────┬──────────────────────┘
           │
           ▼
┌─────────────────────────────────┐
│ Download & Extract Template     │
│ from NuGet Package              │
└──────────┬──────────────────────┘
           │
           ▼
┌─────────────────────────────────┐
│ ✅ Project Ready!               │
│ Open in VS Code → Reopen in    │
│ Container                       │
└─────────────────────────────────┘
```

## Benefits

### For Users
- ✅ **Discoverable**: See all available templates
- ✅ **Extensible**: Add custom templates via NuGet
- ✅ **Version Control**: Templates are versioned packages
- ✅ **Custom Feeds**: Use company-internal templates
- ✅ **Consistent**: Same experience across teams

### For Template Authors
- ✅ **Standard Distribution**: Use NuGet (familiar ecosystem)
- ✅ **Versioning**: Semantic versioning built-in
- ✅ **Metadata**: Rich descriptions and tags
- ✅ **Updates**: Users can discover new versions
- ✅ **Private Distribution**: Support for private feeds

## Migration Guide

### For Users

**Old Way:**
```bash
pks init MyProject  # Got console template by default
```

**New Way:**
```bash
pks init MyProject  # Shows interactive menu of devcontainers
```

**For Scripts/CI:**
```bash
# Old: implicitly used console template
pks init MyProject

# New: explicitly specify template
pks init MyProject --template pks-claude-dotnet9
```

### For Template Developers

**Old:** Templates were hardcoded in PKS CLI

**New:** Create and distribute templates as NuGet packages

## Troubleshooting

### No Templates Found

```bash
$ pks init MyProject
⚠️  No templates found with tag 'pks-templates'.
```

**Solution:**
- Check NuGet connectivity
- Verify tag is correct
- Try with `--nuget-source` to specify feed explicitly

### Template Not Discovered

**Check Package Tags:**
```xml
<PackageTags>pks-templates devcontainer</PackageTags>
```

Must include `pks-templates` (or your custom tag).

### Custom Feed Not Working

```bash
# Test NuGet source
dotnet nuget list source

# Add source if needed
dotnet nuget add source https://your-feed.com/v3/index.json \
  --name YourFeed
```

## Future Enhancements

- Template search/filter by language, framework
- Template ratings and download counts
- Template previews before installation
- Template composition (combine multiple templates)
- Local template development mode
- Template validation and testing tools

## Related Commands

- `pks template list` - List available templates
- `pks template search <query>` - Search for templates
- `pks template install <id>` - Pre-install template
- `pks devcontainer init` - Initialize devcontainer in existing project

## Learn More

- [Creating Templates](./TEMPLATE-CREATION.md)
- [NuGet Package Documentation](https://learn.microsoft.com/nuget/)
- [Devcontainer Spec](https://containers.dev/)
- [PKS CLI Architecture](./ARCHITECTURE.md)
