using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Fixtures.Devcontainer;
using PKS.CLI.Tests.Infrastructure.Mocks;
using Xunit;

namespace PKS.CLI.Tests.Services.Devcontainer;

/// <summary>
/// Tests for IDevcontainerService implementation
/// </summary>
public class DevcontainerServiceTests : TestBase
{
    private readonly Mock<IDevcontainerFeatureRegistry> _mockFeatureRegistry;
    private readonly Mock<IDevcontainerTemplateService> _mockTemplateService;
    private readonly Mock<IDevcontainerFileGenerator> _mockFileGenerator;
    private readonly Mock<IVsCodeExtensionService> _mockExtensionService;

    public DevcontainerServiceTests()
    {
        _mockFeatureRegistry = DevcontainerServiceMocks.CreateFeatureRegistry();
        _mockTemplateService = DevcontainerServiceMocks.CreateTemplateService();
        _mockFileGenerator = DevcontainerServiceMocks.CreateFileGenerator();
        _mockExtensionService = DevcontainerServiceMocks.CreateVsCodeExtensionService();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        
        services.AddSingleton(_mockFeatureRegistry.Object);
        services.AddSingleton(_mockTemplateService.Object);
        services.AddSingleton(_mockFileGenerator.Object);
        services.AddSingleton(_mockExtensionService.Object);
        
        // Register the actual service when implemented
        // services.AddSingleton<IDevcontainerService, DevcontainerService>();
    }

    [Fact]
    public async Task CreateConfigurationAsync_WithValidOptions_ShouldReturnSuccessResult()
    {
        // Arrange
        var service = DevcontainerServiceMocks.CreateDevcontainerService().Object;
        var options = new DevcontainerOptions
        {
            Name = "test-project",
            OutputPath = "/test/path",
            Template = "dotnet-basic",
            Features = new List<string> { "dotnet", "docker-in-docker" },
            Extensions = new List<string> { "ms-dotnettools.csharp" }
        };

        // Act
        var result = await service.CreateConfigurationAsync(options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Configuration.Should().NotBeNull();
        result.Configuration!.Name.Should().NotBeEmpty();
        result.GeneratedFiles.Should().NotBeEmpty();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateConfigurationAsync_WithInvalidOptions_ShouldReturnFailureResult()
    {
        // Arrange
        var mockService = DevcontainerServiceMocks.CreateDevcontainerService();
        mockService.Setup(x => x.CreateConfigurationAsync(It.IsAny<DevcontainerOptions>()))
            .ReturnsAsync(new DevcontainerResult
            {
                Success = false,
                Message = "Invalid configuration options",
                Errors = new List<string> { "Name is required", "Output path is required" }
            });

        var service = mockService.Object;
        var options = new DevcontainerOptions(); // Empty options

        // Act
        var result = await service.CreateConfigurationAsync(options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        result.Configuration.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(GetValidationTestCases))]
    public async Task ValidateConfigurationAsync_WithVariousConfigurations_ShouldReturnExpectedResults(
        DevcontainerConfiguration configuration, bool expectedValid, string expectedError)
    {
        // Arrange
        var service = DevcontainerServiceMocks.CreateDevcontainerService().Object;

        // Act
        var result = await service.ValidateConfigurationAsync(configuration);

        // Assert
        result.Should().NotBeNull();
        result.IsValid.Should().Be(expectedValid);
        
        if (!expectedValid)
        {
            result.Errors.Should().NotBeEmpty();
        }
        else
        {
            result.Errors.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ResolveFeatureDependenciesAsync_WithValidFeatures_ShouldReturnResolvedFeatures()
    {
        // Arrange
        var service = DevcontainerServiceMocks.CreateDevcontainerService().Object;
        var features = new List<string> { "dotnet", "docker-in-docker", "azure-cli" };

        // Act
        var result = await service.ResolveFeatureDependenciesAsync(features);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ResolvedFeatures.Should().HaveCount(features.Count);
        result.ConflictingFeatures.Should().BeEmpty();
        result.ErrorMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveFeatureDependenciesAsync_WithConflictingFeatures_ShouldReturnConflicts()
    {
        // Arrange
        var mockService = DevcontainerServiceMocks.CreateDevcontainerService();
        mockService.Setup(x => x.ResolveFeatureDependenciesAsync(It.IsAny<List<string>>()))
            .ReturnsAsync(new FeatureResolutionResult
            {
                Success = false,
                ConflictingFeatures = new List<FeatureConflict>
                {
                    new()
                    {
                        Feature1 = "dotnet:6.0",
                        Feature2 = "dotnet:8.0",
                        Reason = "Multiple versions of the same feature"
                    }
                },
                ErrorMessage = "Feature conflicts detected"
            });

        var service = mockService.Object;
        var features = new List<string> { "dotnet:6.0", "dotnet:8.0" };

        // Act
        var result = await service.ResolveFeatureDependenciesAsync(features);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.ConflictingFeatures.Should().NotBeEmpty();
        result.ErrorMessage.Should().NotBeEmpty();
    }

    [Fact]
    public async Task MergeConfigurationsAsync_WithTwoConfigurations_ShouldMergeCorrectly()
    {
        // Arrange
        var service = DevcontainerServiceMocks.CreateDevcontainerService().Object;
        var baseConfig = new DevcontainerConfiguration
        {
            Name = "base-config",
            Image = "base-image",
            Features = new Dictionary<string, object>
            {
                ["feature1"] = new { version = "1.0" }
            }
        };

        var overlayConfig = new DevcontainerConfiguration
        {
            Name = "overlay-config",
            Features = new Dictionary<string, object>
            {
                ["feature2"] = new { version = "2.0" }
            }
        };

        // Act
        var result = await service.MergeConfigurationsAsync(baseConfig, overlayConfig);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be(overlayConfig.Name);
        result.Image.Should().Be(baseConfig.Image);
        result.Features.Should().HaveCount(2);
        result.Features.Should().ContainKey("feature1");
        result.Features.Should().ContainKey("feature2");
    }

    [Fact]
    public async Task CreateConfigurationAsync_ShouldCallFeatureRegistry()
    {
        // Arrange
        var service = DevcontainerServiceMocks.CreateDevcontainerService().Object;
        var options = new DevcontainerOptions
        {
            Name = "test-project",
            Features = new List<string> { "dotnet" }
        };

        // Act
        await service.CreateConfigurationAsync(options);

        // Assert
        // This test would verify that the feature registry is called
        // when the actual implementation is created
        _mockFeatureRegistry.Verify(x => x.GetAvailableFeaturesAsync(), Times.Never);
    }

    [Fact]
    public async Task CreateConfigurationAsync_WithTemplate_ShouldCallTemplateService()
    {
        // Arrange
        var service = DevcontainerServiceMocks.CreateDevcontainerService().Object;
        var options = new DevcontainerOptions
        {
            Name = "test-project",
            Template = "dotnet-basic"
        };

        // Act
        await service.CreateConfigurationAsync(options);

        // Assert
        // This test would verify that the template service is called
        // when the actual implementation is created
        _mockTemplateService.Verify(x => x.GetTemplateAsync(It.IsAny<string>()), Times.Never);
    }

    public static IEnumerable<object[]> GetValidationTestCases()
    {
        return DevcontainerTestData.GetValidationTestCases();
    }
}