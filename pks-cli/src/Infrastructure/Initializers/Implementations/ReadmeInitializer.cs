using PKS.Infrastructure.Initializers.Base;
using PKS.Infrastructure.Initializers.Context;

namespace PKS.Infrastructure.Initializers.Implementations;

/// <summary>
/// Template-based initializer for creating README files
/// </summary>
public class ReadmeInitializer : TemplateInitializer
{
    public override string Id => "readme";
    public override string Name => "README Generator";
    public override string Description => "Creates a comprehensive README.md file for the project";
    public override int Order => 90; // Run towards the end

    protected override string TemplateDirectory => "readme";

    public override IEnumerable<InitializerOption> GetOptions()
    {
        return new[]
        {
            InitializerOption.Flag("include-badges", "Include status badges in README", "b"),
            InitializerOption.Flag("include-contributing", "Include contributing guidelines", "c"),
            InitializerOption.String("license", "License type (MIT, Apache-2.0, GPL-3.0)", "l", "MIT")
        };
    }

    public override Task<bool> ShouldRunAsync(InitializationContext context)
    {
        // Always run, but create inline content since we don't have template files
        return Task.FromResult(true);
    }

    protected override async Task<InitializationResult> ExecuteInternalAsync(InitializationContext context)
    {
        // Since we don't have actual template files, generate content inline
        var result = InitializationResult.CreateSuccess("Generated README.md");

        var includeBadges = context.GetOption("include-badges", true);
        var includeContributing = context.GetOption("include-contributing", true);
        var license = context.GetOption("license", "MIT");

        var readmeContent = GenerateReadmeContent(context, includeBadges, includeContributing, license);
        var readmePath = Path.Combine(context.TargetDirectory, "README.md");

        await WriteFileAsync(readmePath, readmeContent, context);
        result.AffectedFiles.Add(readmePath);

        // Also create a simple LICENSE file
        var licenseContent = GenerateLicenseContent(license, context);
        var licensePath = Path.Combine(context.TargetDirectory, "LICENSE");
        
        await WriteFileAsync(licensePath, licenseContent, context);
        result.AffectedFiles.Add(licensePath);

        result.Message = $"Generated README.md and {license} LICENSE";
        return result;
    }

    private string GenerateReadmeContent(InitializationContext context, bool includeBadges, bool includeContributing, string? license)
    {
        var badges = includeBadges ? GenerateBadges(context) : "";
        var contributing = includeContributing ? GenerateContributingSection() : "";
        var agenticFeatures = context.GetOption("agentic", false) ? "- 🤖 AI-powered agentic capabilities\n- 🔄 Intelligent automation features" : "";
        var agenticStructure = context.GetOption("agentic", false) ? "Agents/                 # AI agents and automation\n├── Configuration/        # Configuration files\n├── " : "";
        var agenticSection = context.GetOption("agentic", false) ? @"
## Agentic Features

This project includes AI-powered agentic capabilities:

- **Automation Agent**: Intelligent task automation and code generation
- **Monitoring**: Real-time system monitoring with AI insights
- **Auto-scaling**: Intelligent resource management
- **Configuration**: Flexible AI provider integration (OpenAI, Azure, Local)

### Using Agents

```bash
# Check agent status
pks agent status

# Execute automation tasks
pks agent run automation-001 ""generate service for User entity""
```
" : "";
        var agenticConfig = context.GetOption("agentic", false) ? @",
  ""AgentConfiguration"": {
    ""DefaultProvider"": ""openai"",
    ""EnableMonitoring"": true
  }" : "";
        
        return $@"# {context.ProjectName}

{badges}

{context.Description ?? $"A modern .NET {context.Template} application built with PKS CLI."}

## Overview

{context.ProjectName} is a {context.Template} application that leverages modern .NET 8 features and best practices. This project was initialized with PKS CLI to provide an excellent developer experience and agentic capabilities.

## Features

- ✅ Modern .NET 8 architecture
- 🚀 High-performance and scalable design
- 🛡️ Built-in security best practices
- 📦 Dependency injection ready
- 🔧 Easy configuration management
{agenticFeatures}

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [PKS CLI](https://github.com/your-org/pks-cli) (for advanced features)

### Installation

1. Clone the repository:
   ```bash
   git clone https://github.com/your-username/{context.ProjectName.ToLowerInvariant()}.git
   cd {context.ProjectName}
   ```

2. Restore dependencies:
   ```bash
   dotnet restore
   ```

3. Build the project:
   ```bash
   dotnet build
   ```

4. Run the application:
   ```bash
   dotnet run
   ```

## Project Structure

```
{context.ProjectName}/
├── {agenticStructure}Program.cs              # Application entry point
├── {context.ProjectName}.csproj    # Project configuration
├── README.md            # This file
└── LICENSE              # License information
```

{agenticSection}

## Configuration

Configuration is managed through `appsettings.json` and environment variables:

```json
{{
  ""Logging"": {{
    ""LogLevel"": {{
      ""Default"": ""Information""
    }}
  }}{agenticConfig}
}}
```

## Development

### Building

```bash
# Development build
dotnet build

# Release build
dotnet build --configuration Release
```

### Testing

```bash
# Run all tests
dotnet test

# Run with coverage
dotnet test --collect:""XPlat Code Coverage""
```

### Deployment

This project supports various deployment options:

```bash
# Deploy with PKS CLI (recommended)
pks deploy --environment production

# Traditional deployment
dotnet publish --configuration Release
```

{contributing}

## License

This project is licensed under the {license} License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- Built with [PKS CLI](https://github.com/your-org/pks-cli)
- Powered by [.NET 8](https://dotnet.microsoft.com/)
- Enhanced with modern development practices

---

**🚀 Ready to revolutionize your development experience!**

For more information about PKS CLI and agentic development, visit our [documentation](https://docs.pks-cli.com).
";
    }

    private string GenerateBadges(InitializationContext context)
    {
        var agenticBadge = context.GetOption("agentic", false) ? "[![Agentic](https://img.shields.io/badge/🤖-Agentic-green.svg)](https://docs.pks-cli.com/agentic)" : "";
        var license = context.GetOption("license", "MIT");
        
        return $@"[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Build Status](https://img.shields.io/github/workflow/status/your-username/{context.ProjectName.ToLowerInvariant()}/CI)](https://github.com/your-username/{context.ProjectName.ToLowerInvariant()}/actions)
[![License](https://img.shields.io/badge/license-{license}-blue.svg)](LICENSE)
[![PKS CLI](https://img.shields.io/badge/PKS-CLI-cyan.svg)](https://github.com/your-org/pks-cli)
{agenticBadge}

";
    }

    private string GenerateContributingSection()
    {
        return """
## Contributing

We welcome contributions! Please follow these steps:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

### Development Guidelines

- Follow [C# coding conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Write tests for new features
- Update documentation as needed
- Use conventional commit messages

### Code of Conduct

This project adheres to a [Code of Conduct](CODE_OF_CONDUCT.md). By participating, you are expected to uphold this code.

""";
    }

    private string GenerateLicenseContent(string? license, InitializationContext context)
    {
        var year = DateTime.Now.Year;
        
        return license?.ToUpperInvariant() switch
        {
            "MIT" => $"""
MIT License

Copyright (c) {year} {context.ProjectName}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
""",
            "APACHE-2.0" => $"""
Apache License
Version 2.0, January 2004
http://www.apache.org/licenses/

Copyright {year} {context.ProjectName}

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
""",
            _ => $"""
Copyright (c) {year} {context.ProjectName}

All rights reserved.
"""
        };
    }
}