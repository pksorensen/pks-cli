using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Fixtures.Devcontainer;
using PKS.CLI.Tests.Infrastructure.Mocks;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Commands.Devcontainer;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Devcontainer;

/// <summary>
/// Tests for DevcontainerInitCommand
/// </summary>
public class DevcontainerInitCommandTests : TestBase
{
    private Mock<IDevcontainerService> _mockDevcontainerService = null!;
    private Mock<IDevcontainerFeatureRegistry> _mockFeatureRegistry = null!;
    private Mock<IDevcontainerTemplateService> _mockTemplateService = null!;

    public DevcontainerInitCommandTests()
    {
        InitializeMocks();
    }

    private void InitializeMocks()
    {
        // Create inline mocks to avoid potential dependency issues
        _mockDevcontainerService = new Mock<IDevcontainerService>();
        _mockFeatureRegistry = new Mock<IDevcontainerFeatureRegistry>();
        _mockTemplateService = new Mock<IDevcontainerTemplateService>();

        // Setup basic devcontainer service
        _mockDevcontainerService.Setup(x => x.CreateConfigurationAsync(It.IsAny<DevcontainerOptions>()))
            .ReturnsAsync(new DevcontainerResult
            {
                Success = true,
                Message = "Devcontainer configuration created successfully",
                Configuration = new DevcontainerConfiguration
                {
                    Name = "test-devcontainer",
                    Image = "mcr.microsoft.com/dotnet/sdk:8.0"
                },
                GeneratedFiles = new List<string> { ".devcontainer/devcontainer.json" }
            });

        _mockDevcontainerService.Setup(x => x.ValidateOutputPathAsync(It.IsAny<string>()))
            .ReturnsAsync(new PathValidationResult { IsValid = true, CanWrite = true });

        _mockDevcontainerService.Setup(x => x.ResolveFeatureDependenciesAsync(It.IsAny<List<string>>()))
            .ReturnsAsync((List<string> features) => new FeatureResolutionResult
            {
                Success = true,
                ResolvedFeatures = features.Select(f => new DevcontainerFeature { Id = f, Name = f }).ToList(),
                ConflictingFeatures = new List<FeatureConflict>(),
                MissingDependencies = new List<string>()
            });

        // Setup feature registry
        _mockFeatureRegistry.Setup(x => x.GetFeatureAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => new DevcontainerFeature { Id = id, Name = id });

        // Setup template service
        _mockTemplateService.Setup(x => x.GetTemplateAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => new DevcontainerTemplate { Id = id, Name = id });
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);

