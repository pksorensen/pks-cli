using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Fixtures.Devcontainer;
using PKS.CLI.Tests.Infrastructure.Mocks;
using System.Text.Json;
using Xunit;

using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;

namespace PKS.CLI.Tests.Services.Devcontainer;

/// <summary>
/// Tests for IDevcontainerFileGenerator implementation
/// </summary>
public class DevcontainerFileGeneratorTests : TestBase
{
    private readonly Mock<IDevcontainerFileGenerator> _mockFileGenerator;

    public DevcontainerFileGeneratorTests()
    {
        _mockFileGenerator = DevcontainerServiceMocks.CreateFileGenerator();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton(_mockFileGenerator.Object);
    }

    [Fact]
    public async Task GenerateDevcontainerJsonAsync_WithValidConfiguration_ShouldGenerateCorrectJson()
    {
        // Arrange
        var generator = _mockFileGenerator.Object;
        var configuration = DevcontainerTestData.GetBasicConfiguration();
        var outputPath = CreateTempDirectory();

        // Act
        var result = await generator.GenerateDevcontainerJsonAsync(configuration, outputPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.FilePath.Should().EndWith("devcontainer.json");
        result.Content.Should().NotBeEmpty();
        result.ErrorMessage.Should().BeEmpty();

        // Verify JSON structure
        var jsonDocument = JsonDocument.Parse(result.Content);
        var root = jsonDocument.RootElement;
        
        root.GetProperty("name").GetString().Should().Be(configuration.Name);
        root.GetProperty("image").GetString().Should().Be(configuration.Image);
        root.TryGetProperty("features", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GenerateDockerfileAsync_WithValidConfiguration_ShouldGenerateCorrectDockerfile()
    {
        // Arrange
        var generator = _mockFileGenerator.Object;
        var configuration = DevcontainerTestData.GetBasicConfiguration();
        var outputPath = CreateTempDirectory();

        // Act
        var result = await generator.GenerateDockerfileAsync(configuration, outputPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.FilePath.Should().EndWith("Dockerfile");
        result.Content.Should().NotBeEmpty();
        result.Content.Should().Contain("FROM");
        result.Content.Should().Contain(configuration.Image);
        result.ErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task GenerateDockerComposeAsync_WithValidConfiguration_ShouldGenerateCorrectDockerCompose()
    {
        // Arrange
        var generator = _mockFileGenerator.Object;
        var configuration = DevcontainerTestData.GetBasicConfiguration();
        var outputPath = CreateTempDirectory();

        // Act
        var result = await generator.GenerateDockerComposeAsync(configuration, outputPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.FilePath.Should().EndWith("docker-compose.yml");
        result.Content.Should().NotBeEmpty();
        result.Content.Should().Contain("version:");
        result.Content.Should().Contain("services:");
        result.ErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateOutputPathAsync_WithValidPath_ShouldReturnValid()
    {
        // Arrange
        var generator = _mockFileGenerator.Object;
        var validPath = CreateTempDirectory();

        // Act
        var result = await generator.ValidateOutputPathAsync(validPath);

        // Assert
        result.Should().NotBeNull();
        result.IsValid.Should().BeTrue();
        result.CanWrite.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateOutputPathAsync_WithInvalidPath_ShouldReturnInvalid()
    {
        // Arrange
        var mockGenerator = DevcontainerServiceMocks.CreateFileGenerator();
        mockGenerator.Setup(x => x.ValidateOutputPathAsync("/invalid/path"))
            .ReturnsAsync(new PathValidationResult
            {
                IsValid = false,
                CanWrite = false,
                Errors = new List<string> { "Path does not exist", "No write permission" }
            });

        var generator = mockGenerator.Object;
        var invalidPath = "/invalid/path";

        // Act
        var result = await generator.ValidateOutputPathAsync(invalidPath);

        // Assert
        result.Should().NotBeNull();
        result.IsValid.Should().BeFalse();
        result.CanWrite.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().Contain("Path does not exist");
    }

    [Fact]
    public async Task GenerateDevcontainerJsonAsync_WithComplexConfiguration_ShouldIncludeAllProperties()
    {
        // Arrange
        var generator = _mockFileGenerator.Object;
        var configuration = DevcontainerTestData.GetComplexConfiguration();
        var outputPath = CreateTempDirectory();

        // Setup mock to return complex JSON
        _mockFileGenerator.Setup(x => x.GenerateDevcontainerJsonAsync(configuration, outputPath))
            .ReturnsAsync(new FileGenerationResult
            {
                Success = true,
                FilePath = Path.Combine(outputPath, ".devcontainer", "devcontainer.json"),
                Content = JsonSerializer.Serialize(configuration, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                })
            });

        // Act
        var result = await generator.GenerateDevcontainerJsonAsync(configuration, outputPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Content.Should().NotBeEmpty();

        var jsonDocument = JsonDocument.Parse(result.Content);
        var root = jsonDocument.RootElement;
        
        root.GetProperty("name").GetString().Should().Be(configuration.Name);
        root.GetProperty("image").GetString().Should().Be(configuration.Image);
        root.TryGetProperty("features", out var features).Should().BeTrue();
        features.GetArrayLength().Should().BeGreaterThan(0);
        root.TryGetProperty("customizations", out _).Should().BeTrue();
        root.TryGetProperty("forwardPorts", out var ports).Should().BeTrue();
        ports.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GenerateDevcontainerJsonAsync_WithInvalidOutputPath_ShouldReturnError(string invalidPath)
    {
        // Arrange
        var mockGenerator = DevcontainerServiceMocks.CreateFileGenerator();
        mockGenerator.Setup(x => x.GenerateDevcontainerJsonAsync(It.IsAny<DevcontainerConfiguration>(), It.IsAny<string>()))
            .ReturnsAsync((DevcontainerConfiguration config, string path) =>
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new FileGenerationResult
                    {
                        Success = false,
                        ErrorMessage = "Output path cannot be null or empty"
                    };
                }
                return new FileGenerationResult { Success = true };
            });

        var generator = mockGenerator.Object;
        var configuration = DevcontainerTestData.GetBasicConfiguration();

        // Act
        var result = await generator.GenerateDevcontainerJsonAsync(configuration, invalidPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GenerateDockerfileAsync_WithCustomBaseImage_ShouldUseCorrectImage()
    {
        // Arrange
        var generator = _mockFileGenerator.Object;
        var configuration = DevcontainerTestData.GetBasicConfiguration();
        configuration.Image = "custom/image:latest";
        var outputPath = CreateTempDirectory();

        // Setup mock to return Dockerfile with custom image
        _mockFileGenerator.Setup(x => x.GenerateDockerfileAsync(configuration, outputPath))
            .ReturnsAsync(new FileGenerationResult
            {
                Success = true,
                FilePath = Path.Combine(outputPath, ".devcontainer", "Dockerfile"),
                Content = $"FROM {configuration.Image}\n\n# Custom dockerfile content\nRUN apt-get update"
            });

        // Act
        var result = await generator.GenerateDockerfileAsync(configuration, outputPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Content.Should().Contain($"FROM {configuration.Image}");
    }

    [Fact]
    public async Task GenerateDockerComposeAsync_WithMultipleServices_ShouldIncludeAllServices()
    {
        // Arrange
        var generator = _mockFileGenerator.Object;
        var configuration = DevcontainerTestData.GetComplexConfiguration();
        var outputPath = CreateTempDirectory();

        // Setup mock to return docker-compose with multiple services
        _mockFileGenerator.Setup(x => x.GenerateDockerComposeAsync(configuration, outputPath))
            .ReturnsAsync(new FileGenerationResult
            {
                Success = true,
                FilePath = Path.Combine(outputPath, ".devcontainer", "docker-compose.yml"),
                Content = DevcontainerTestData.GetDockerComposeYml()
            });

        // Act
        var result = await generator.GenerateDockerComposeAsync(configuration, outputPath);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Content.Should().Contain("devcontainer:");
        result.Content.Should().Contain("database:");
        result.Content.Should().Contain("postgres");
    }

    [Fact]
    public async Task GenerateFiles_ShouldCreateConsistentFileStructure()
    {
        // Arrange
        var generator = _mockFileGenerator.Object;
        var configuration = DevcontainerTestData.GetBasicConfiguration();
        var outputPath = CreateTempDirectory();

        // Act
        var jsonResult = await generator.GenerateDevcontainerJsonAsync(configuration, outputPath);
        var dockerfileResult = await generator.GenerateDockerfileAsync(configuration, outputPath);
        var composeResult = await generator.GenerateDockerComposeAsync(configuration, outputPath);

        // Assert
        jsonResult.FilePath.Should().Contain(".devcontainer");
        dockerfileResult.FilePath.Should().Contain(".devcontainer");
        composeResult.FilePath.Should().Contain(".devcontainer");

        Path.GetDirectoryName(jsonResult.FilePath).Should().Be(Path.GetDirectoryName(dockerfileResult.FilePath));
        Path.GetDirectoryName(jsonResult.FilePath).Should().Be(Path.GetDirectoryName(composeResult.FilePath));
    }

    [Fact]
    public async Task ValidateOutputPathAsync_WithReadOnlyPath_ShouldReturnCannotWrite()
    {
        // Arrange
        var mockGenerator = DevcontainerServiceMocks.CreateFileGenerator();
        mockGenerator.Setup(x => x.ValidateOutputPathAsync("/readonly/path"))
            .ReturnsAsync(new PathValidationResult
            {
                IsValid = true,
                CanWrite = false,
                Errors = new List<string> { "Path is read-only" }
            });

        var generator = mockGenerator.Object;

        // Act
        var result = await generator.ValidateOutputPathAsync("/readonly/path");

        // Assert
        result.Should().NotBeNull();
        result.IsValid.Should().BeTrue();
        result.CanWrite.Should().BeFalse();
        result.Errors.Should().Contain("Path is read-only");
    }

    [Theory]
    [InlineData("devcontainer.json")]
    [InlineData("Dockerfile")]
    [InlineData("docker-compose.yml")]
    public async Task GenerateFiles_ShouldProduceValidFileNames(string expectedFileName)
    {
        // Arrange
        var generator = _mockFileGenerator.Object;
        var configuration = DevcontainerTestData.GetBasicConfiguration();
        var outputPath = CreateTempDirectory();

        // Act & Assert
        switch (expectedFileName)
        {
            case "devcontainer.json":
                var jsonResult = await generator.GenerateDevcontainerJsonAsync(configuration, outputPath);
                Path.GetFileName(jsonResult.FilePath).Should().Be(expectedFileName);
                break;
            case "Dockerfile":
                var dockerResult = await generator.GenerateDockerfileAsync(configuration, outputPath);
                Path.GetFileName(dockerResult.FilePath).Should().Be(expectedFileName);
                break;
            case "docker-compose.yml":
                var composeResult = await generator.GenerateDockerComposeAsync(configuration, outputPath);
                Path.GetFileName(composeResult.FilePath).Should().Be(expectedFileName);
                break;
        }
    }
}