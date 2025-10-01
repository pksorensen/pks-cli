# PKS CLI Template Creation Guide

This guide explains how to create new PKS CLI template packages for distribution.

## Quick Start

### Using the Slash Command (Recommended)

```bash
/dev:create-template templates/my-new-template
```

This automated command will:
1. Analyze your devcontainer files
2. Create all necessary template files
3. Parameterize values
4. Add to solution and build
5. Create NuGet package

### Manual Process

If you prefer manual control, follow these steps:

## Step 1: Create Template Folder Structure

```
templates/my-template/
├── .template.config/
│   ├── template.json          # Template configuration
│   └── icon.png               # Template icon
├── content/
│   └── .devcontainer/
│       ├── devcontainer.json  # Your devcontainer config
│       ├── Dockerfile         # Your Dockerfile
│       └── ...                # Other devcontainer files
├── PKS.Templates.MyTemplate.csproj  # NuGet package project
└── README.md                        # Template documentation
```

## Step 2: Create Template Configuration

### `.template.config/template.json`

```json
{
  "$schema": "http://json.schemastore.org/template",
  "author": "PKS CLI Team",
  "classifications": ["DevContainer", "Docker", "PKS CLI"],
  "name": "PKS My Template",
  "description": "Description of your template",
  "identity": "PKS.Templates.MyTemplate.Identity",
  "groupIdentity": "PKS.Templates.MyTemplate",
  "shortName": "pks-my-template",
  "tags": {
    "language": "JSON",
    "type": "item"
  },
  "sourceName": "PKSMyTemplate",
  "preferNameDirectory": true,
  "placeholderFilename": "template_placeholder",
  "symbols": {
    "ProjectName": {
      "type": "parameter",
      "datatype": "string",
      "defaultValue": "MyProject",
      "replaces": "PKSMyTemplate",
      "description": "The name of the project"
    },
    "Description": {
      "type": "parameter",
      "datatype": "string",
      "defaultValue": "A PKS CLI project",
      "replaces": "PROJECT_DESCRIPTION",
      "description": "Description of the project"
    }
  },
  "postActions": [
    {
      "condition": "OS != \"Windows\"",
      "description": "Set execute permissions for shell scripts",
      "actionId": "3A7C4B45-1F5D-4A30-960B-2576915CF100",
      "args": {
        "executable": "chmod",
        "args": "+x .devcontainer/*.sh"
      },
      "continueOnError": true
    }
  ]
}
```

### Key Components:

- **identity**: Unique identifier for your template
- **shortName**: What users type: `pks init --template <shortName>`
- **sourceName**: Base name that gets replaced by ProjectName
- **symbols**: Parameters that can be customized
- **replaces**: String in files that gets replaced with parameter value

## Step 3: Create NuGet Project File

### `PKS.Templates.MyTemplate.csproj`

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>

    <!-- Template Package Properties -->
    <PackageType>Template</PackageType>
    <PackageId>PKS.Templates.MyTemplate</PackageId>
    <PackageVersion>1.0.0</PackageVersion>
    <Title>PKS My Template</Title>
    <Authors>PKS CLI Team</Authors>
    <Owners>pksorensen</Owners>
    <PackageRequireLicenseAcceptance>false</PackageRequireLicenseAcceptance>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageProjectUrl>https://github.com/pksorensen/pks-cli</PackageProjectUrl>
    <Description>Your template description</Description>
    <PackageTags>devcontainer docker template pks-cli</PackageTags>
    <RepositoryType>git</RepositoryType>
    <RepositoryUrl>https://github.com/pksorensen/pks-cli.git</RepositoryUrl>
    <RepositoryBranch>main</RepositoryBranch>

    <!-- Template Specific Properties -->
    <IncludeContentInPack>true</IncludeContentInPack>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <ContentTargetFolders>content</ContentTargetFolders>
    <NoWarn>$(NoWarn);NU5128;NU5119</NoWarn>
    <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
    <NoDefaultExcludes>true</NoDefaultExcludes>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="content/**/*" Pack="true" PackagePath="content/" />
    <Content Include=".template.config/**/*" Pack="true" PackagePath="content/.template.config/" />
  </ItemGroup>

  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="content/README.md" />
  </ItemGroup>
