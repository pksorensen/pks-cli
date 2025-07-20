# DevContainer Templates in PKS CLI

Quick reference guide for using and creating devcontainer templates with PKS CLI.

## Using Templates

### Discovery from NuGet Sources

```bash
# Discover from NuGet.org (default)
pks devcontainer wizard --from-templates

# Use custom NuGet feed
pks devcontainer wizard --from-templates --sources https://your-feed.com/v3/index.json

# Use local folder with .nupkg files
pks devcontainer wizard --from-templates --sources ./artifacts

# Multiple sources
pks devcontainer wizard --from-templates --sources ./local,https://api.nuget.org/v3/index.json

# Debug template discovery
pks devcontainer wizard --from-templates --sources ./artifacts --debug
```

### Template Categories

Templates are organized into categories based on their `category:` tag:

| Category | Description | Example Templates |
|----------|-------------|-------------------|
| **general** | General purpose, starter templates | Universal DevContainer, Basic setup |
| **runtime** | Language/runtime specific | .NET, Node.js, Python, Go, Java |
| **web** | Web development frameworks | React, Angular, Vue, ASP.NET Core |
| **microservices** | Microservice architectures | Docker Compose, Kubernetes, Service mesh |

## Creating Templates

### Required NuGet Tags

Your template package **must** include these tags in `<PackageTags>`:

```xml
<PackageTags>devcontainer pks-devcontainers category:general</PackageTags>
```

| Tag | Required | Purpose |
|-----|----------|---------|
| `devcontainer` | ✅ **Yes** | Identifies as devcontainer template |
| `pks-devcontainers` | ✅ **Yes** | Primary PKS CLI discovery tag |
| `category:CATEGORY` | Recommended | Controls template category placement |

### Quick Start Template

Minimum viable template structure:

```
MyTemplate/
├── MyTemplate.csproj           # Package configuration
├── .template.config/
│   └── template.json          # Template metadata
└── content/
    └── .devcontainer/
        └── devcontainer.json  # DevContainer config
```

#### MyTemplate.csproj
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <PackageType>Template</PackageType>
    <PackageId>MyCompany.Templates.DevContainer</PackageId>
    <PackageVersion>1.0.0</PackageVersion>
    <Title>My DevContainer Template</Title>
    <Description>Custom devcontainer template</Description>
    <PackageTags>devcontainer pks-devcontainers category:general</PackageTags>
    <IncludeContentInPack>true</IncludeContentInPack>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <ContentTargetFolders>content</ContentTargetFolders>
  </PropertyGroup>
  <ItemGroup>
    <Content Include="content/**/*" Pack="true" PackagePath="content/" />
    <Content Include=".template.config/**/*" Pack="true" PackagePath="content/.template.config/" />
  </ItemGroup>
</Project>
```

#### .template.config/template.json
```json
{
  "$schema": "http://json.schemastore.org/template",
  "author": "Your Company",
  "classifications": ["DevContainer", "Docker"],
  "name": "My DevContainer Template",
  "description": "Custom devcontainer for my projects",
  "identity": "MyCompany.Templates.DevContainer.Custom",
  "shortName": "my-devcontainer",
  "tags": {
    "language": "JSON",
    "type": "item"
  }
}
```

### Environment Variables Support

Templates can define required environment variables that PKS CLI will prompt users to configure:

```xml
<!-- In .csproj file -->
<PropertyGroup>
  <requiredEnv_GITHUB_TOKEN>GitHub personal access token for repository access</requiredEnv_GITHUB_TOKEN>
  <requiredEnv_API_KEY>API key for external service integration</requiredEnv_API_KEY>
</PropertyGroup>
```

When users select your template, PKS CLI will automatically prompt for these variables with secure input for tokens/secrets.

### Build and Test

```bash
# Build package
dotnet pack --configuration Release

