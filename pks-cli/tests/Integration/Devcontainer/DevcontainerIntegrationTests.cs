using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Fixtures.Devcontainer;
using PKS.CLI.Tests.Infrastructure.Mocks;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Integration.Devcontainer;

/// <summary>
/// Integration tests for complete devcontainer workflows
/// </summary>
public class DevcontainerIntegrationTests : TestBase
{
    private readonly Mock<IDevcontainerService> _mockDevcontainerService;
    private readonly Mock<IDevcontainerFeatureRegistry> _mockFeatureRegistry;
    private readonly Mock<IDevcontainerTemplateService> _mockTemplateService;
    private readonly Mock<IDevcontainerFileGenerator> _mockFileGenerator;
    private readonly Mock<IVsCodeExtensionService> _mockExtensionService;

    public DevcontainerIntegrationTests()
    {
        _mockDevcontainerService = DevcontainerServiceMocks.CreateDevcontainerService();
        _mockFeatureRegistry = DevcontainerServiceMocks.CreateFeatureRegistry();
        _mockTemplateService = DevcontainerServiceMocks.CreateTemplateService();
        _mockFileGenerator = DevcontainerServiceMocks.CreateFileGenerator();
        _mockExtensionService = DevcontainerServiceMocks.CreateVsCodeExtensionService();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        
        services.AddSingleton(_mockDevcontainerService.Object);
        services.AddSingleton(_mockFeatureRegistry.Object);
        services.AddSingleton(_mockTemplateService.Object);
        services.AddSingleton(_mockFileGenerator.Object);
        services.AddSingleton(_mockExtensionService.Object);
    }

