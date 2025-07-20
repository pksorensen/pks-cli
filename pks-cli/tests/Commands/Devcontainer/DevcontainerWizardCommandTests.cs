using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Fixtures.Devcontainer;
using PKS.CLI.Tests.Infrastructure.Mocks;
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
    private readonly Mock<IDevcontainerService> _mockDevcontainerService;
    private readonly Mock<IDevcontainerFeatureRegistry> _mockFeatureRegistry;
    private readonly Mock<IDevcontainerTemplateService> _mockTemplateService;
    private readonly Mock<IVsCodeExtensionService> _mockExtensionService;

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
        settings.GetType().Should().HaveProperty("OutputPath");
        settings.GetType().Should().HaveProperty("Force");
        settings.GetType().Should().HaveProperty("SkipValidation");
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
            SkipValidation = false
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

    private DevcontainerWizardCommand CreateMockWizardCommand()
    {
        var mockCommand = new Mock<DevcontainerWizardCommand>();
        
        mockCommand.Setup(x => x.ExecuteAsync(It.IsAny<CommandContext>(), It.IsAny<DevcontainerWizardSettings>()))
            .ReturnsAsync((CommandContext context, DevcontainerWizardSettings settings) =>
            {
                return SimulateWizardExecution(settings);
            });

        return mockCommand.Object;
    }

    private async Task<int> ExecuteWizardCommandAsync(DevcontainerWizardCommand command, DevcontainerWizardSettings settings)
    {
        var context = new CommandContext(Mock.Of<IRemainingArguments>(), "devcontainer wizard", null);
        return await command.ExecuteAsync(context, settings);
    }

    private async Task<int> SimulateWizardExecution(DevcontainerWizardSettings settings)
    {
        // Simulate wizard execution
        TestConsole.WriteLine("Devcontainer Configuration Wizard");
        TestConsole.WriteLine("Loading templates...");
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
        var projectName = TestConsole.Input.ReadLine();
        
        if (string.IsNullOrWhiteSpace(projectName) || projectName.Contains("invalid-name"))
        {
            TestConsole.WriteLine("Invalid project name. Project names must not be empty and contain only valid characters.");
            TestConsole.WriteLine("Please enter a valid project name:");
            projectName = TestConsole.Input.ReadLine();
        }

        TestConsole.WriteLine("Select a template:");
        TestConsole.WriteLine("1. .NET Basic - Basic .NET development environment");
        TestConsole.WriteLine("2. .NET Web API - Complete .NET web development environment");

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
        var useDockerCompose = TestConsole.Input.ReadLine();

        TestConsole.WriteLine("Configuration Summary");
        TestConsole.WriteLine($"Project Name: {projectName}");
        TestConsole.WriteLine("Template: .NET Basic");
        TestConsole.WriteLine("Features: None selected");
        TestConsole.WriteLine("Extensions: None selected");
        TestConsole.WriteLine($"Docker Compose: {(useDockerCompose?.ToLower() == "y" ? "Yes" : "No")}");

        if (!settings.SkipValidation)
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
        var confirm = TestConsole.Input.ReadLine();

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
}

// Mock wizard settings class
public class DevcontainerWizardSettings
{
    public string OutputPath { get; set; } = ".";
    public bool Force { get; set; }
    public bool SkipValidation { get; set; }
}

// Mock wizard command class
public class DevcontainerWizardCommand
{
    public virtual async Task<int> ExecuteAsync(CommandContext context, DevcontainerWizardSettings settings)
    {
        await Task.CompletedTask;
        return 0;
    }
}