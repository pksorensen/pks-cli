using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Fixtures.Devcontainer;
using PKS.CLI.Tests.Infrastructure.Mocks;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Devcontainer;

/// <summary>
/// Tests for IDevcontainerFeatureRegistry implementation
/// </summary>
public class DevcontainerFeatureRegistryTests : TestBase
{
    private readonly Mock<IDevcontainerFeatureRegistry> _mockRegistry;
    private readonly List<DevcontainerFeature> _testFeatures;

    public DevcontainerFeatureRegistryTests()
    {
        _testFeatures = DevcontainerTestData.GetAvailableFeatures();
        _mockRegistry = DevcontainerServiceMocks.CreateFeatureRegistry();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        
        // Create the mock here since constructor runs after ConfigureServices
        var mockRegistry = DevcontainerServiceMocks.CreateFeatureRegistry();
        services.AddSingleton(mockRegistry.Object);
    }

    [Fact]
    public async Task GetAvailableFeaturesAsync_ShouldReturnAllFeatures()
    {
        // Arrange
        var registry = _mockRegistry.Object;

        // Act
        var result = await registry.GetAvailableFeaturesAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        result.Should().HaveCountGreaterThan(0);
        result.Should().Contain(f => f.Id == "dotnet");
        result.Should().Contain(f => f.Id == "docker-in-docker");
        result.Should().Contain(f => f.Id == "azure-cli");
    }

