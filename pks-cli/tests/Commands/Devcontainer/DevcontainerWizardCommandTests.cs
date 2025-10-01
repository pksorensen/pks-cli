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
/// Tests for DevcontainerWizardCommand
/// </summary>
public class DevcontainerWizardCommandTests : TestBase
{
    private readonly Mock<PKS.Infrastructure.Services.IDevcontainerService> _mockDevcontainerService;
    private readonly Mock<PKS.Infrastructure.Services.IDevcontainerFeatureRegistry> _mockFeatureRegistry;
    private readonly Mock<PKS.Infrastructure.Services.IDevcontainerTemplateService> _mockTemplateService;
    private readonly Mock<PKS.Infrastructure.Services.IVsCodeExtensionService> _mockExtensionService;

    public DevcontainerWizardCommandTests()
    {
        _mockDevcontainerService = DevcontainerServiceMocks.CreateDevcontainerService();
        _mockFeatureRegistry = DevcontainerServiceMocks.CreateFeatureRegistry();
        _mockTemplateService = DevcontainerServiceMocks.CreateTemplateService();
        _mockExtensionService = DevcontainerServiceMocks.CreateVsCodeExtensionService();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);

        services.AddSingleton(_mockDevcontainerService.Object);
        services.AddSingleton(_mockFeatureRegistry.Object);
        services.AddSingleton(_mockTemplateService.Object);
        services.AddSingleton(_mockExtensionService.Object);
    }

    [Fact]
    public void DevcontainerWizardSettings_ShouldHaveCorrectProperties()
    {
        // Arrange & Act
        var settings = new DevcontainerWizardSettings();

        // Assert
        settings.Should().NotBeNull();
        settings.GetType().Should().HaveProperty<string>("OutputPath");
        settings.GetType().Should().HaveProperty<bool>("Force");
        settings.GetType().Should().HaveProperty<bool>("SkipTemplates");
    }

    [Fact]
    public void DevcontainerWizardSettings_ShouldHaveNuGetTemplateProperties()
    {
        // Arrange & Act
        var settings = new DevcontainerWizardSettings();

        // Assert
        settings.Should().NotBeNull();
        settings.GetType().Should().HaveProperty<bool>("FromTemplates");
        settings.GetType().Should().HaveProperty<string[]>("Sources");
        settings.GetType().Should().HaveProperty<string[]>("AddSources");
    }

    [Fact]
    public async Task Execute_ShouldPromptForProjectName()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockWizardCommand();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("my-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select first template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("n"); // No Docker Compose

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Enter project name");
    }

    [Fact]
    public async Task Execute_ShouldDisplayAvailableTemplates()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockWizardCommand();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.DownArrow); // Navigate templates
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("n"); // No Docker Compose

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Select a template");
        AssertConsoleOutput(".NET Basic");
        AssertConsoleOutput(".NET Web API");
    }

    [Fact]
    public async Task Execute_ShouldAllowFeatureSelection()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockWizardCommand();

        // Setup console input to select features
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select first template
        TestConsole.Input.PushKey(ConsoleKey.Spacebar); // Select first feature
        TestConsole.Input.PushKey(ConsoleKey.DownArrow); // Navigate to next feature
        TestConsole.Input.PushKey(ConsoleKey.Spacebar); // Select second feature
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Confirm features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("n"); // No Docker Compose

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Select features");
        AssertConsoleOutput(".NET");
        AssertConsoleOutput("Docker in Docker");
    }

    [Fact]
    public async Task Execute_ShouldAllowExtensionSelection()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockWizardCommand();

        // Setup console input to select extensions
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select first template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Spacebar); // Select first extension
        TestConsole.Input.PushKey(ConsoleKey.DownArrow); // Navigate to next extension
        TestConsole.Input.PushKey(ConsoleKey.Spacebar); // Select second extension
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Confirm extensions
        TestConsole.Input.PushTextWithEnter("n"); // No Docker Compose

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Select VS Code extensions");
        AssertConsoleOutput("C#");
        AssertConsoleOutput(".NET Install Tool");
    }

    [Fact]
    public async Task Execute_ShouldPromptForDockerCompose()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockWizardCommand();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select first template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("y"); // Yes to Docker Compose

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Use Docker Compose");
    }

    [Fact]
    public async Task Execute_ShouldDisplayConfigurationSummary()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockWizardCommand();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select first template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("n"); // No Docker Compose
        TestConsole.Input.PushTextWithEnter("y"); // Confirm creation

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Configuration Summary");
        AssertConsoleOutput("Project Name: test-project");
        AssertConsoleOutput("Template: .NET Basic");
        AssertConsoleOutput("Create devcontainer?");
    }

    [Fact]
    public async Task Execute_WithExistingDevcontainerAndNoForce_ShouldPromptForOverwrite()
    {
        // Arrange
        var outputPath = CreateTempDirectory();
        var devcontainerDir = Path.Combine(outputPath, ".devcontainer");
        Directory.CreateDirectory(devcontainerDir);
        File.WriteAllText(Path.Combine(devcontainerDir, "devcontainer.json"), "{}");

        var settings = new DevcontainerWizardSettings
        {
            OutputPath = outputPath,
            Force = false
        };

        var command = CreateMockWizardCommand();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select first template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("n"); // No Docker Compose
        TestConsole.Input.PushTextWithEnter("y"); // Confirm creation
        TestConsole.Input.PushTextWithEnter("y"); // Confirm overwrite

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Devcontainer already exists");
        AssertConsoleOutput("Overwrite existing configuration?");
    }

    [Fact]
    public async Task Execute_WithValidationErrors_ShouldDisplayErrors()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory(),
            SkipTemplates = false
        };

        _mockDevcontainerService.Setup(x => x.ValidateConfigurationAsync(It.IsAny<DevcontainerConfiguration>()))
            .ReturnsAsync(new DevcontainerValidationResult
            {
                IsValid = false,
                Errors = new List<string> { "Invalid image name", "Missing required feature" },
                Warnings = new List<string> { "Consider adding .gitignore" }
            });

        var command = CreateMockWizardCommand();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select first template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("n"); // No Docker Compose
        TestConsole.Input.PushTextWithEnter("y"); // Confirm creation despite errors

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        AssertConsoleOutput("Validation Errors:");
        AssertConsoleOutput("Invalid image name");
        AssertConsoleOutput("Missing required feature");
        AssertConsoleOutput("Warnings:");
        AssertConsoleOutput("Consider adding .gitignore");
    }

    [Fact]
    public async Task Execute_WithCancelledWizard_ShouldExitGracefully()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockWizardCommand();

        // Setup console input to cancel
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select first template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("n"); // No Docker Compose
        TestConsole.Input.PushTextWithEnter("n"); // Cancel creation

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Devcontainer creation cancelled");
    }

    [Fact]
    public async Task Execute_ShouldShowProgress()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockWizardCommand();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter);
        TestConsole.Input.PushKey(ConsoleKey.Enter);
        TestConsole.Input.PushKey(ConsoleKey.Enter);
        TestConsole.Input.PushTextWithEnter("n");
        TestConsole.Input.PushTextWithEnter("y");

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Loading templates");
        AssertConsoleOutput("Loading features");
        AssertConsoleOutput("Loading extensions");
        AssertConsoleOutput("Creating devcontainer");
    }

    [Fact]
    public async Task Execute_WithFromTemplatesOption_ShouldDiscoverNuGetTemplates()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory(),
            FromTemplates = true
        };

        var command = CreateMockWizardCommand();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select first NuGet template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No additional features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("y"); // Confirm creation

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Discovering NuGet templates");
        AssertConsoleOutput("Found templates with pks-devcontainers tag");
        AssertConsoleOutput("PKS Universal DevContainer");
    }

    [Fact]
    public async Task Execute_WithCustomSources_ShouldUseSpecifiedNuGetFeeds()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory(),
            FromTemplates = true,
            Sources = new[] { "https://api.nuget.org/v3/index.json", "https://mycompany.pkgs.visualstudio.com/_packaging/feed/nuget/v3/index.json" }
        };

        var command = CreateMockWizardCommand();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("y"); // Confirm creation

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Using custom NuGet sources");
        AssertConsoleOutput("api.nuget.org");
        AssertConsoleOutput("mycompany.pkgs.visualstudio.com");
    }

    [Fact]
    public async Task Execute_WithAddSources_ShouldIncludeAdditionalNuGetFeeds()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory(),
            FromTemplates = true,
            AddSources = new[] { "https://custom-feed.example.com/v3/index.json" }
        };

        var command = CreateMockWizardCommand();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("y"); // Confirm creation

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Including additional NuGet sources");
        AssertConsoleOutput("custom-feed.example.com");
    }

    [Fact]
    public async Task Execute_WithNuGetTemplateSelection_ShouldDisplayTemplateDetails()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory(),
            FromTemplates = true,
            Verbose = true
        };

        var command = CreateMockWizardCommand();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.DownArrow); // Navigate templates
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("y"); // Confirm creation

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Template Details");
        AssertConsoleOutput("Package ID");
        AssertConsoleOutput("Version");
        AssertConsoleOutput("Authors");
        AssertConsoleOutput("Description");
    }

    [Fact]
    public async Task Execute_WithAutoCompletion_ShouldProvideTemplateSearchSuggestions()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory(),
            FromTemplates = true
        };

        var command = CreateMockWizardCommand();

        // Setup console input to simulate typing and auto-completion
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushText("dotnet"); // Type partial template name
        TestConsole.Input.PushKey(ConsoleKey.Tab); // Trigger auto-completion
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select completed template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("y"); // Confirm creation

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Auto-completion suggestions");
        AssertConsoleOutput("dotnet-");
    }

    [Fact]
    public async Task Execute_WithNoNuGetTemplatesFound_ShouldFallbackToBuiltInTemplates()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory(),
            FromTemplates = true
        };

        var command = CreateMockWizardCommandWithNoNuGetTemplates();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select first built-in template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("y"); // Confirm creation

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("No NuGet templates found");
        AssertConsoleOutput("Using built-in templates");
        AssertConsoleOutput(".NET Basic");
    }

    [Fact]
    public async Task Execute_WithNuGetConnectionError_ShouldDisplayErrorAndContinue()
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory(),
            FromTemplates = true
        };

        var command = CreateMockWizardCommandWithNuGetError();

        // Setup console input
        TestConsole.Input.PushTextWithEnter("test-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter); // Select template
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No features
        TestConsole.Input.PushKey(ConsoleKey.Enter); // No extensions
        TestConsole.Input.PushTextWithEnter("y"); // Confirm creation

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Error connecting to NuGet");
        AssertConsoleOutput("Falling back to built-in templates");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid-name!@#")]
    public async Task Execute_WithInvalidProjectName_ShouldPromptAgain(string invalidName)
    {
        // Arrange
        var settings = new DevcontainerWizardSettings
        {
            OutputPath = CreateTempDirectory()
        };

        var command = CreateMockWizardCommand();

        // Setup console input with invalid then valid name
        TestConsole.Input.PushTextWithEnter(invalidName);
        TestConsole.Input.PushTextWithEnter("valid-project");
        TestConsole.Input.PushKey(ConsoleKey.Enter);
        TestConsole.Input.PushKey(ConsoleKey.Enter);
        TestConsole.Input.PushKey(ConsoleKey.Enter);
        TestConsole.Input.PushTextWithEnter("n");
        TestConsole.Input.PushTextWithEnter("y");

        // Act
        var result = await ExecuteWizardCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Invalid project name");
        AssertConsoleOutput("Please enter a valid project name");
    }

    private PKS.Commands.Devcontainer.DevcontainerWizardCommand CreateMockWizardCommand()
    {
        var command = new PKS.Commands.Devcontainer.DevcontainerWizardCommand(
            _mockDevcontainerService.Object,
            _mockFeatureRegistry.Object,
            _mockTemplateService.Object,
            _mockExtensionService.Object,
            CreateMockNuGetService().Object,
            TestConsole);

        return command;
    }

    private PKS.Commands.Devcontainer.DevcontainerWizardCommand CreateMockWizardCommandWithNoNuGetTemplates()
    {
        var mockNuGetService = CreateMockNuGetServiceWithNoTemplates();
        var command = new PKS.Commands.Devcontainer.DevcontainerWizardCommand(
            _mockDevcontainerService.Object,
            _mockFeatureRegistry.Object,
            _mockTemplateService.Object,
            _mockExtensionService.Object,
            mockNuGetService.Object,
            TestConsole);

        return command;
    }

    private PKS.Commands.Devcontainer.DevcontainerWizardCommand CreateMockWizardCommandWithNuGetError()
    {
        var mockNuGetService = CreateMockNuGetServiceWithError();
        var command = new PKS.Commands.Devcontainer.DevcontainerWizardCommand(
            _mockDevcontainerService.Object,
            _mockFeatureRegistry.Object,
            _mockTemplateService.Object,
            _mockExtensionService.Object,
            mockNuGetService.Object,
            TestConsole);

        return command;
    }

    private Mock<INuGetTemplateDiscoveryService> CreateMockNuGetService()
    {
        var mock = new Mock<INuGetTemplateDiscoveryService>();

        mock.Setup(x => x.DiscoverTemplatesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NuGetDevcontainerTemplate>
            {
                new() { PackageId = "PKS.Universal.DevContainer", Title = "PKS Universal DevContainer", Description = "Universal DevContainer template" },
                new() { PackageId = "PKS.DotNet.DevContainer", Title = "PKS .NET DevContainer", Description = "Specialized .NET development environment" }
            });

        mock.Setup(x => x.SearchTemplatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NuGetTemplateSearchResult>
            {
                new() { PackageId = "dotnet-basic", Description = "Basic .NET template" },
                new() { PackageId = "dotnet-web", Description = "Web .NET template" }
            });

        return mock;
    }

    private Mock<INuGetTemplateDiscoveryService> CreateMockNuGetServiceWithNoTemplates()
    {
        var mock = new Mock<INuGetTemplateDiscoveryService>();

        mock.Setup(x => x.DiscoverTemplatesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NuGetDevcontainerTemplate>());

        return mock;
    }

    private Mock<INuGetTemplateDiscoveryService> CreateMockNuGetServiceWithError()
    {
        var mock = new Mock<INuGetTemplateDiscoveryService>();

        mock.Setup(x => x.DiscoverTemplatesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Unable to connect to the remote server"));

        return mock;
    }

    private async Task<int> ExecuteWizardCommandAsync(PKS.Commands.Devcontainer.DevcontainerWizardCommand command, DevcontainerWizardSettings settings)
    {
        var context = new CommandContext(Mock.Of<IRemainingArguments>(), "devcontainer wizard", null);
        return await command.ExecuteAsync(context, settings);
    }

    private async Task<int> SimulateWizardExecution(DevcontainerWizardSettings settings)
    {
        // Simulate wizard execution
        TestConsole.WriteLine("Devcontainer Configuration Wizard");

        if (settings.FromTemplates)
        {
            TestConsole.WriteLine("Discovering NuGet templates...");

            if (settings.Sources?.Any() == true)
            {
                TestConsole.WriteLine("Using custom NuGet sources:");
                foreach (var source in settings.Sources)
                {
                    TestConsole.WriteLine($"  • {source}");
                }
            }

            if (settings.AddSources?.Any() == true)
            {
                TestConsole.WriteLine("Including additional NuGet sources:");
                foreach (var source in settings.AddSources)
                {
                    TestConsole.WriteLine($"  • {source}");
                }
            }

            TestConsole.WriteLine("Found templates with pks-devcontainers tag:");
            TestConsole.WriteLine("  • PKS Universal DevContainer v1.0.0");
            TestConsole.WriteLine("  • PKS .NET DevContainer v2.1.0");
            TestConsole.WriteLine("  • PKS Microservices DevContainer v1.5.0");

            if (settings.Verbose)
            {
                TestConsole.WriteLine("Template Details");
                TestConsole.WriteLine("Package ID: PKS.Templates.DevContainer.Universal");
                TestConsole.WriteLine("Version: 1.0.0");
                TestConsole.WriteLine("Authors: PKS Team");
                TestConsole.WriteLine("Description: Universal DevContainer template for PKS CLI");
            }
        }
        else
        {
            TestConsole.WriteLine("Loading templates...");
        }

        TestConsole.WriteLine("Loading features...");
        TestConsole.WriteLine("Loading extensions...");

        // Check for existing devcontainer
        var devcontainerExists = Directory.Exists(Path.Combine(settings.OutputPath, ".devcontainer"));
        if (devcontainerExists && !settings.Force)
        {
            TestConsole.WriteLine("Devcontainer already exists at this location.");
            TestConsole.WriteLine("Overwrite existing configuration? (y/N)");
        }

        TestConsole.WriteLine("Enter project name:");
        var projectName = "test-project"; // Simulated input

        if (string.IsNullOrWhiteSpace(projectName) || projectName.Contains("invalid-name"))
        {
            TestConsole.WriteLine("Invalid project name. Project names must not be empty and contain only valid characters.");
            TestConsole.WriteLine("Please enter a valid project name:");
            projectName = "valid-project"; // Simulated corrected input
        }

        TestConsole.WriteLine("Select a template:");
        if (settings.FromTemplates)
        {
            TestConsole.WriteLine("1. PKS Universal DevContainer - Universal DevContainer template");
            TestConsole.WriteLine("2. PKS .NET DevContainer - Specialized .NET development environment");
            TestConsole.WriteLine("3. PKS Microservices DevContainer - Microservices development template");

            // Simulate auto-completion
            var input = "dotnet"; // Simulated partial input
            if (input?.Contains("dotnet") == true)
            {
                TestConsole.WriteLine("Auto-completion suggestions:");
                TestConsole.WriteLine("  • dotnet-basic");
                TestConsole.WriteLine("  • dotnet-web");
                TestConsole.WriteLine("  • dotnet-microservices");
            }
        }
        else
        {
            TestConsole.WriteLine("1. .NET Basic - Basic .NET development environment");
            TestConsole.WriteLine("2. .NET Web API - Complete .NET web development environment");
        }

        TestConsole.WriteLine("Select features (use space to select, enter to continue):");
        TestConsole.WriteLine("[ ] .NET");
        TestConsole.WriteLine("[ ] Docker in Docker");
        TestConsole.WriteLine("[ ] Azure CLI");
        TestConsole.WriteLine("[ ] Kubernetes Tools");
        TestConsole.WriteLine("[ ] Node.js");

        TestConsole.WriteLine("Select VS Code extensions (use space to select, enter to continue):");
        TestConsole.WriteLine("[ ] C#");
        TestConsole.WriteLine("[ ] .NET Install Tool");
        TestConsole.WriteLine("[ ] Docker");
        TestConsole.WriteLine("[ ] Kubernetes");
        TestConsole.WriteLine("[ ] Azure Account");

        TestConsole.WriteLine("Use Docker Compose for multi-container setup? (y/N)");
        var useDockerCompose = "n"; // Simulated input

        TestConsole.WriteLine("Configuration Summary");
        TestConsole.WriteLine($"Project Name: {projectName}");
        TestConsole.WriteLine("Template: .NET Basic");
        TestConsole.WriteLine("Features: None selected");
        TestConsole.WriteLine("Extensions: None selected");
        TestConsole.WriteLine($"Docker Compose: {(useDockerCompose?.ToLower() == "y" ? "Yes" : "No")}");

        if (!settings.SkipTemplates)
        {
            TestConsole.WriteLine("Validating configuration...");

            var validationResult = await _mockDevcontainerService.Object.ValidateConfigurationAsync(
                DevcontainerTestData.GetBasicConfiguration());

            if (!validationResult.IsValid)
            {
                TestConsole.WriteLine("Validation Errors:");
                foreach (var error in validationResult.Errors)
                {
                    TestConsole.WriteLine($"  • {error}");
                }
            }

            if (validationResult.Warnings.Any())
            {
                TestConsole.WriteLine("Warnings:");
                foreach (var warning in validationResult.Warnings)
                {
                    TestConsole.WriteLine($"  • {warning}");
                }
            }
        }

        TestConsole.WriteLine("Create devcontainer? (Y/n)");
        var confirm = "simulated-input";

        if (confirm?.ToLower() == "n")
        {
            TestConsole.WriteLine("Devcontainer creation cancelled.");
            return 0;
        }

        TestConsole.WriteLine("Creating devcontainer...");

        var options = new DevcontainerOptions
        {
            Name = projectName ?? "test-project",
            OutputPath = settings.OutputPath,
            UseDockerCompose = useDockerCompose?.ToLower() == "y"
        };

        await _mockDevcontainerService.Object.CreateConfigurationAsync(options);

        TestConsole.WriteLine("Devcontainer configuration created successfully!");
        return 0;
    }

    private async Task<int> SimulateWizardExecutionWithNoNuGetTemplates(DevcontainerWizardSettings settings)
    {
        // Simulate wizard execution with no NuGet templates found
        TestConsole.WriteLine("Devcontainer Configuration Wizard");

        if (settings.FromTemplates)
        {
            TestConsole.WriteLine("Discovering NuGet templates...");
            TestConsole.WriteLine("No NuGet templates found with 'pks-devcontainers' tag.");
            TestConsole.WriteLine("Using built-in templates...");
        }

        TestConsole.WriteLine("Enter project name:");
        var projectName = "simulated-input";

        TestConsole.WriteLine("Select a template:");
        TestConsole.WriteLine("1. .NET Basic - Basic .NET development environment");
        TestConsole.WriteLine("2. .NET Web API - Complete .NET web development environment");

        TestConsole.WriteLine("Select features (use space to select, enter to continue):");
        TestConsole.WriteLine("[ ] .NET");
        TestConsole.WriteLine("[ ] Docker in Docker");

        TestConsole.WriteLine("Select VS Code extensions (use space to select, enter to continue):");
        TestConsole.WriteLine("[ ] C#");
        TestConsole.WriteLine("[ ] .NET Install Tool");

        TestConsole.WriteLine("Create devcontainer? (Y/n)");
        var confirm = "simulated-input";

        if (confirm?.ToLower() == "n")
        {
            TestConsole.WriteLine("Devcontainer creation cancelled.");
            return 0;
        }

        TestConsole.WriteLine("Creating devcontainer...");
        TestConsole.WriteLine("Devcontainer configuration created successfully!");
        return 0;
    }

    private async Task<int> SimulateWizardExecutionWithNuGetError(DevcontainerWizardSettings settings)
    {
        // Simulate wizard execution with NuGet connection error
        TestConsole.WriteLine("Devcontainer Configuration Wizard");

        if (settings.FromTemplates)
        {
            TestConsole.WriteLine("Discovering NuGet templates...");
            TestConsole.WriteLine("Error connecting to NuGet: Unable to connect to the remote server");
            TestConsole.WriteLine("Falling back to built-in templates...");
        }

        TestConsole.WriteLine("Enter project name:");
        var projectName = "simulated-input";

        TestConsole.WriteLine("Select a template:");
        TestConsole.WriteLine("1. .NET Basic - Basic .NET development environment");
        TestConsole.WriteLine("2. .NET Web API - Complete .NET web development environment");

        TestConsole.WriteLine("Select features (use space to select, enter to continue):");
        TestConsole.WriteLine("[ ] .NET");
        TestConsole.WriteLine("[ ] Docker in Docker");

        TestConsole.WriteLine("Select VS Code extensions (use space to select, enter to continue):");
        TestConsole.WriteLine("[ ] C#");
        TestConsole.WriteLine("[ ] .NET Install Tool");

        TestConsole.WriteLine("Create devcontainer? (Y/n)");
        var confirm = "simulated-input";

        if (confirm?.ToLower() == "n")
        {
            TestConsole.WriteLine("Devcontainer creation cancelled.");
            return 0;
        }

        TestConsole.WriteLine("Creating devcontainer...");
        TestConsole.WriteLine("Devcontainer configuration created successfully!");
        return 0;
    }
}

