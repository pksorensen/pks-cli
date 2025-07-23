using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Integration.Devcontainer;

/// <summary>
/// Tests for devcontainer template extraction and file generation
/// </summary>
public class DevcontainerTemplateExtractionTests : TestBase
{
    private readonly IDevcontainerTemplateService _templateService;
    private readonly IDevcontainerFileGenerator _fileGenerator;
    private readonly INuGetTemplateDiscoveryService _nugetService;

    public DevcontainerTemplateExtractionTests()
    {
        _templateService = ServiceProvider.GetRequiredService<IDevcontainerTemplateService>();
        _fileGenerator = ServiceProvider.GetRequiredService<IDevcontainerFileGenerator>();
        _nugetService = ServiceProvider.GetRequiredService<INuGetTemplateDiscoveryService>();
    }

    [Fact]
    public async Task TemplateExtraction_PksUniversalDevcontainer_ShouldExtractCorrectly()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("template-extraction-pks-universal");
        var templateName = "pks-universal-devcontainer";

        // Act
        var template = await _templateService.GetTemplateAsync(templateName);

        // Assert
        template.Should().NotBeNull();
        template!.Id.Should().Be(templateName);
        template.Name.Should().NotBeNullOrEmpty();
        template.BaseImage.Should().NotBeNullOrEmpty();
        template.RequiredFeatures.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TemplateExtraction_ExtractFiles_ShouldCreateAllRequiredFiles()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("template-extraction-files");
        var templateName = "pks-universal-devcontainer";

        var options = new DevcontainerOptions
        {
            Name = "test-extraction",
            OutputPath = testOutputPath,
            Template = templateName
        };

        // Act
        var result = await _templateService.ExtractTemplateAsync(templateName, options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ExtractedFiles.Should().NotBeEmpty();

        // Verify expected files were extracted
        result.ExtractedFiles.Should().Contain(f => f.EndsWith("devcontainer.json"));
        result.ExtractedFiles.Should().Contain(f => f.EndsWith("Dockerfile"));

        // Verify files actually exist on disk
        foreach (var file in result.ExtractedFiles)
        {
            File.Exists(file).Should().BeTrue($"Extracted file should exist: {file}");
        }
    }

    [Fact]
    public async Task TemplateExtraction_PlaceholderReplacement_ShouldReplaceCorrectly()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("template-extraction-placeholders");
        var projectName = "MyTestProject";

        var options = new DevcontainerOptions
        {
            Name = projectName,
            OutputPath = testOutputPath,
            Template = "pks-universal-devcontainer"
        };

        // Act
        var result = await _templateService.ExtractTemplateAsync("pks-universal-devcontainer", options);

        // Assert
        result.Success.Should().BeTrue();

        var devcontainerJsonPath = result.ExtractedFiles.First(f => f.EndsWith("devcontainer.json"));
        var devcontainerContent = await File.ReadAllTextAsync(devcontainerJsonPath);