    [Fact]
    public async Task CompleteWorkflow_BasicDevcontainerCreation_ShouldSucceed()
    {
        // Arrange
        var projectName = "test-integration-project";
        var outputPath = CreateTempDirectory();
        var options = new DevcontainerOptions
        {
            Name = projectName,
            OutputPath = outputPath,
            Template = "dotnet-basic",
            Features = new List<string> { "dotnet" },
            Extensions = new List<string> { "ms-dotnettools.csharp" }
        };

        // Setup complete workflow mocks
        SetupCompleteWorkflowMocks(options);

        // Act
        var result = await ExecuteCompleteWorkflow(options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Configuration.Should().NotBeNull();
        result.GeneratedFiles.Should().NotBeEmpty();
        result.GeneratedFiles.Should().Contain(f => f.EndsWith("devcontainer.json"));
        result.GeneratedFiles.Should().Contain(f => f.EndsWith("Dockerfile"));

        // Verify service interactions
        _mockTemplateService.Verify(x => x.GetTemplateAsync("dotnet-basic"), Times.Once);
        _mockFeatureRegistry.Verify(x => x.GetFeatureAsync("dotnet"), Times.Once);
        _mockExtensionService.Verify(x => x.ValidateExtensionAsync("ms-dotnettools.csharp"), Times.Once);
        _mockDevcontainerService.Verify(x => x.CreateConfigurationAsync(It.IsAny<DevcontainerOptions>()), Times.Once);
    }

    [Fact]
    public async Task CompleteWorkflow_WithDockerCompose_ShouldGenerateAllFiles()
    {
        // Arrange
        var projectName = "docker-compose-project";
        var outputPath = CreateTempDirectory();
        var options = new DevcontainerOptions
        {
            Name = projectName,
            OutputPath = outputPath,
            Template = "dotnet-web",
            Features = new List<string> { "dotnet", "docker-in-docker" },
            UseDockerCompose = true
        };

        // Setup workflow mocks with Docker Compose
        SetupCompleteWorkflowMocks(options);
        _mockDevcontainerService.Setup(x => x.CreateConfigurationAsync(It.IsAny<DevcontainerOptions>()))
            .ReturnsAsync(new DevcontainerResult
            {
                Success = true,
                Configuration = DevcontainerTestData.GetComplexConfiguration(),
                GeneratedFiles = new List<string>
                {
                    Path.Combine(outputPath, ".devcontainer", "devcontainer.json"),
                    Path.Combine(outputPath, ".devcontainer", "Dockerfile"),
                    Path.Combine(outputPath, ".devcontainer", "docker-compose.yml")
                }
            });

        // Act
        var result = await ExecuteCompleteWorkflow(options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.GeneratedFiles.Should().HaveCount(3);
        result.GeneratedFiles.Should().Contain(f => f.EndsWith("docker-compose.yml"));

        // Verify Docker Compose generation
        _mockFileGenerator.Verify(x => x.GenerateDockerComposeAsync(It.IsAny<DevcontainerConfiguration>(), outputPath), Times.Once);
    }

    [Fact]
    public async Task CompleteWorkflow_WithFeatureDependencyResolution_ShouldResolveCorrectly()
    {
        // Arrange
        var options = new DevcontainerOptions
        {
            Name = "dependency-test",
            OutputPath = CreateTempDirectory(),
            Features = new List<string> { "dotnet", "docker-in-docker", "azure-cli" }
        };

        // Setup feature dependency resolution
        var resolvedFeatures = DevcontainerTestData.GetAvailableFeatures()
            .Where(f => options.Features.Contains(f.Id)).ToList();

        _mockDevcontainerService.Setup(x => x.ResolveFeatureDependenciesAsync(options.Features))
            .ReturnsAsync(new FeatureResolutionResult
            {
                Success = true,
                ResolvedFeatures = resolvedFeatures,
                ConflictingFeatures = new List<FeatureConflict>()
            });

        SetupCompleteWorkflowMocks(options);

        // Act
        var dependencyResult = await _mockDevcontainerService.Object.ResolveFeatureDependenciesAsync(options.Features);
        var workflowResult = await ExecuteCompleteWorkflow(options);

        // Assert
        dependencyResult.Success.Should().BeTrue();
        dependencyResult.ResolvedFeatures.Should().HaveCount(3);
        dependencyResult.ConflictingFeatures.Should().BeEmpty();

        workflowResult.Success.Should().BeTrue();
        workflowResult.Configuration!.Features.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CompleteWorkflow_WithFeatureConflicts_ShouldHandleGracefully()
    {
        // Arrange
        var options = new DevcontainerOptions
        {
            Name = "conflict-test",
            OutputPath = CreateTempDirectory(),
            Features = new List<string> { "conflicting-feature-1", "conflicting-feature-2" }
        };

        // Setup feature conflict scenario
        _mockDevcontainerService.Setup(x => x.ResolveFeatureDependenciesAsync(options.Features))
            .ReturnsAsync(new FeatureResolutionResult
            {
                Success = false,
                ConflictingFeatures = new List<FeatureConflict>
                {
                    new()
                    {
                        Feature1 = "conflicting-feature-1",
                        Feature2 = "conflicting-feature-2",
                        Reason = "These features cannot be used together"
                    }
                },
                ErrorMessage = "Feature conflicts detected"
            });

        _mockDevcontainerService.Setup(x => x.CreateConfigurationAsync(It.IsAny<DevcontainerOptions>()))
            .ReturnsAsync(new DevcontainerResult
            {
                Success = false,
                Errors = new List<string> { "Cannot resolve feature dependencies" }
            });

        // Act
        var result = await ExecuteCompleteWorkflow(options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().Contain("Cannot resolve feature dependencies");
    }

    [Fact]
    public async Task CompleteWorkflow_WithInvalidTemplate_ShouldFailGracefully()
    {
        // Arrange
        var options = new DevcontainerOptions
        {
            Name = "invalid-template-test",
            OutputPath = CreateTempDirectory(),
            Template = "nonexistent-template"
        };

        // Setup template not found scenario
        _mockTemplateService.Setup(x => x.GetTemplateAsync("nonexistent-template"))
            .ReturnsAsync((DevcontainerTemplate?)null);

        _mockDevcontainerService.Setup(x => x.CreateConfigurationAsync(It.IsAny<DevcontainerOptions>()))
            .ReturnsAsync(new DevcontainerResult
            {
                Success = false,
                Errors = new List<string> { "Template 'nonexistent-template' not found" }
            });

        // Act
        var result = await ExecuteCompleteWorkflow(options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Errors.Should().Contain("Template 'nonexistent-template' not found");

        // Verify template service was called
        _mockTemplateService.Verify(x => x.GetTemplateAsync("nonexistent-template"), Times.Once);
    }

    [Fact]
    public async Task CompleteWorkflow_WithValidationErrors_ShouldReturnValidationResults()
    {
        // Arrange
        var options = new DevcontainerOptions
        {
            Name = "validation-test",
            OutputPath = CreateTempDirectory()
        };

        var invalidConfiguration = DevcontainerTestData.GetInvalidConfiguration();

        // Setup validation failure
        _mockDevcontainerService.Setup(x => x.ValidateConfigurationAsync(It.IsAny<DevcontainerConfiguration>()))
            .ReturnsAsync(new DevcontainerValidationResult
            {
                IsValid = false,
                Errors = new List<string> { "Name is required", "Invalid image format" },
                Warnings = new List<string> { "Consider adding .gitignore" }
            });

        _mockDevcontainerService.Setup(x => x.CreateConfigurationAsync(It.IsAny<DevcontainerOptions>()))
            .ReturnsAsync(new DevcontainerResult
            {
                Success = false,
                Configuration = invalidConfiguration,
                Errors = new List<string> { "Configuration validation failed" }
            });

        // Act
        var result = await ExecuteCompleteWorkflow(options);
        var validationResult = await _mockDevcontainerService.Object.ValidateConfigurationAsync(invalidConfiguration);

        // Assert
        result.Success.Should().BeFalse();
        validationResult.IsValid.Should().BeFalse();
        validationResult.Errors.Should().HaveCount(2);
        validationResult.Warnings.Should().HaveCount(1);
    }

    [Fact]
    public async Task CompleteWorkflow_WithFileGenerationFailure_ShouldHandleGracefully()
    {
        // Arrange
        var options = new DevcontainerOptions
        {
            Name = "file-gen-failure-test",
            OutputPath = "/readonly/path"
        };

        // Setup file generation failure
        _mockFileGenerator.Setup(x => x.ValidateOutputPathAsync("/readonly/path"))
            .ReturnsAsync(new PathValidationResult
            {
                IsValid = false,
                CanWrite = false,
                Errors = new List<string> { "Path is read-only" }
            });

        _mockDevcontainerService.Setup(x => x.CreateConfigurationAsync(It.IsAny<DevcontainerOptions>()))
            .ReturnsAsync(new DevcontainerResult
            {
                Success = false,
                Errors = new List<string> { "Cannot write to output path" }
            });

        // Act
        var result = await ExecuteCompleteWorkflow(options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Errors.Should().Contain("Cannot write to output path");
    }

    [Fact]
    public async Task CompleteWorkflow_WithExtensionValidation_ShouldValidateAllExtensions()
    {
        // Arrange
        var options = new DevcontainerOptions
        {
            Name = "extension-validation-test",
            OutputPath = CreateTempDirectory(),
            Extensions = new List<string> 
            { 
                "ms-dotnettools.csharp", 
                "ms-vscode.vscode-docker",
                "invalid-extension-id"
            }
        };

        // Setup extension validation
        _mockExtensionService.Setup(x => x.ValidateExtensionAsync("ms-dotnettools.csharp"))
            .ReturnsAsync(new ExtensionValidationResult { IsValid = true, Exists = true });
        
        _mockExtensionService.Setup(x => x.ValidateExtensionAsync("ms-vscode.vscode-docker"))
            .ReturnsAsync(new ExtensionValidationResult { IsValid = true, Exists = true });
        
        _mockExtensionService.Setup(x => x.ValidateExtensionAsync("invalid-extension-id"))
            .ReturnsAsync(new ExtensionValidationResult 
            { 
                IsValid = false, 
                Exists = false,
                ErrorMessage = "Extension not found"
            });

        SetupCompleteWorkflowMocks(options);

        // Act
        var result = await ExecuteCompleteWorkflow(options);

        // Assert
        result.Should().NotBeNull();
        
        // Verify all extensions were validated
        _mockExtensionService.Verify(x => x.ValidateExtensionAsync("ms-dotnettools.csharp"), Times.Once);
        _mockExtensionService.Verify(x => x.ValidateExtensionAsync("ms-vscode.vscode-docker"), Times.Once);
        _mockExtensionService.Verify(x => x.ValidateExtensionAsync("invalid-extension-id"), Times.Once);
    }

    [Fact]
    public async Task CompleteWorkflow_WithConfigurationMerging_ShouldMergeTemplateAndCustomSettings()
    {
        // Arrange
        var options = new DevcontainerOptions
        {
            Name = "merge-test",
            OutputPath = CreateTempDirectory(),
            Template = "dotnet-basic",
            Features = new List<string> { "azure-cli" },
            CustomSettings = new Dictionary<string, object>
            {
                ["customPort"] = 8080,
                ["enableDebugging"] = true
            }
        };

        var templateConfig = DevcontainerTestData.GetBasicConfiguration();
        var customConfig = new DevcontainerConfiguration
        {
            Name = options.Name,
            ForwardPorts = new[] { 8080 },
            RemoteEnv = new Dictionary<string, string> { ["DEBUG"] = "true" }
        };

        // Setup configuration merging
        _mockTemplateService.Setup(x => x.ApplyTemplateAsync("dotnet-basic", options))
            .ReturnsAsync(templateConfig);

        _mockDevcontainerService.Setup(x => x.MergeConfigurationsAsync(templateConfig, It.IsAny<DevcontainerConfiguration>()))
            .ReturnsAsync((DevcontainerConfiguration baseConfig, DevcontainerConfiguration overlay) =>
            {
                var merged = new DevcontainerConfiguration
                {
                    Name = overlay.Name ?? baseConfig.Name,
                    Image = overlay.Image ?? baseConfig.Image,
                    Features = baseConfig.Features.Concat(overlay.Features ?? new Dictionary<string, object>())
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    ForwardPorts = (baseConfig.ForwardPorts ?? Array.Empty<int>())
                        .Concat(overlay.ForwardPorts ?? Array.Empty<int>()).ToArray()
                };
                return merged;
            });

        SetupCompleteWorkflowMocks(options);

        // Act
        var result = await ExecuteCompleteWorkflow(options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();

        // Verify merging was called
        _mockDevcontainerService.Verify(x => x.MergeConfigurationsAsync(
            It.IsAny<DevcontainerConfiguration>(), 
            It.IsAny<DevcontainerConfiguration>()), Times.Once);
    }

    private void SetupCompleteWorkflowMocks(DevcontainerOptions options)
    {
        // Setup template service
        if (!string.IsNullOrEmpty(options.Template))
        {
            _mockTemplateService.Setup(x => x.GetTemplateAsync(options.Template))
                .ReturnsAsync(new DevcontainerTemplate
                {
                    Id = options.Template,
                    Name = "Test Template",
                    BaseImage = "mcr.microsoft.com/dotnet/sdk:8.0",
                    RequiredFeatures = new[] { "dotnet" }
                });

            _mockTemplateService.Setup(x => x.ApplyTemplateAsync(options.Template, options))
                .ReturnsAsync(DevcontainerTestData.GetBasicConfiguration());
        }

        // Setup feature registry
        foreach (var feature in options.Features)
        {
            _mockFeatureRegistry.Setup(x => x.GetFeatureAsync(feature))
                .ReturnsAsync(DevcontainerTestData.GetAvailableFeatures()
                    .FirstOrDefault(f => f.Id == feature));
        }

        // Setup extension service
        foreach (var extension in options.Extensions)
        {
            _mockExtensionService.Setup(x => x.ValidateExtensionAsync(extension))
                .ReturnsAsync(new ExtensionValidationResult 
                { 
                    IsValid = true, 
                    Exists = true 
                });
        }

        // Setup file generator
        _mockFileGenerator.Setup(x => x.ValidateOutputPathAsync(options.OutputPath))
            .ReturnsAsync(new PathValidationResult 
            { 
                IsValid = true, 
                CanWrite = true 
            });

        // Setup devcontainer service for successful creation
        _mockDevcontainerService.Setup(x => x.CreateConfigurationAsync(options))
            .ReturnsAsync(new DevcontainerResult
            {
                Success = true,
                Configuration = DevcontainerTestData.GetBasicConfiguration(),
                GeneratedFiles = new List<string>
                {
                    Path.Combine(options.OutputPath, ".devcontainer", "devcontainer.json"),
                    Path.Combine(options.OutputPath, ".devcontainer", "Dockerfile")
                }
            });
    }

    private async Task<DevcontainerResult> ExecuteCompleteWorkflow(DevcontainerOptions options)
    {
        // This simulates the complete workflow that would be orchestrated by the actual implementation
        
        // 1. Validate output path
        var pathValidation = await _mockFileGenerator.Object.ValidateOutputPathAsync(options.OutputPath);
        if (!pathValidation.IsValid || !pathValidation.CanWrite)
        {
            return new DevcontainerResult
            {
                Success = false,
                Errors = pathValidation.Errors
            };
        }

        // 2. Validate template if specified
        if (!string.IsNullOrEmpty(options.Template))
        {
            var template = await _mockTemplateService.Object.GetTemplateAsync(options.Template);
            if (template == null)
            {
                return new DevcontainerResult
                {
                    Success = false,
                    Errors = new List<string> { $"Template '{options.Template}' not found" }
                };
            }
        }

        // 3. Validate features
        foreach (var feature in options.Features)
        {
            var featureObj = await _mockFeatureRegistry.Object.GetFeatureAsync(feature);
            if (featureObj == null)
            {
                return new DevcontainerResult
                {
                    Success = false,
                    Errors = new List<string> { $"Feature '{feature}' not found" }
                };
            }
        }

        // 4. Validate extensions
        foreach (var extension in options.Extensions)
        {
            var extensionValidation = await _mockExtensionService.Object.ValidateExtensionAsync(extension);
            if (!extensionValidation.IsValid)
            {
                return new DevcontainerResult
                {
                    Success = false,
                    Errors = new List<string> { $"Extension '{extension}' is invalid: {extensionValidation.ErrorMessage}" }
                };
            }
        }

        // 5. Create configuration
        return await _mockDevcontainerService.Object.CreateConfigurationAsync(options);
    }
}