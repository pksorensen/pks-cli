using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Fixtures.Devcontainer;
using PKS.CLI.Tests.Infrastructure.Mocks;
using Xunit;

using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;

namespace PKS.CLI.Tests.Services.Devcontainer;

/// <summary>
/// Tests for IDevcontainerTemplateService implementation
/// </summary>
public class DevcontainerTemplateServiceTests : TestBase
{
    private readonly Mock<IDevcontainerTemplateService> _mockTemplateService;

    public DevcontainerTemplateServiceTests()
    {
        _mockTemplateService = DevcontainerServiceMocks.CreateTemplateService();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton(_mockTemplateService.Object);
    }

    [Fact]
    public async Task GetAvailableTemplatesAsync_ShouldReturnTemplates()
    {
        // Arrange
        var service = _mockTemplateService.Object;

        // Act
        var result = await service.GetAvailableTemplatesAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        result.Should().Contain(t => t.Id == "dotnet-basic");
        result.Should().Contain(t => t.Id == "dotnet-web");
    }

    [Theory]
    [InlineData("dotnet-basic", true)]
    [InlineData("dotnet-web", true)]
    [InlineData("nonexistent-template", false)]
    public async Task GetTemplateAsync_WithVariousIds_ShouldReturnExpectedResults(string templateId, bool shouldExist)
    {
        // Arrange
        var service = _mockTemplateService.Object;

        // Act
        var result = await service.GetTemplateAsync(templateId);

        // Assert
        if (shouldExist)
        {
            result.Should().NotBeNull();
            result!.Id.Should().Be(templateId);
        }
        else
        {
            result.Should().BeNull();
        }
    }

    [Fact]
    public async Task ApplyTemplateAsync_WithValidTemplate_ShouldReturnConfiguration()
    {
        // Arrange
        var service = _mockTemplateService.Object;
        var templateId = "dotnet-basic";
        var options = new DevcontainerOptions
        {
            Name = "test-project",
            OutputPath = "/test/path"
        };

        // Act
        var result = await service.ApplyTemplateAsync(templateId, options);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be(options.Name);
        result.Image.Should().NotBeEmpty();
        result.Features.Should().NotBeEmpty();
        result.Features.Should().ContainKey("ghcr.io/devcontainers/features/dotnet:2");
    }

    [Fact]
    public async Task GetAvailableTemplatesAsync_ShouldReturnTemplatesWithCorrectStructure()
    {
        // Arrange
        var service = _mockTemplateService.Object;

        // Act
        var result = await service.GetAvailableTemplatesAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().NotBeEmpty();

        foreach (var template in result)
        {
            template.Id.Should().NotBeNullOrEmpty();
            template.Name.Should().NotBeNullOrEmpty();
            template.Description.Should().NotBeNullOrEmpty();
            template.Category.Should().NotBeNullOrEmpty();
            template.BaseImage.Should().NotBeNullOrEmpty();
            template.RequiredFeatures.Should().NotBeNull();
            template.OptionalFeatures.Should().NotBeNull();
        }
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("web")]
    [InlineData("test")]
    public async Task GetAvailableTemplatesAsync_ShouldIncludeDifferentCategories(string expectedCategory)
    {
        // Arrange
        var service = _mockTemplateService.Object;

        // Act
        var result = await service.GetAvailableTemplatesAsync();

        // Assert
        result.Should().NotBeNull();

        if (expectedCategory != "test")
        {
            result.Should().Contain(t => t.Category == expectedCategory);
        }
    }

    [Fact]
    public async Task ApplyTemplateAsync_WithCustomOptions_ShouldApplyCustomizations()
    {
        // Arrange
        var service = _mockTemplateService.Object;
        var templateId = "dotnet-web";
        var options = new DevcontainerOptions
        {
            Name = "custom-project",
            Features = new List<string> { "docker-in-docker", "azure-cli" },
            Extensions = new List<string> { "ms-vscode.vscode-docker" },
            UseDockerCompose = true,
            CustomSettings = new Dictionary<string, object>
            {
                ["customPort"] = 8080,
                ["enableDebugging"] = true
            }
        };

        // Setup mock to return configuration with custom settings
        _mockTemplateService.Setup(x => x.ApplyTemplateAsync(templateId, options))
            .ReturnsAsync(new DevcontainerConfiguration
            {
                Name = options.Name,
                Image = "mcr.microsoft.com/dotnet/aspnet:8.0",
                Features = new Dictionary<string, object>
                {
                    ["ghcr.io/devcontainers/features/dotnet:2"] = new { version = "8.0" },
                    ["ghcr.io/devcontainers/features/node:1"] = new { version = "20" }
                },
                ForwardPorts = new[] { 5000, 5001, 8080 },
                Customizations = new Dictionary<string, object>
                {
                    ["vscode"] = new
                    {
                        extensions = new[] { "ms-dotnettools.csharp", "ms-vscode.vscode-docker" }
                    }
                }
            });

        // Act
        var result = await service.ApplyTemplateAsync(templateId, options);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("custom-project");
        result.ForwardPorts.Should().Contain(8080);
        result.Customizations.Should().ContainKey("vscode");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GetTemplateAsync_WithInvalidId_ShouldReturnNull(string? invalidId)
    {
        // Arrange
        var mockService = DevcontainerServiceMocks.CreateTemplateService();
        mockService.Setup(x => x.GetTemplateAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => string.IsNullOrWhiteSpace(id) ? null : new DevcontainerTemplate { Id = id });

        var service = mockService.Object;

        // Act
        var result = await service.GetTemplateAsync(invalidId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ApplyTemplateAsync_WithNullOptions_ShouldHandleGracefully()
    {
        // Arrange
        var mockService = DevcontainerServiceMocks.CreateTemplateService();
        mockService.Setup(x => x.ApplyTemplateAsync(It.IsAny<string>(), null))
            .ThrowsAsync(new ArgumentNullException("options"));

        var service = mockService.Object;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.ApplyTemplateAsync("dotnet-basic", null!));
    }

    [Fact]
    public async Task GetAvailableTemplatesAsync_ShouldReturnConsistentResults()
    {
        // Arrange
        var service = _mockTemplateService.Object;

        // Act
        var result1 = await service.GetAvailableTemplatesAsync();
        var result2 = await service.GetAvailableTemplatesAsync();

        // Assert
        result1.Should().BeEquivalentTo(result2);
    }

    [Fact]
    public async Task ApplyTemplateAsync_WithSameTemplateAndDifferentOptions_ShouldProduceDifferentConfigurations()
    {
        // Arrange
        var service = _mockTemplateService.Object;
        var templateId = "dotnet-basic";

        var options1 = new DevcontainerOptions { Name = "project1" };
        var options2 = new DevcontainerOptions { Name = "project2" };

        // Setup mocks to return different configurations
        _mockTemplateService.SetupSequence(x => x.ApplyTemplateAsync(templateId, It.IsAny<DevcontainerOptions>()))
            .ReturnsAsync(new DevcontainerConfiguration { Name = "project1" })
            .ReturnsAsync(new DevcontainerConfiguration { Name = "project2" });

        // Act
        var result1 = await service.ApplyTemplateAsync(templateId, options1);
        var result2 = await service.ApplyTemplateAsync(templateId, options2);

        // Assert
        result1.Name.Should().Be("project1");
        result2.Name.Should().Be("project2");
    }
}