        // Verify placeholders were replaced
        devcontainerContent.Should().Contain(projectName);
        devcontainerContent.Should().NotContain("${projectName}");
        devcontainerContent.Should().NotContain("{{ProjectName}}");
    }

    [Theory]
    [InlineData("dotnet-basic")]
    [InlineData("dotnet-web")]
    [InlineData("pks-universal-devcontainer")]
    public async Task TemplateExtraction_BuiltInTemplates_ShouldExtractSuccessfully(string templateName)
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory($"template-extraction-{templateName}");

        var options = new DevcontainerOptions
        {
            Name = $"test-{templateName}",
            OutputPath = testOutputPath,
            Template = templateName
        };

        // Act
        var template = await _templateService.GetTemplateAsync(templateName);
        var extractionResult = await _templateService.ExtractTemplateAsync(templateName, options);

        // Assert
        template.Should().NotBeNull();
        extractionResult.Success.Should().BeTrue();
        extractionResult.ExtractedFiles.Should().NotBeEmpty();

        // Verify devcontainer.json was created and is valid JSON
        var devcontainerJsonPath = extractionResult.ExtractedFiles.FirstOrDefault(f => f.EndsWith("devcontainer.json"));
        devcontainerJsonPath.Should().NotBeNull();

        var jsonContent = await File.ReadAllTextAsync(devcontainerJsonPath!);
        var config = JsonSerializer.Deserialize<DevcontainerConfiguration>(jsonContent);
        config.Should().NotBeNull();
    }

    [Fact]
    public async Task FileGeneration_DevcontainerJson_ShouldGenerateValidConfiguration()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("file-generation-devcontainer-json");
        var devcontainerPath = Path.Combine(testOutputPath, ".devcontainer");
        Directory.CreateDirectory(devcontainerPath);

        var configuration = new DevcontainerConfiguration
        {
            Name = "Test Development Container",
            Image = "mcr.microsoft.com/dotnet/sdk:8.0",
            Features = new Dictionary<string, object>
            {
                ["ghcr.io/devcontainers/features/dotnet:2"] = new { version = "8.0" },
                ["ghcr.io/devcontainers/features/git:1"] = new { }
            },
            ForwardPorts = new[] { 5000, 5001 },
            PostCreateCommand = "dotnet restore && dotnet build",
            RemoteEnv = new Dictionary<string, string>
            {
                ["DOTNET_USE_POLLING_FILE_WATCHER"] = "true"
            }
        };

        // Act
        var result = await _fileGenerator.GenerateDevcontainerJsonAsync(configuration, devcontainerPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.GeneratedFilePath.Should().EndWith("devcontainer.json");

        File.Exists(result.GeneratedFilePath).Should().BeTrue();

        // Verify the generated JSON is valid and contains expected content
        var generatedContent = await File.ReadAllTextAsync(result.GeneratedFilePath);
        var parsedConfig = JsonSerializer.Deserialize<DevcontainerConfiguration>(generatedContent);

        parsedConfig.Should().NotBeNull();
        parsedConfig!.Name.Should().Be(configuration.Name);
        parsedConfig.Image.Should().Be(configuration.Image);
        parsedConfig.Features.Should().HaveCount(2);
        parsedConfig.ForwardPorts.Should().BeEquivalentTo(new[] { 5000, 5001 });
        parsedConfig.PostCreateCommand.Should().Be(configuration.PostCreateCommand);
        parsedConfig.RemoteEnv.Should().ContainKey("DOTNET_USE_POLLING_FILE_WATCHER");
    }

    [Fact]
    public async Task FileGeneration_Dockerfile_ShouldGenerateValidDockerfile()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("file-generation-dockerfile");
        var devcontainerPath = Path.Combine(testOutputPath, ".devcontainer");
        Directory.CreateDirectory(devcontainerPath);

        var configuration = new DevcontainerConfiguration
        {
            Name = "Test Container",
            Image = "mcr.microsoft.com/dotnet/sdk:8.0",
            Features = new Dictionary<string, object>
            {
                ["ghcr.io/devcontainers/features/docker-in-docker:2"] = new { },
                ["ghcr.io/devcontainers/features/git:1"] = new { }
            }
        };

        // Act
        var result = await _fileGenerator.GenerateDockerfileAsync(configuration, devcontainerPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.GeneratedFilePath.Should().EndWith("Dockerfile");

        File.Exists(result.GeneratedFilePath).Should().BeTrue();

        var dockerfileContent = await File.ReadAllTextAsync(result.GeneratedFilePath);
        dockerfileContent.Should().Contain("FROM mcr.microsoft.com/dotnet/sdk:8.0");
        dockerfileContent.Should().Contain("USER vscode");
        dockerfileContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task FileGeneration_DockerCompose_ShouldGenerateValidComposeFile()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("file-generation-docker-compose");
        var devcontainerPath = Path.Combine(testOutputPath, ".devcontainer");
        Directory.CreateDirectory(devcontainerPath);

        var configuration = new DevcontainerConfiguration
        {
            Name = "Test Container",
            Build = new DevcontainerBuild
            {
                DockerfilePath = "Dockerfile",
                Context = "."
            },
            ForwardPorts = new[] { 5000, 5001, 3000 },
            Volumes = new[] { "./app:/workspace" }
        };

        // Act
        var result = await _fileGenerator.GenerateDockerComposeAsync(configuration, devcontainerPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.GeneratedFilePath.Should().EndWith("docker-compose.yml");

        File.Exists(result.GeneratedFilePath).Should().BeTrue();

        var composeContent = await File.ReadAllTextAsync(result.GeneratedFilePath);
        composeContent.Should().Contain("version:");
        composeContent.Should().Contain("services:");
        composeContent.Should().Contain("devcontainer:");
        composeContent.Should().Contain("build:");
        composeContent.Should().Contain("ports:");
        composeContent.Should().Contain("5000:5000");
        composeContent.Should().Contain("5001:5001");
        composeContent.Should().Contain("3000:3000");
    }

    [Fact]
    public async Task FileGeneration_WithCustomSettings_ShouldApplyCustomSettings()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("file-generation-custom-settings");
        var devcontainerPath = Path.Combine(testOutputPath, ".devcontainer");
        Directory.CreateDirectory(devcontainerPath);

        var customSettings = new Dictionary<string, object>
        {
            ["workspaceFolder"] = "/app",
            ["remoteUser"] = "developer",
            ["runArgs"] = new[] { "--cap-add=SYS_PTRACE", "--security-opt", "seccomp=unconfined" },
            ["customizations"] = new Dictionary<string, object>
            {
                ["vscode"] = new Dictionary<string, object>
                {
                    ["extensions"] = new[] { "ms-dotnettools.csharp", "ms-vscode.vscode-docker" },
                    ["settings"] = new Dictionary<string, object>
                    {
                        ["terminal.integrated.shell.linux"] = "/bin/bash"
                    }
                }
            }
        };

        var configuration = new DevcontainerConfiguration
        {
            Name = "Custom Settings Container",
            Image = "mcr.microsoft.com/dotnet/sdk:8.0",
            CustomSettings = customSettings
        };

        // Act
        var result = await _fileGenerator.GenerateDevcontainerJsonAsync(configuration, devcontainerPath);

        // Assert
        result.Success.Should().BeTrue();

        var generatedContent = await File.ReadAllTextAsync(result.GeneratedFilePath);
        generatedContent.Should().Contain("\"workspaceFolder\": \"/app\"");
        generatedContent.Should().Contain("\"remoteUser\": \"developer\"");
        generatedContent.Should().Contain("ms-dotnettools.csharp");
        generatedContent.Should().Contain("ms-vscode.vscode-docker");
        generatedContent.Should().Contain("terminal.integrated.shell.linux");
    }

    [Fact]
    public async Task TemplateExtraction_WithFeatureConfiguration_ShouldConfigureFeaturesCorrectly()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("template-extraction-features");

        var options = new DevcontainerOptions
        {
            Name = "feature-test",
            OutputPath = testOutputPath,
            Template = "dotnet-web",
            Features = new List<string>
            {
                "ghcr.io/devcontainers/features/dotnet:2",
                "ghcr.io/devcontainers/features/node:1",
                "ghcr.io/devcontainers/features/docker-in-docker:2"
            }
        };

        // Act
        var result = await _templateService.ExtractTemplateAsync("dotnet-web", options);

        // Assert
        result.Success.Should().BeTrue();

        var devcontainerJsonPath = result.ExtractedFiles.First(f => f.EndsWith("devcontainer.json"));
        var devcontainerContent = await File.ReadAllTextAsync(devcontainerJsonPath);
        var config = JsonSerializer.Deserialize<DevcontainerConfiguration>(devcontainerContent);

        config!.Features.Should().ContainKey("ghcr.io/devcontainers/features/dotnet:2");
        config.Features.Should().ContainKey("ghcr.io/devcontainers/features/node:1");
        config.Features.Should().ContainKey("ghcr.io/devcontainers/features/docker-in-docker:2");
    }

    [Fact]
    public async Task FileGeneration_PathValidation_ShouldValidateOutputPaths()
    {
        // Arrange
        var readOnlyPath = "/readonly/path";
        var validPath = CreateTestArtifactDirectory("path-validation-valid");

        // Act & Assert - Read-only path
        var readOnlyValidation = await _fileGenerator.ValidateOutputPathAsync(readOnlyPath);
        readOnlyValidation.IsValid.Should().BeFalse();
        readOnlyValidation.CanWrite.Should().BeFalse();
        readOnlyValidation.Errors.Should().NotBeEmpty();

        // Act & Assert - Valid path
        var validValidation = await _fileGenerator.ValidateOutputPathAsync(validPath);
        validValidation.IsValid.Should().BeTrue();
        validValidation.CanWrite.Should().BeTrue();
        validValidation.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task TemplateExtraction_NonExistentTemplate_ShouldHandleGracefully()
    {
        // Arrange
        var nonExistentTemplate = "non-existent-template";
        var testOutputPath = CreateTestArtifactDirectory("template-extraction-nonexistent");

        var options = new DevcontainerOptions
        {
            Name = "test",
            OutputPath = testOutputPath,
            Template = nonExistentTemplate
        };

        // Act
        var template = await _templateService.GetTemplateAsync(nonExistentTemplate);
        var extractionResult = await _templateService.ExtractTemplateAsync(nonExistentTemplate, options);

        // Assert
        template.Should().BeNull();
        extractionResult.Success.Should().BeFalse();
        extractionResult.ErrorMessage.Should().NotBeNullOrEmpty();
        extractionResult.ExtractedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task FileGeneration_ConcurrentGeneration_ShouldHandleMultipleRequests()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("file-generation-concurrent");
        var tasks = new List<Task<FileGenerationResult>>();

        // Create multiple devcontainer configurations
        for (int i = 0; i < 5; i++)
        {
            var devcontainerPath = Path.Combine(testOutputPath, $"devcontainer-{i}", ".devcontainer");
            Directory.CreateDirectory(devcontainerPath);

            var configuration = new DevcontainerConfiguration
            {
                Name = $"Concurrent Test Container {i}",
                Image = "mcr.microsoft.com/dotnet/sdk:8.0",
                Features = new Dictionary<string, object>
                {
                    ["ghcr.io/devcontainers/features/dotnet:2"] = new { }
                }
            };

            // Act - Start concurrent generation tasks
            tasks.Add(_fileGenerator.GenerateDevcontainerJsonAsync(configuration, devcontainerPath));
        }

        // Wait for all tasks to complete
        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(5);
        results.Should().OnlyContain(r => r.Success);

        foreach (var result in results)
        {
            File.Exists(result.GeneratedFilePath).Should().BeTrue();
        }
    }

    /// <summary>
    /// Creates a test artifact directory for generated files
    /// </summary>
    private string CreateTestArtifactDirectory(string testName)
    {
        var testArtifactsPath = Path.Combine(Path.GetTempPath(), "test-artifacts", "template-extraction", testName);

        if (Directory.Exists(testArtifactsPath))
        {
            Directory.Delete(testArtifactsPath, true);
        }

        Directory.CreateDirectory(testArtifactsPath);
        return testArtifactsPath;
    }
}