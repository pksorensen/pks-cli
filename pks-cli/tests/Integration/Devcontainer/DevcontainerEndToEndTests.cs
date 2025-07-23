using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Fixtures.Devcontainer;
using PKS.Infrastructure.Initializers.Context;
using PKS.Infrastructure.Initializers.Implementations;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Integration.Devcontainer;

/// <summary>
/// Comprehensive end-to-end tests for the devcontainer initialization system
/// Tests the complete workflow from empty project to initialized devcontainer
/// </summary>
public class DevcontainerEndToEndTests : TestBase
{
    private readonly IDevcontainerService _devcontainerService;
    private readonly IDevcontainerTemplateService _templateService;
    private readonly IDevcontainerFeatureRegistry _featureRegistry;
    private readonly IDevcontainerFileGenerator _fileGenerator;
    private readonly INuGetTemplateDiscoveryService _nugetService;
    private readonly DevcontainerInitializer _initializer;

    public DevcontainerEndToEndTests()
    {
        _devcontainerService = ServiceProvider.GetRequiredService<IDevcontainerService>();
        _templateService = ServiceProvider.GetRequiredService<IDevcontainerTemplateService>();
        _featureRegistry = ServiceProvider.GetRequiredService<IDevcontainerFeatureRegistry>();
        _fileGenerator = ServiceProvider.GetRequiredService<IDevcontainerFileGenerator>();
        _nugetService = ServiceProvider.GetRequiredService<INuGetTemplateDiscoveryService>();
        _initializer = ServiceProvider.GetRequiredService<DevcontainerInitializer>();
    }

    [Fact]
    public async Task EndToEnd_CompleteWorkflow_EmptyProjectToInitializedDevcontainer()
    {
        // Arrange
        var testName = "complete-workflow-test";
        var projectPath = CreateTestProject(testName);
        var devcontainerPath = Path.Combine(projectPath, ".devcontainer");

        var context = new InitializationContext
        {
            ProjectName = testName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = "api",
            Interactive = false,
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-features"] = new[] { "dotnet", "docker-in-docker" },
                ["devcontainer-ports"] = new[] { "5000", "5001" }
            }
        };

        // Act
        var shouldRun = await _initializer.ShouldRunAsync(context);
        shouldRun.Should().BeTrue();

        var result = await _initializer.ExecuteAsync(context);

        // Assert - Verify execution was successful
        result.Errors.Should().BeEmpty();
        result.AffectedFiles.Should().NotBeEmpty();

        // Verify devcontainer directory was created
        Directory.Exists(devcontainerPath).Should().BeTrue();

        // Verify required files were generated
        var devcontainerJsonPath = Path.Combine(devcontainerPath, "devcontainer.json");
        var dockerfilePath = Path.Combine(devcontainerPath, "Dockerfile");

        File.Exists(devcontainerJsonPath).Should().BeTrue();
        File.Exists(dockerfilePath).Should().BeTrue();

        // Verify devcontainer.json content
        var devcontainerJson = await File.ReadAllTextAsync(devcontainerJsonPath);
        var devcontainerConfig = JsonSerializer.Deserialize<DevcontainerConfiguration>(devcontainerJson);

        devcontainerConfig.Should().NotBeNull();
        devcontainerConfig!.Name.Should().Contain(testName);
        devcontainerConfig.Features.Should().NotBeEmpty();

        // Verify Dockerfile content
        var dockerfileContent = await File.ReadAllTextAsync(dockerfilePath);
        dockerfileContent.Should().Contain("FROM");
        dockerfileContent.Should().Contain("dotnet");

