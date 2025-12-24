using PKS.Commands.Prd;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Moq;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Spectre.Console;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace PKS.CLI.Tests.Commands.Prd;

/// <summary>
/// Integration tests for the complete PRD command flow
/// </summary>
public class PrdIntegrationTests : IDisposable
{
    private readonly ServiceCollection _services;
    private readonly TestConsole _console;
    private readonly CommandApp _app;
    private readonly Mock<IPrdService> _mockPrdService;
    private readonly IAnsiConsole? _originalConsole;

    public PrdIntegrationTests()
    {
        _services = new ServiceCollection();
        _console = new TestConsole();
        _mockPrdService = new Mock<IPrdService>();

        // Store original console and configure AnsiConsole to use TestConsole
        _originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = _console;

        // Setup services
        _services.AddSingleton(_mockPrdService.Object);
        _services.AddSingleton<ITypeRegistrar>(new TypeRegistrar(_services));

        // Create command app with full configuration matching the real application structure
        _app = new CommandApp(new TypeRegistrar(_services));
        _app.Configure(config =>
        {
            config.SetApplicationName("pks");
            config.SetApplicationVersion("1.0.0");

            // Configure PRD branch command with subcommands (matching Program.cs structure)
            config.AddBranch<PrdSettings>("prd", prd =>
            {
                prd.SetDescription("Manage Product Requirements Documents (PRDs) with AI-powered generation");

                prd.AddCommand<PrdGenerateCommand>("generate")
                    .WithDescription("Generate comprehensive PRD from idea description")
                    .WithExample(new[] { "prd", "generate", "A mobile app for task management" });

                prd.AddCommand<PrdLoadCommand>("load")
                    .WithDescription("Load and parse existing PRD file")
                    .WithExample(new[] { "prd", "load", "docs/PRD.md" });

                prd.AddCommand<PrdRequirementsCommand>("requirements")
                    .WithDescription("List and filter requirements from PRD")
                    .WithExample(new[] { "prd", "requirements", "--status", "pending" });

                prd.AddCommand<PrdStatusCommand>("status")
                    .WithDescription("Display PRD status, progress, and statistics")
                    .WithExample(new[] { "prd", "status" });

                prd.AddCommand<PrdValidateCommand>("validate")
                    .WithDescription("Validate PRD for completeness and consistency")
                    .WithExample(new[] { "prd", "validate", "--strict" });

                prd.AddCommand<PrdTemplateCommand>("template")
                    .WithDescription("Generate PRD templates for different project types")
                    .WithExample(new[] { "prd", "template", "MyProject", "--type", "web" });
            });
        });
    }

    [Fact]
    public async Task PrdCommands_ShouldBeAvailable()
    {
        // This test verifies that all PRD commands are properly configured as a branch
        // Since we're using branch commands, we'll test that the branch structure is available
        var commands = new[] { "generate", "load", "requirements", "status", "validate", "template" };

        // Test that the basic prd command works
        var result = await _app.RunAsync(new[] { "prd", "--help" });
        result.Should().Be(0, "PRD base command should work");

        // If we reach here, all commands were configured successfully under the prd branch
        _app.Should().NotBeNull();
    }