        try
        {
            // Ensure mocks are initialized
            if (_mockDevcontainerService == null || _mockFeatureRegistry == null || _mockTemplateService == null)
            {
                InitializeMocks();
            }

            // Register mock services
            services.AddSingleton<IDevcontainerService>(_mockDevcontainerService.Object);
            services.AddSingleton<IDevcontainerFeatureRegistry>(_mockFeatureRegistry.Object);
            services.AddSingleton<IDevcontainerTemplateService>(_mockTemplateService.Object);

            // Register the actual command
            services.AddTransient<DevcontainerInitCommand>();
        }
        catch (Exception ex)
        {
            // Debug the exception to understand what's failing
            System.Diagnostics.Debug.WriteLine($"Error in ConfigureServices: {ex}");
            throw;
        }
    }



    [Theory]
    [InlineData("test-project", "dotnet-basic", new[] { "dotnet" }, new[] { "ms-dotnettools.csharp" })]
    [InlineData("my-app", "dotnet-web", new[] { "dotnet", "node" }, new[] { "ms-dotnettools.csharp", "ms-vscode.vscode-docker" })]
    public async Task Execute_WithValidSettings_ShouldCreateDevcontainerConfiguration(
        string name, string template, string[] features, string[] extensions)
    {
        // Arrange
        var settings = new DevcontainerInitSettings
        {
            Name = name,
            Template = template,
            Features = features,
            Extensions = extensions,
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockCommand();

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0); // Success exit code

        _mockDevcontainerService.Verify(x => x.CreateConfigurationAsync(It.Is<DevcontainerOptions>(
            opts => opts.Name == name &&
                   opts.Template == template &&
                   opts.Features.SequenceEqual(features) &&
                   opts.Extensions.SequenceEqual(extensions))), Times.Once);

        AssertConsoleOutput("Devcontainer configuration created successfully");
    }

    [Fact]
    public async Task Execute_WithMissingRequiredParameters_ShouldPromptForInput()
    {
        // Arrange
        var settings = new DevcontainerInitSettings
        {
            Interactive = true,
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockCommand();

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Interactive mode requested - launching devcontainer wizard...");
        AssertConsoleOutput("Interactive mode is available via 'pks devcontainer wizard' command");
    }

    [Fact]
    public async Task Execute_WithInvalidTemplate_ShouldReturnError()
    {
        // Arrange
        var settings = new DevcontainerInitSettings
        {
            Name = "test-project",
            Template = "nonexistent-template",
            OutputPath = CreateTempDirectory()
        };

        _mockTemplateService.Setup(x => x.GetTemplateAsync("nonexistent-template"))
            .ReturnsAsync((DevcontainerTemplate?)null);

        var command = CreateMockCommand();

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(1); // Error exit code
        AssertConsoleOutput("Template 'nonexistent-template' not found");
    }

    [Fact]
    public async Task Execute_WithInvalidFeatures_ShouldReturnError()
    {
        // Arrange
        var settings = new DevcontainerInitSettings
        {
            Name = "test-project",
            Features = new string[] { "nonexistent-feature" },
            OutputPath = CreateTempDirectory()
        };

        _mockFeatureRegistry.Setup(x => x.GetFeatureAsync("nonexistent-feature"))
            .ReturnsAsync((DevcontainerFeature?)null);

        var command = CreateMockCommand();

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(1); // Error exit code
        AssertConsoleOutput("Feature 'nonexistent-feature' not found");
    }

    [Fact]
    public async Task Execute_WithExistingDevcontainerAndNoForce_ShouldReturnError()
    {
        // Arrange
        var outputPath = CreateTempDirectory();
        var devcontainerDir = Path.Combine(outputPath, ".devcontainer");
        Directory.CreateDirectory(devcontainerDir);
        File.WriteAllText(Path.Combine(devcontainerDir, "devcontainer.json"), "{}");

        var settings = new DevcontainerInitSettings
        {
            Name = "test-project",
            OutputPath = outputPath,
            Force = false
        };

        var command = CreateMockCommand();

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(1); // Error exit code
        AssertConsoleOutput("Devcontainer already exists");
        AssertConsoleOutput("Use --force to overwrite");
    }

    [Fact]
    public async Task Execute_WithExistingDevcontainerAndForce_ShouldOverwrite()
    {
        // Arrange
        var outputPath = CreateTempDirectory();
        var devcontainerDir = Path.Combine(outputPath, ".devcontainer");
        Directory.CreateDirectory(devcontainerDir);
        File.WriteAllText(Path.Combine(devcontainerDir, "devcontainer.json"), "{}");

        var settings = new DevcontainerInitSettings
        {
            Name = "test-project",
            OutputPath = outputPath,
            Force = true
        };

        var command = CreateMockCommand();

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0); // Success exit code
        AssertConsoleOutput("Overwriting existing devcontainer");
        AssertConsoleOutput("Devcontainer configuration created successfully");
    }

    [Fact]
    public async Task Execute_WithDockerCompose_ShouldGenerateDockerComposeFiles()
    {
        // Arrange
        var settings = new DevcontainerInitSettings
        {
            Name = "test-project",
            UseDockerCompose = true,
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockCommand();

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);

        _mockDevcontainerService.Verify(x => x.CreateConfigurationAsync(It.Is<DevcontainerOptions>(
            opts => opts.UseDockerCompose == true)), Times.Once);

        AssertConsoleOutput("Docker Compose configuration generated");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Execute_WithInvalidName_ShouldReturnError(string? invalidName)
    {
        // Arrange
        var settings = new DevcontainerInitSettings
        {
            Name = invalidName,
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockCommand();

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(1); // Error exit code
        AssertConsoleOutput("Project name is required");
    }

    [Fact]
    public async Task Execute_WithReadOnlyOutputPath_ShouldReturnError()
    {
        // Arrange - Use an existing temporary directory for this test
        var existingPath = CreateTempDirectory();
        var settings = new DevcontainerInitSettings
        {
            Name = "test-project",
            OutputPath = existingPath
        };

        // Setup path validation to fail for this specific path (simulating read-only)
        _mockDevcontainerService.Setup(x => x.ValidateOutputPathAsync(existingPath))
            .ReturnsAsync(new PathValidationResult 
            { 
                IsValid = false, 
                CanWrite = false, 
                Errors = new List<string> { "Output path is read-only" }
            });

        var command = CreateMockCommand();

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(1); // Error exit code
        AssertConsoleOutput("Invalid output path:");
        AssertConsoleOutput("Output path is read-only");
    }

    [Fact]
    public async Task Execute_ShouldDisplayProgress()
    {
        // Arrange
        var settings = new DevcontainerInitSettings
        {
            Name = "test-project",
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockCommand();

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Creating devcontainer configuration");
        AssertConsoleOutput("Generating configuration files");
        AssertConsoleOutput("Devcontainer configuration created successfully");
    }

    private PKS.Commands.Devcontainer.DevcontainerInitCommand CreateMockCommand()
    {
        // Create actual command instance with mocked dependencies
        var command = new PKS.Commands.Devcontainer.DevcontainerInitCommand(
            _mockDevcontainerService.Object,
            _mockFeatureRegistry.Object,
            _mockTemplateService.Object,
            TestConsole);

        return command;
    }

    private async Task<int> ExecuteCommandAsync(PKS.Commands.Devcontainer.DevcontainerInitCommand command, DevcontainerInitSettings settings)
    {
        var context = new CommandContext(Mock.Of<IRemainingArguments>(), "devcontainer", null);
        return await command.ExecuteAsync(context, settings);
    }

    private async Task<int> SimulateCommandExecution(DevcontainerInitSettings settings)
    {
        // Simulate the actual command execution logic
        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            TestConsole.WriteLine("Project name is required");
            return 1;
        }

        if (settings.Interactive)
        {
            TestConsole.WriteLine("Enter project name:");
            TestConsole.WriteLine("Select template:");
        }

        if (!string.IsNullOrEmpty(settings.Template) && settings.Template == "nonexistent-template")
        {
            TestConsole.WriteLine($"Template '{settings.Template}' not found");
            return 1;
        }

        if (settings.Features.Contains("nonexistent-feature"))
        {
            TestConsole.WriteLine("Feature 'nonexistent-feature' not found");
            return 1;
        }

        var devcontainerExists = Directory.Exists(Path.Combine(settings.OutputPath, ".devcontainer"));
        if (devcontainerExists && !settings.Force)
        {
            TestConsole.WriteLine("Devcontainer already exists");
            TestConsole.WriteLine("Use --force to overwrite");
            return 1;
        }

        if (devcontainerExists && settings.Force)
        {
            TestConsole.WriteLine("Overwriting existing devcontainer");
        }

        if (settings.OutputPath == "/readonly/path")
        {
            TestConsole.WriteLine("Cannot write to output path");
            return 1;
        }

        TestConsole.WriteLine("Creating devcontainer configuration");
        TestConsole.WriteLine("Generating configuration files");

        if (settings.UseDockerCompose)
        {
            TestConsole.WriteLine("Docker Compose configuration generated");
        }

        // Simulate service call
        var options = new DevcontainerOptions
        {
            Name = settings.Name,
            Template = settings.Template,
            Features = settings.Features?.ToList() ?? new List<string>(),
            Extensions = settings.Extensions?.ToList() ?? new List<string>(),
            OutputPath = settings.OutputPath,
            UseDockerCompose = settings.UseDockerCompose
        };

        await _mockDevcontainerService.Object.CreateConfigurationAsync(options);

        TestConsole.WriteLine("Devcontainer configuration created successfully");
        return 0;
    }
}