        // Verify result data contains expected information
        result.Data.Should().ContainKey("devcontainer_enabled");
        result.Data["devcontainer_enabled"].Should().Be("true");
        result.Data.Should().ContainKey("devcontainer_features");
        result.Data.Should().ContainKey("devcontainer_template");
    }

    [Theory]
    [InlineData("api", "dotnet-web")]
    [InlineData("web", "dotnet-web")]
    [InlineData("console", "dotnet-basic")]
    [InlineData("agent", "dotnet-basic")]
    public async Task EndToEnd_TemplateMapping_ShouldMapCorrectly(string projectTemplate, string expectedDevcontainerTemplate)
    {
        // Arrange
        var testName = $"template-mapping-{projectTemplate}";
        var projectPath = CreateTestProject(testName);

        var context = new InitializationContext
        {
            ProjectName = testName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = projectTemplate,
            Options = new Dictionary<string, object> { ["devcontainer"] = true }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Errors.Should().BeEmpty();
        result.Data.Should().ContainKey("devcontainer_template");
        result.Data["devcontainer_template"].Should().Be(expectedDevcontainerTemplate);
    }

    [Fact]
    public async Task EndToEnd_FeatureResolution_ShouldResolveFeatureDependencies()
    {
        // Arrange
        var testName = "feature-resolution-test";
        var projectPath = CreateTestProject(testName);

        var context = new InitializationContext
        {
            ProjectName = testName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = "api",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-features"] = new[] { "dotnet", "docker", "azure-cli" }
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Errors.Should().BeEmpty();

        var devcontainerPath = Path.Combine(projectPath, ".devcontainer", "devcontainer.json");
        var devcontainerJson = await File.ReadAllTextAsync(devcontainerPath);
        var config = JsonSerializer.Deserialize<DevcontainerConfiguration>(devcontainerJson);

        config!.Features.Should().NotBeEmpty();

        // Verify feature resolution converted simple names to full identifiers
        var features = config.Features.Keys.ToList();
        features.Should().Contain(f => f.Contains("ghcr.io/devcontainers/features/dotnet"));
        features.Should().Contain(f => f.Contains("ghcr.io/devcontainers/features/docker-in-docker"));
        features.Should().Contain(f => f.Contains("ghcr.io/devcontainers/features/azure-cli"));
    }

    [Fact]
    public async Task EndToEnd_DockerCompose_ShouldGenerateDockerComposeFiles()
    {
        // Arrange
        var testName = "docker-compose-test";
        var projectPath = CreateTestProject(testName);

        var context = new InitializationContext
        {
            ProjectName = testName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = "web",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-compose"] = true
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Errors.Should().BeEmpty();

        var devcontainerPath = Path.Combine(projectPath, ".devcontainer");
        var dockerComposePath = Path.Combine(devcontainerPath, "docker-compose.yml");

        File.Exists(dockerComposePath).Should().BeTrue();

        var composeContent = await File.ReadAllTextAsync(dockerComposePath);
        composeContent.Should().Contain("version:");
        composeContent.Should().Contain("services:");
    }

    [Fact]
    public async Task EndToEnd_PortForwarding_ShouldConfigureCorrectPorts()
    {
        // Arrange
        var testName = "port-forwarding-test";
        var projectPath = CreateTestProject(testName);

        var context = new InitializationContext
        {
            ProjectName = testName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = "api",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-ports"] = new[] { "3000", "8080", "9000" }
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Errors.Should().BeEmpty();

        var devcontainerPath = Path.Combine(projectPath, ".devcontainer", "devcontainer.json");
        var devcontainerJson = await File.ReadAllTextAsync(devcontainerPath);
        var config = JsonSerializer.Deserialize<DevcontainerConfiguration>(devcontainerJson);

        config!.ForwardPorts.Should().NotBeNull();
        config.ForwardPorts.Should().Contain(3000);
        config.ForwardPorts.Should().Contain(8080);
        config.ForwardPorts.Should().Contain(9000);
    }

    [Fact]
    public async Task EndToEnd_PostCreateCommand_ShouldSetCorrectCommands()
    {
        // Arrange
        var testName = "post-create-command-test";
        var projectPath = CreateTestProject(testName);
        var customCommand = "echo 'Custom setup' && npm install";

        var context = new InitializationContext
        {
            ProjectName = testName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = "web",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-post-create"] = customCommand,
                ["agentic"] = true
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Errors.Should().BeEmpty();

        var devcontainerPath = Path.Combine(projectPath, ".devcontainer", "devcontainer.json");
        var devcontainerJson = await File.ReadAllTextAsync(devcontainerPath);
        var config = JsonSerializer.Deserialize<DevcontainerConfiguration>(devcontainerJson);

        config!.PostCreateCommand.Should().Be(customCommand);
    }

    [Fact]
    public async Task EndToEnd_AutoEnableForTemplates_ShouldEnableForAppropriateTemplates()
    {
        // Test that devcontainer is auto-enabled for certain templates
        var templates = new[] { "api", "web", "agent", "agentic" };

        foreach (var template in templates)
        {
            // Arrange
            var testName = $"auto-enable-{template}";
            var projectPath = CreateTestProject(testName);

            var context = new InitializationContext
            {
                ProjectName = testName,
                TargetDirectory = projectPath,
                WorkingDirectory = projectPath,
                Template = template,
                Options = new Dictionary<string, object>() // No explicit devcontainer flag
            };

            // Act
            var shouldRun = await _initializer.ShouldRunAsync(context);

            // Assert
            shouldRun.Should().BeTrue($"Devcontainer should be auto-enabled for template '{template}'");
        }
    }

    [Fact]
    public async Task EndToEnd_NoAutoEnableForConsole_ShouldNotEnableForConsoleTemplate()
    {
        // Arrange
        var testName = "no-auto-enable-console";
        var projectPath = CreateTestProject(testName);

        var context = new InitializationContext
        {
            ProjectName = testName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = "console",
            Options = new Dictionary<string, object>() // No explicit devcontainer flag
        };

        // Act
        var shouldRun = await _initializer.ShouldRunAsync(context);

        // Assert
        shouldRun.Should().BeFalse("Devcontainer should not be auto-enabled for console template");
    }

    [Fact]
    public async Task EndToEnd_McpIntegration_ShouldAddPythonFeature()
    {
        // Arrange
        var testName = "mcp-integration-test";
        var projectPath = CreateTestProject(testName);

        var context = new InitializationContext
        {
            ProjectName = testName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = "agent",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["mcp"] = true
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Errors.Should().BeEmpty();

        var devcontainerPath = Path.Combine(projectPath, ".devcontainer", "devcontainer.json");
        var devcontainerJson = await File.ReadAllTextAsync(devcontainerPath);
        var config = JsonSerializer.Deserialize<DevcontainerConfiguration>(devcontainerJson);

        config!.Features.Keys.Should().Contain(f => f.Contains("python"));
    }

    [Fact]
    public async Task EndToEnd_GitHubIntegration_ShouldAddGitHubCliFeature()
    {
        // Arrange
        var testName = "github-integration-test";
        var projectPath = CreateTestProject(testName);

        var context = new InitializationContext
        {
            ProjectName = testName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = "api",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["github"] = true
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Errors.Should().BeEmpty();

        var devcontainerPath = Path.Combine(projectPath, ".devcontainer", "devcontainer.json");
        var devcontainerJson = await File.ReadAllTextAsync(devcontainerPath);
        var config = JsonSerializer.Deserialize<DevcontainerConfiguration>(devcontainerJson);

        config!.Features.Keys.Should().Contain(f => f.Contains("github-cli"));
    }

    [Fact]
    public async Task EndToEnd_EnvironmentVariables_ShouldSetCorrectDefaults()
    {
        // Arrange
        var testName = "environment-variables-test";
        var projectPath = CreateTestProject(testName);

        var context = new InitializationContext
        {
            ProjectName = testName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = "api",
            Options = new Dictionary<string, object> { ["devcontainer"] = true }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Errors.Should().BeEmpty();

        var devcontainerPath = Path.Combine(projectPath, ".devcontainer", "devcontainer.json");
        var devcontainerJson = await File.ReadAllTextAsync(devcontainerPath);
        var config = JsonSerializer.Deserialize<DevcontainerConfiguration>(devcontainerJson);

        config!.RemoteEnv.Should().ContainKey("DOTNET_USE_POLLING_FILE_WATCHER");
        config.RemoteEnv["DOTNET_USE_POLLING_FILE_WATCHER"].Should().Be("true");
        config.RemoteEnv.Should().ContainKey("NUGET_FALLBACK_PACKAGES");
    }

    [Fact]
    public async Task EndToEnd_InitializerOrderAndDependencies_ShouldExecuteInCorrectOrder()
    {
        // Arrange
        var testName = "initializer-order-test";
        var projectPath = CreateTestProject(testName);

        var context = new InitializationContext
        {
            ProjectName = testName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = "api",
            Options = new Dictionary<string, object> { ["devcontainer"] = true }
        };

        // Act & Assert
        var order = _initializer.Order;
        order.Should().Be(20, "DevcontainerInitializer should run after DotNetProjectInitializer (10) but before feature initializers");

        var options = _initializer.GetOptions().ToList();
        options.Should().NotBeEmpty();
        options.Should().Contain(o => o.Name == "devcontainer");
        options.Should().Contain(o => o.Name == "devcontainer-features");
        options.Should().Contain(o => o.Name == "devcontainer-template");
        options.Should().Contain(o => o.Name == "devcontainer-compose");
    }

    /// <summary>
    /// Creates a test project directory in the test-artifacts folder
    /// </summary>
    private string CreateTestProject(string projectName)
    {
        var testArtifactsPath = Path.Combine(Path.GetTempPath(), "test-artifacts", "devcontainer-e2e", projectName);

        if (Directory.Exists(testArtifactsPath))
        {
            Directory.Delete(testArtifactsPath, true);
        }

        Directory.CreateDirectory(testArtifactsPath);

        // Create a basic .csproj file to simulate an existing project
        var csprojPath = Path.Combine(testArtifactsPath, $"{projectName}.csproj");
        var csprojContent = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """;

        File.WriteAllText(csprojPath, csprojContent);

        return testArtifactsPath;
    }
}