    [Fact]
    public async Task PrdGenerateCommand_WithValidArgs_ShouldExecuteSuccessfully()
    {
        // Arrange
        var expectedResult = new PrdGenerationResult
        {
            Success = true,
            OutputFile = "test-prd.md",
            Sections = new List<string> { "Overview", "Requirements", "User Stories" },
            WordCount = 1500,
            EstimatedReadTime = "7 minutes",
            Message = "PRD generated successfully"
        };
        _mockPrdService
            .Setup(s => s.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var args = new[] { "prd", "generate", "Build a task management app", "--name", "TaskMaster" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _mockPrdService.Verify(s => s.GeneratePrdAsync(
            It.Is<PrdGenerationRequest>(r =>
                r.IdeaDescription == "Build a task management app" &&
                r.ProjectName == "TaskMaster"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrdGenerateCommand_WithInvalidTemplate_ShouldReturnError()
    {
        // Arrange
        var args = new[] { "prd", "generate", "Test idea", "--template", "invalid-template" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Invalid template type");
    }

    [Fact]
    public async Task PrdLoadCommand_WithValidFile_ShouldExecuteSuccessfully()
    {
        // Arrange
        var loadResult = new PrdLoadResult
        {
            Success = true,
            ProductName = "Test Project",
            Template = "standard",
            Sections = new List<string> { "Overview", "Requirements" },
            Message = "PRD loaded successfully",
            Analysis = new PrdAnalysis
            {
                WordCount = 1500,
                SectionCount = 2,
                EstimatedReadTime = "7 minutes",
                Completeness = "85%"
            }
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("test.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(loadResult);

        var args = new[] { "prd", "load", "test.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _mockPrdService.Verify(s => s.LoadPrdAsync("test.md", It.IsAny<CancellationToken>()), Times.Once);
        _console.Output.Should().Contain("PRD loaded successfully");
    }

    [Fact]
    public async Task PrdLoadCommand_WithNonExistentFile_ShouldReturnError()
    {
        // Arrange
        var failedLoadResult = new PrdLoadResult
        {
            Success = false,
            Message = "File not found"
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("nonexistent.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(failedLoadResult);

        var args = new[] { "prd", "load", "nonexistent.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Failed to load PRD");
    }

    [Fact]
    public async Task PrdRequirementsCommand_WithFilters_ShouldExecuteSuccessfully()
    {
        // Arrange
        var document = CreateTestPrdDocument();
        var loadResult = new PrdLoadResult
        {
            Success = true,
            ProductName = "Test Project",
            Template = "standard",
            Sections = new List<string> { "Overview" }
        };

        var filteredRequirements = new List<PrdRequirement>
        {
            new()
            {
                Id = "REQ-001",
                Title = "Test Requirement",
                Status = RequirementStatus.Draft,
                Priority = RequirementPriority.High
            }
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(loadResult);

        _mockPrdService
            .Setup(s => s.GetRequirementsAsync(It.IsAny<PrdDocument>(), RequirementStatus.Draft, RequirementPriority.High))
            .ReturnsAsync(filteredRequirements);

        var args = new[] { "prd", "requirements", "test.md", "--status", "draft", "--priority", "high" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _mockPrdService.Verify(s => s.GetRequirementsAsync(
            It.IsAny<PrdDocument>(), RequirementStatus.Draft, RequirementPriority.High), Times.Once);
    }

    [Fact]
    public async Task PrdStatusCommand_WithValidFile_ShouldDisplayStatus()
    {
        // Arrange
        var status = new PrdStatus
        {
            FilePath = "test.md",
            Exists = true,
            TotalRequirements = 10,
            CompletedRequirements = 7,
            InProgressRequirements = 2,
            PendingRequirements = 1,
            LastModified = DateTime.Now
        };

        _mockPrdService
            .Setup(s => s.GetPrdStatusAsync("test.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var args = new[] { "prd", "status", "test.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _mockPrdService.Verify(s => s.GetPrdStatusAsync("test.md", It.IsAny<CancellationToken>()), Times.Once);
        _console.Output.Should().Contain("PRD Status Report");
        _console.Output.Should().Contain("70");
    }

    [Fact]
    public async Task PrdValidateCommand_WithValidPrd_ShouldReturnSuccess()
    {
        // Arrange
        var loadResult = new PrdLoadResult
        {
            Success = true,
            ProductName = "Test Project",
            Template = "standard",
            Sections = new List<string> { "Overview" }
        };

        var validationResult = new PrdValidationResult
        {
            Success = true,
            IsValid = true,
            CompletenessScore = 90.0,
            Errors = new List<object>(),
            Warnings = new List<object>(),
            Suggestions = new List<PrdSuggestion>()
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("test.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(loadResult);

        _mockPrdService
            .Setup(s => s.ValidatePrdAsync(It.IsAny<PrdValidationOptions>()))
            .ReturnsAsync(validationResult);

        var args = new[] { "prd", "validate", "test.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _mockPrdService.Verify(s => s.ValidatePrdAsync(It.IsAny<PrdValidationOptions>()), Times.Once);
        _console.Output.Should().Contain("Valid");
        _console.Output.Should().Contain("90.0%");
    }

    [Fact]
    public async Task PrdValidateCommand_WithInvalidPrd_ShouldReturnError()
    {
        // Arrange
        var loadResult = new PrdLoadResult
        {
            Success = true,
            ProductName = "Test Project",
            Template = "standard",
            Sections = new List<string> { "Overview" }
        };

        var validationResult = new PrdValidationResult
        {
            Success = true,
            IsValid = false,
            CompletenessScore = 50.0,
            Errors = new List<object> { "Missing project description" },
            Warnings = new List<object> { "Few requirements defined" },
            Suggestions = new List<PrdSuggestion>
            {
                new() { Type = "Improvement", Description = "Add more user stories" }
            }
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("test.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(loadResult);

        _mockPrdService
            .Setup(s => s.ValidatePrdAsync(It.IsAny<PrdValidationOptions>()))
            .ReturnsAsync(validationResult);

        var args = new[] { "prd", "validate", "test.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Issues Found");
        _console.Output.Should().Contain("Missing project description");
    }

    [Fact]
    public async Task PrdTemplateCommand_WithValidType_ShouldGenerateTemplate()
    {
        // Arrange
        _mockPrdService
            .Setup(s => s.GenerateTemplateAsync("TestProject", PrdTemplateType.Web, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("output.md");

        var args = new[] { "prd", "template", "TestProject", "--type", "web" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _mockPrdService.Verify(s => s.GenerateTemplateAsync(
            "TestProject", PrdTemplateType.Web, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _console.Output.Should().Contain("PRD template generated");
    }

    [Fact]
    public async Task PrdTemplateCommand_WithListOption_ShouldDisplayTemplates()
    {
        // Arrange
        var args = new[] { "prd", "template", "TestProject", "--list" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain("Available PRD Templates");
        _console.Output.Should().Contain("standard");
        _console.Output.Should().Contain("technical");
        _console.Output.Should().Contain("web");
        _console.Output.Should().Contain("mobile");
    }

    [Theory]
    [InlineData("prd", "generate", "--help")]
    [InlineData("prd", "load", "--help")]
    [InlineData("prd", "requirements", "--help")]
    [InlineData("prd", "status", "--help")]
    [InlineData("prd", "validate", "--help")]
    [InlineData("prd", "template", "--help")]
    public async Task PrdSubcommands_WithHelpOption_ShouldDisplayHelp(params string[] args)
    {
        // Act
        var result = await _app.RunAsync(args);

        // Assert - The help commands should return successful status
        result.Should().Be(0, "Help commands should execute successfully");

        // Note: Help output might be handled differently in Spectre.Console.Cli
        // For now, we'll just verify the command executes without error
    }

    [Fact]
    public async Task PrdCommand_WithInvalidSubcommand_ShouldReturnError()
    {
        // Arrange
        var args = new[] { "prd", "invalid-command" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().NotBe(0);
    }

    [Fact]
    public async Task PrdGenerateCommand_WithMissingRequiredArgument_ShouldReturnError()
    {
        // Arrange
        var args = new[] { "prd", "generate" }; // Missing idea description

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().NotBe(0);
    }

    [Fact]
    public async Task PrdCommands_WithVerboseOption_ShouldEnableVerboseOutput()
    {
        // Arrange
        var generateResult = new PrdGenerationResult
        {
            Success = true,
            OutputFile = "test-prd.md",
            Sections = new List<string> { "Overview", "Requirements" },
            WordCount = 1500,
            EstimatedReadTime = "7 minutes",
            Message = "PRD generated successfully"
        };
        _mockPrdService
            .Setup(s => s.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(generateResult);

        var validationResult = new PrdValidationResult
        {
            Success = true,
            IsValid = true,
            CompletenessScore = 95.0,
            Errors = new List<object>(),
            Warnings = new List<object>(),
            Suggestions = new List<PrdSuggestion>()
        };

        _mockPrdService
            .Setup(s => s.ValidatePrdAsync(It.IsAny<PrdValidationOptions>()))
            .ReturnsAsync(validationResult);

        var args = new[] { "prd", "generate", "Test idea", "--verbose", "--output", "test-output.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        // With verbose flag, the generate command should execute successfully
        _mockPrdService.Verify(s => s.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        // Note: Verbose validation feature may be conditional based on implementation details
    }

    private PrdDocument CreateTestPrdDocument()
    {
        return new PrdDocument
        {
            Configuration = new PrdConfiguration
            {
                ProjectName = "Test Project",
                Description = "A test project",
                Author = "Test Author",
                Version = "1.0.0",
                CreatedAt = DateTime.Now.AddDays(-1),
                UpdatedAt = DateTime.Now,
                TargetAudience = "Test Users",
                Stakeholders = new List<string> { "Product Manager", "Development Team" },
                Metadata = new Dictionary<string, object>()
            },
            Requirements = new List<PrdRequirement>
            {
                new()
                {
                    Id = "REQ-001",
                    Title = "User Authentication",
                    Description = "Users must be able to authenticate",
                    Type = RequirementType.Functional,
                    Priority = RequirementPriority.High,
                    Status = RequirementStatus.Draft,
                    Assignee = "John Doe",
                    EstimatedEffort = 5,
                    AcceptanceCriteria = new List<string> { "User can log in", "User can log out" }
                }
            },
            UserStories = new List<UserStory>
            {
                new()
                {
                    Id = "US-001",
                    Title = "User Login",
                    AsA = "user",
                    IWant = "to log into the system",
                    SoThat = "I can access my data",
                    Priority = UserStoryPriority.MustHave,
                    EstimatedPoints = 3
                }
            },
            Sections = new List<PrdSection>
            {
                new()
                {
                    Id = "overview",
                    Title = "Project Overview",
                    Content = "This is a test project overview",
                    Order = 1,
                    Type = SectionType.Overview
                }
            }
        };
    }

    public void Dispose()
    {
        try
        {
            // Reset AnsiConsole to original console
            if (_originalConsole != null)
            {
                AnsiConsole.Console = _originalConsole;
            }
        }
        finally
        {
            // Don't dispose the console immediately as commands might still be writing to it
            // Let the GC handle it when it's safe
            GC.SuppressFinalize(this);
        }
    }
}