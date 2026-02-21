using System.Text.Json;
using PKS.CLI.Tests.Infrastructure.Mocks;
using PKS.CLI.Infrastructure.Services.Models;

namespace PKS.CLI.Tests.Infrastructure.Fixtures;

/// <summary>
/// Generates test data for various PKS CLI components
/// </summary>
public static class TestDataGenerator
{
    private static readonly Random Random = new();

    /// <summary>
    /// Generates a random project name
    /// </summary>
    public static string GenerateProjectName() => $"TestProject{Random.Next(1000, 9999)}";

    /// <summary>
    /// Generates a random description
    /// </summary>
    public static string GenerateDescription() => $"Test description for automated testing {Random.Next(100, 999)}";

    /// <summary>
    /// Generates test initialization options
    /// </summary>
    public static InitializationOptions GenerateInitializationOptions(
        string? projectName = null,
        string? template = null,
        bool agentic = false,
        bool mcp = false)
    {
        return new InitializationOptions
        {
            ProjectName = projectName ?? GenerateProjectName(),
            Template = template ?? "console",
            Description = GenerateDescription(),
            Agentic = agentic,
            Mcp = mcp,
            Force = false,
            TargetDirectory = Path.GetTempPath()
        };
    }

    /// <summary>
    /// Generates test hook definitions
    /// </summary>
    public static List<HookDefinition> GenerateHookDefinitions(int count = 3)
    {
        var hooks = new List<HookDefinition>();
        for (int i = 0; i < count; i++)
        {
            hooks.Add(new HookDefinition
            {
                Name = $"test-hook-{i + 1}",
                Description = $"Test hook number {i + 1}",
                Parameters = new List<string> { "param1", "param2" }
            });
        }
        return hooks;
    }

    /// <summary>
    /// Generates test hook context
    /// </summary>
    public static HookContext GenerateHookContext(string workingDir = "")
    {
        return new HookContext
        {
            WorkingDirectory = workingDir.IsNullOrEmpty() ? Path.GetTempPath() : workingDir,
            Parameters = new Dictionary<string, object>
            {
                ["param1"] = "value1",
                ["param2"] = Random.Next(1, 100),
                ["param3"] = true
            }
        };
    }

    /// <summary>
    /// Generates test MCP server configuration
    /// </summary>
    public static PKS.CLI.Infrastructure.Services.MCP.McpServerConfig GenerateMcpServerConfig(int port = 0)
    {
        return new PKS.CLI.Infrastructure.Services.MCP.McpServerConfig
        {
            Port = port == 0 ? Random.Next(8000, 9000) : port,
            Transport = "stdio",
            Debug = true,
            ServerName = "test-server",
            ServerVersion = "1.0.0"
        };
    }

    /// <summary>
    /// Generates test agent configuration
    /// </summary>
    public static AgentConfiguration GenerateAgentConfiguration(string? name = null, string? type = null)
    {
        return new AgentConfiguration
        {
            Name = name ?? $"test-agent-{Random.Next(100, 999)}",
            Type = type ?? "automation",
            Settings = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["priority"] = Random.Next(1, 10),
                ["maxRetries"] = 3
            }
        };
    }

    /// <summary>
    /// Generates test deployment plan
    /// </summary>
    public static DeploymentPlan GenerateDeploymentPlan(string? environment = null)
    {
        return new DeploymentPlan
        {
            Environment = environment ?? "test",
            Resources = new List<string> { "deployment.yaml", "service.yaml" },
            Configuration = new Dictionary<string, object>
            {
                ["replicas"] = Random.Next(1, 5),
                ["namespace"] = "test-namespace",
                ["image"] = $"test-image:{Random.Next(1, 100)}"
            }
        };
    }

    /// <summary>
    /// Generates test file content
    /// </summary>
    public static string GenerateFileContent(string fileType = "cs")
    {
        return fileType.ToLower() switch
        {
            "cs" => GenerateCSharpContent(),
            "json" => GenerateJsonContent(),
            "yaml" => GenerateYamlContent(),
            "md" => GenerateMarkdownContent(),
            _ => $"// Test content for {fileType} file\n// Generated at {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
        };
    }

    private static string GenerateCSharpContent()
    {
        return $@"using System;

namespace TestNamespace
{{
    public class TestClass{Random.Next(100, 999)}
    {{
        public string TestProperty {{ get; set; }} = ""Test Value"";
        
        public void TestMethod()
        {{
            Console.WriteLine(""Test output"");
        }}
    }}
}}";
    }

    public static string GenerateJsonContent()
    {
        var data = new
        {
            name = GenerateProjectName(),
            version = "1.0.0",
            description = GenerateDescription(),
            timestamp = DateTime.UtcNow,
            testValue = Random.Next(1, 1000)
        };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string GenerateYamlContent()
    {
        return $@"name: {GenerateProjectName()}
version: 1.0.0
description: {GenerateDescription()}
metadata:
  created: {DateTime.UtcNow:yyyy-MM-dd}
  testValue: {Random.Next(1, 1000)}
settings:
  enabled: true
  timeout: 30";
    }

    public static string GenerateMarkdownContent()
    {
        return $@"# {GenerateProjectName()}

{GenerateDescription()}

## Features

- Feature 1
- Feature 2
- Feature 3

## Usage

```bash
pks command --option value
```

Generated on {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    }
}

// Extension method for string null/empty checking
public static class StringExtensions
{
    public static bool IsNullOrEmpty(this string? value) => string.IsNullOrEmpty(value);
}