# Test locally
pks devcontainer wizard --from-templates --sources ./bin/Release --debug
```

## Template Discovery Process

### Local Folder Sources

PKS CLI automatically detects local folder sources and:

1. **Scans for .nupkg files** in the specified directory
2. **Extracts .nuspec metadata** from each package
3. **Checks for required tags** (`devcontainer`, `pks-devcontainers`)
4. **Categorizes templates** based on `category:` tags

### Remote NuGet Sources

For remote feeds, PKS CLI:

1. **Uses NuGet V3 Search API** to query for packages
2. **Searches by tag** (`tags:pks-devcontainers`, then `tags:pks-cli`)
3. **Filters results** for devcontainer-related content
4. **Downloads package metadata** for matching templates

## Troubleshooting

### Template Not Found

**Check required tags are present:**
```bash
unzip -p YourTemplate.1.0.0.nupkg YourTemplate.nuspec | grep -i PackageTags
```

**Use debug mode to see discovery process:**
```bash
pks devcontainer wizard --from-templates --sources ./artifacts --debug
```

**Expected debug output for successful discovery:**
```
ℹ DEBUG: Starting template discovery with 1 sources
ℹ DEBUG: Source: ./artifacts
ℹ DEBUG: Detected local folder source: ./artifacts. Using direct file search.
ℹ DEBUG: Found 1 .nupkg files in ./artifacts
ℹ DEBUG: Package YourTemplate v1.0.0 has tags: devcontainer, pks-devcontainers, category:general
ℹ DEBUG: Successfully extracted template: YourTemplate v1.0.0 with matching tag 'pks-devcontainers'
✓ Found 1 templates from NuGet packages
```

### Template in Wrong Category

Add or update the category tag:
```xml
<!-- Change from general to runtime -->
<PackageTags>devcontainer pks-devcontainers category:runtime dotnet</PackageTags>
```

### No Templates Found from Remote Source

Verify the package is published and tagged correctly:
```bash
# Search on NuGet.org
https://www.nuget.org/packages?q=tags:pks-devcontainers

# Check if your package appears in results
```

## Advanced Features

### Conditional Template Content

Use `sources.modifiers` for optional features:

```json
{
  "symbols": {
    "IncludeDocker": {
      "type": "parameter",
      "datatype": "bool",
      "defaultValue": "true"
    }
  },
  "sources": [
    {
      "modifiers": [
        {
          "condition": "(!IncludeDocker)",
          "exclude": ["**/Dockerfile", "**/docker-compose.yml"]
        }
      ]
    }
  ]
}
```

### Multi-Language Templates

Support multiple programming languages in one template:

```json
{
  "symbols": {
    "Language": {
      "type": "parameter",
      "datatype": "choice",
      "choices": [
        { "choice": "csharp", "description": "C# (.NET)" },
        { "choice": "typescript", "description": "TypeScript (Node.js)" },
        { "choice": "python", "description": "Python" }
      ],
      "defaultValue": "csharp"
    }
  },
  "sources": [
    {
      "modifiers": [
        {
          "condition": "(Language != 'csharp')",
          "exclude": ["**/*.cs", "**/*.csproj"]
        },
        {
          "condition": "(Language != 'typescript')",
          "exclude": ["**/*.ts", "**/package.json", "**/tsconfig.json"]
        },
        {
          "condition": "(Language != 'python')",
          "exclude": ["**/*.py", "**/requirements.txt", "**/pyproject.toml"]
        }
      ]
    }
  ]
}
```

## Best Practices

### Template Design
- ✅ Use clear, descriptive names and descriptions
- ✅ Include comprehensive README.md documentation
- ✅ Provide sensible default values for parameters
- ✅ Test with both local and remote sources
- ✅ Follow semantic versioning

### Package Configuration
- ✅ Always include required tags: `devcontainer pks-devcontainers`
- ✅ Choose appropriate category: `category:general|runtime|web|microservices`
- ✅ Add descriptive tags for discoverability
- ✅ Set proper package metadata (author, description, license)

### DevContainer Configuration
- ✅ Use official Microsoft base images when possible
- ✅ Include essential VS Code extensions
- ✅ Configure appropriate port forwarding
- ✅ Provide useful post-create commands
- ✅ Follow devcontainer specification standards

## Examples

- **PKS Universal DevContainer**: `/templates/devcontainer/` in PKS CLI repository
- **Language-Specific Templates**: Coming soon in PKS CLI template gallery
- **Community Templates**: Search NuGet.org for `tags:pks-devcontainers`

## Resources

- 📖 [Complete Template Authoring Guide](./TEMPLATE-AUTHORING.md)
- 🔧 [DevContainer Specification](https://containers.dev/implementors/json_reference/)
- 📦 [.NET Template Engine](https://github.com/dotnet/templating)
- 🐳 [Microsoft DevContainer Images](https://github.com/devcontainers/images)