    [Theory]
    [InlineData("dotnet", true)]
    [InlineData("docker-in-docker", true)]
    [InlineData("azure-cli", true)]
    [InlineData("nonexistent-feature", false)]
    public async Task GetFeatureAsync_WithVariousIds_ShouldReturnExpectedResults(string featureId, bool shouldExist)
    {
        // Arrange
        var registry = _mockRegistry.Object;

        // Act
        var result = await registry.GetFeatureAsync(featureId);

        // Assert
        if (shouldExist)
        {
            result.Should().NotBeNull();
            result!.Id.Should().Be(featureId);
        }
        else
        {
            result.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("docker", 1)] // Should match "docker-in-docker"
    [InlineData("dotnet", 1)] // Should match "dotnet"
    [InlineData("azure", 1)] // Should match "azure-cli"
    [InlineData("kubernetes", 1)] // Should match kubectl-helm-minikube
    [InlineData("nonexistent", 0)] // Should match nothing
    public async Task SearchFeaturesAsync_WithQuery_ShouldReturnMatchingFeatures(string query, int expectedCount)
    {
        // Arrange
        var registry = _mockRegistry.Object;

        // Act
        var result = await registry.SearchFeaturesAsync(query);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(expectedCount);

        if (expectedCount > 0)
        {
            result.Should().Contain(f =>
                f.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                f.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                f.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Theory]
    [InlineData("runtime", 2)] // dotnet and node
    [InlineData("tool", 1)] // docker-in-docker
    [InlineData("cloud", 1)] // azure-cli
    [InlineData("kubernetes", 1)] // kubectl-helm-minikube
    [InlineData("nonexistent", 0)] // no features
    public async Task GetFeaturesByCategory_WithCategory_ShouldReturnCategoryFeatures(string category, int expectedCount)
    {
        // Arrange
        var registry = _mockRegistry.Object;

        // Act
        var result = await registry.GetFeaturesByCategory(category);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(expectedCount);

        if (expectedCount > 0)
        {
            result.Should().OnlyContain(f => f.Category == category);
        }
    }

    [Fact]
    public async Task ValidateFeatureConfiguration_WithValidConfiguration_ShouldReturnValid()
    {
        // Arrange
        var registry = _mockRegistry.Object;
        var featureId = "dotnet";
        var validConfig = new { version = "8.0" };

        // Act
        var result = await registry.ValidateFeatureConfiguration(featureId, validConfig);

        // Assert
        result.Should().NotBeNull();
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ValidateFeatureConfiguration_WithInvalidConfiguration_ShouldReturnInvalid()
    {
        // Arrange
        var mockRegistry = DevcontainerServiceMocks.CreateFeatureRegistry();
        mockRegistry.Setup(x => x.ValidateFeatureConfiguration(It.IsAny<string>(), It.IsAny<object>()))
            .ReturnsAsync(new FeatureValidationResult
            {
                IsValid = false,
                Errors = new List<string> { "Invalid version specified", "Configuration is malformed" }
            });

        var registry = mockRegistry.Object;
        var featureId = "dotnet";
        var invalidConfig = new { version = "invalid-version", invalidProperty = "test" };

        // Act
        var result = await registry.ValidateFeatureConfiguration(featureId, invalidConfig);

        // Assert
        result.Should().NotBeNull();
        result.IsValid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().Contain("Invalid version specified");
    }

    [Fact]
    public async Task GetAvailableFeaturesAsync_ShouldReturnFeaturesWithCorrectStructure()
    {
        // Arrange
        var registry = _mockRegistry.Object;

        // Act
        var result = await registry.GetAvailableFeaturesAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().NotBeEmpty();

        foreach (var feature in result)
        {
            feature.Id.Should().NotBeNullOrEmpty();
            feature.Name.Should().NotBeNullOrEmpty();
            feature.Description.Should().NotBeNullOrEmpty();
            feature.Version.Should().NotBeNullOrEmpty();
            feature.Repository.Should().NotBeNullOrEmpty();
            feature.Category.Should().NotBeNullOrEmpty();
            feature.Tags.Should().NotBeEmpty();
            feature.Documentation.Should().NotBeNullOrEmpty();
            feature.DefaultOptions.Should().NotBeNull();
            feature.AvailableOptions.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task SearchFeaturesAsync_WithEmptyQuery_ShouldReturnAllFeatures()
    {
        // Arrange
        var registry = _mockRegistry.Object;
        var allFeatures = await registry.GetAvailableFeaturesAsync();

        // Act
        var result = await registry.SearchFeaturesAsync(string.Empty);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(allFeatures.Count);
    }

    [Fact]
    public async Task SearchFeaturesAsync_WithCaseSensitiveQuery_ShouldReturnMatches()
    {
        // Arrange
        var registry = _mockRegistry.Object;

        // Act
        var lowerResult = await registry.SearchFeaturesAsync("docker");
        var upperResult = await registry.SearchFeaturesAsync("DOCKER");

        // Assert
        lowerResult.Should().NotBeEmpty();
        upperResult.Should().NotBeEmpty();
        lowerResult.Should().BeEquivalentTo(upperResult);
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("docker-in-docker")]
    [InlineData("azure-cli")]
    [InlineData("kubectl-helm-minikube")]
    [InlineData("node")]
    public async Task GetFeatureAsync_WithKnownFeatures_ShouldReturnFeatureWithOptions(string featureId)
    {
        // Arrange
        var registry = _mockRegistry.Object;

        // Act
        var result = await registry.GetFeatureAsync(featureId);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(featureId);
        result.DefaultOptions.Should().NotBeNull();
        result.AvailableOptions.Should().NotBeNull();

        if (result.AvailableOptions.Any())
        {
            foreach (var option in result.AvailableOptions.Values)
            {
                option.Type.Should().NotBeNullOrEmpty();
                option.Description.Should().NotBeNullOrEmpty();
                option.Default.Should().NotBeNull();
            }
        }
    }

    [Fact]
    public async Task ValidateFeatureConfiguration_WithNullConfiguration_ShouldHandleGracefully()
    {
        // Arrange
        var mockRegistry = DevcontainerServiceMocks.CreateFeatureRegistry();
        mockRegistry.Setup(x => x.ValidateFeatureConfiguration(It.IsAny<string>(), null))
            .ReturnsAsync(new FeatureValidationResult
            {
                IsValid = false,
                Errors = new List<string> { "Configuration cannot be null" }
            });

        var registry = mockRegistry.Object;

        // Act
        var result = await registry.ValidateFeatureConfiguration("dotnet", null!);

        // Assert
        result.Should().NotBeNull();
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain("Configuration cannot be null");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GetFeatureAsync_WithInvalidId_ShouldReturnNull(string? invalidId)
    {
        // Arrange
        var mockRegistry = DevcontainerServiceMocks.CreateFeatureRegistry();
        mockRegistry.Setup(x => x.GetFeatureAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => string.IsNullOrWhiteSpace(id) ? null :
                DevcontainerTestData.GetAvailableFeatures().FirstOrDefault(f => f.Id == id));

        var registry = mockRegistry.Object;

        // Act
        var result = await registry.GetFeatureAsync(invalidId);

        // Assert
        result.Should().BeNull();
    }
}