</Project>
```

## Step 4: Parameterize Content Files

In your `content/.devcontainer/devcontainer.json`, replace hardcoded values:

**Before:**
```json
{
  "name": "My Specific Project",
  "remoteEnv": {
    "NODE_OPTIONS": "--max-old-space-size=4096"
  }
}
```

**After:**
```json
{
  "name": "PKSMyTemplate - PROJECT_DESCRIPTION",
  "remoteEnv": {
    "NODE_OPTIONS": "--max-old-space-size=NODE_MEMORY_LIMIT"
  }
}
```

Then define these in `template.json` symbols:
```json
"symbols": {
  "NodeMemoryLimit": {
    "type": "parameter",
    "datatype": "string",
    "defaultValue": "4096",
    "replaces": "NODE_MEMORY_LIMIT",
    "description": "Node.js memory limit in MB"
  }
}
```

## Step 5: Add to Solution

```bash
cd /workspace/pks-cli
dotnet sln add templates/my-template/PKS.Templates.MyTemplate.csproj
```

## Step 6: Build and Package

```bash
# Build
dotnet build templates/my-template/PKS.Templates.MyTemplate.csproj --configuration Release

# Create NuGet package
dotnet pack templates/my-template/PKS.Templates.MyTemplate.csproj --configuration Release
```

Package will be created in `/workspace/artifacts/`

## Step 7: Test

### Local Development Test
```bash
cd pks-cli/src
dotnet run -- init TestProject --devcontainer --template my-template
```

### After Installation
```bash
./install.sh
pks init MyProject --devcontainer --template my-template
```

## Common Parameters

Here are commonly used template parameters:

| Parameter | Type | Purpose | Example Replacement |
|-----------|------|---------|-------------------|
| ProjectName | string | Project identifier | `PKSMyTemplate` |
| Description | string | Project description | `PROJECT_DESCRIPTION` |
| TimeZone | string | Container timezone | `TIMEZONE_VALUE` |
| NodeMemoryLimit | string | Node memory limit | `NODE_MEMORY_LIMIT` |
| GitHubPATToken | string | GitHub token | `GITHUB_PAT_VALUE` |
| EnableFeature | bool | Feature flag | Conditional file inclusion |

## Parameter Types

### String Parameters
```json
"MyParam": {
  "type": "parameter",
  "datatype": "string",
  "defaultValue": "default-value",
  "replaces": "PLACEHOLDER",
  "description": "Parameter description"
}
```

### Boolean Parameters
```json
"EnableFeature": {
  "type": "parameter",
  "datatype": "bool",
  "defaultValue": "true",
  "description": "Enable feature X"
}
```

Use with conditional file inclusion:
```json
"sources": [
  {
    "modifiers": [
      {
        "condition": "(!EnableFeature)",
        "exclude": ["**/feature-files/**"]
      }
    ]
  }
]
```

## Best Practices

1. **Use descriptive parameter names**: `TimeZone` not `TZ`
2. **Provide good defaults**: Users should be able to use template without customization
3. **Document all parameters**: In README.md and template.json descriptions
4. **Test thoroughly**: Create test projects to verify all parameters work
5. **Version your templates**: Update PackageVersion when making changes
6. **Include comprehensive README**: Users need to know how to use your template

## Troubleshooting

### Template not found
- Verify template is in solution: `dotnet sln list`
- Check package was created: `ls artifacts/`
- Rebuild solution: `dotnet build`

### Parameters not replacing
- Check `replaces` value matches exactly in content files
- Verify file is included in `content/` directory
- Check for typos in placeholder strings

### Package warnings
- "Missing readme": Add README.md to template root
- "NU5128": Harmless warning about dependencies (suppressed in .csproj)

## Examples

See existing templates for reference:
- `templates/devcontainer/` - Multi-language devcontainer
- `templates/claude-dotnet-9/` - Claude-optimized .NET 9 with Aspire
- `templates/mcp/` - MCP configuration template

## Resources

- [.NET Template Documentation](https://learn.microsoft.com/dotnet/core/tools/custom-templates)
- [Template JSON Schema](http://json.schemastore.org/template)
- [VS Code DevContainer Spec](https://containers.dev/implementors/json_reference/)
- [PKS CLI Documentation](../README.md)
