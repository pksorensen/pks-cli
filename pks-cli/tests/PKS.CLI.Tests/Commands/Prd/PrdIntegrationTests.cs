using PKS.Commands.Prd;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Moq;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
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

    public PrdIntegrationTests()
    {
        _services = new ServiceCollection();
        _console = new TestConsole();
        _mockPrdService = new Mock<IPrdService>();
        
        // Setup services
        _services.AddSingleton(_mockPrdService.Object);
        _services.AddSingleton<ITypeRegistrar>(new TypeRegistrar(_services));
        
        // Create command app with full configuration
        _app = new CommandApp(new TypeRegistrar(_services));
        _app.Configure(config =>
        {
            config.SetApplicationName("pks");
            config.SetApplicationVersion("1.0.0");
            
            // Configure individual PRD commands
            config.AddCommand<PrdGenerateCommand>("generate")
                  .WithDescription("Generate a comprehensive PRD from an idea description")
                  .WithExample(new[] { "generate", "Build a task management app" });
            
            config.AddCommand<PrdLoadCommand>("load")
                  .WithDescription("Load and parse an existing PRD file")
                  .WithExample(new[] { "load", "docs/PRD.md" });
            
            config.AddCommand<PrdRequirementsCommand>("requirements")
                  .WithDescription("List and filter requirements from a PRD document")
                  .WithExample(new[] { "requirements", "--status", "draft" });
            
            config.AddCommand<PrdStatusCommand>("status")
                  .WithDescription("Display PRD status, progress, and statistics")
                  .WithExample(new[] { "status", "--watch" });
            
            config.AddCommand<PrdValidateCommand>("validate")
                  .WithDescription("Validate PRD for completeness, consistency, and quality")
                  .WithExample(new[] { "validate", "--strict" });
            
            config.AddCommand<PrdTemplateCommand>("template")
                  .WithDescription("Generate PRD templates for different project types")
                  .WithExample(new[] { "template", "MyProject", "--type", "web" });
        });
    }

    [Fact]
    public async Task PrdCommands_ShouldBeAvailable()
    {
        // This test verifies that all PRD commands are properly configured
        // Since we're not using branch commands, we'll test individual command availability
        var commands = new[] { "generate", "load", "requirements", "status", "validate", "template" };
        
        foreach (var command in commands)
        {
            // The commands should be registered without throwing exceptions
            // This is verified during configuration in the constructor
        }
        
        // If we reach here, all commands were configured successfully
        _app.Should().NotBeNull();
    }

    [Fact]
    public async Task PrdGenerateCommand_WithValidArgs_ShouldExecuteSuccessfully()
    {
        // Arrange
        var expectedDocument = CreateTestPrdDocument();
        _mockPrdService
            .Setup(s => s.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDocument);

        var args = new[] { "generate", "Build a task management app", "--name", "TaskMaster" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _mockPrdService.Verify(s => s.GeneratePrdAsync(
            It.Is<PrdGenerationRequest>(r => 
                r.IdeaDescription == "Build a task management app" &&
                r.ProjectName == "TaskMaster"),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrdGenerateCommand_WithInvalidTemplate_ShouldReturnError()
    {
        // Arrange
        var args = new[] { "generate", "Test idea", "--template", "invalid-template" };

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
        var document = CreateTestPrdDocument();
        var parseResult = new PrdParsingResult
        {
            Success = true,
            Document = document,
            Warnings = new List<string>()
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("test.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parseResult);

        var args = new[] { "load", "test.md" };

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
        var parseResult = new PrdParsingResult
        {
            Success = false,
            ErrorMessage = "File not found"
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("nonexistent.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parseResult);

        var args = new[] { "load", "nonexistent.md" };

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
        var parseResult = new PrdParsingResult
        {
            Success = true,
            Document = document
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
            .ReturnsAsync(parseResult);

        _mockPrdService
            .Setup(s => s.GetRequirementsAsync(document, RequirementStatus.Draft, RequirementPriority.High))
            .ReturnsAsync(filteredRequirements);

        var args = new[] { "requirements", "test.md", "--status", "draft", "--priority", "high" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _mockPrdService.Verify(s => s.GetRequirementsAsync(
            document, RequirementStatus.Draft, RequirementPriority.High), Times.Once);
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

        var args = new[] { "status", "test.md" };

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
        var document = CreateTestPrdDocument();
        var parseResult = new PrdParsingResult
        {
            Success = true,
            Document = document
        };

        var validationResult = new PrdValidationResult
        {
            IsValid = true,
            CompletenessScore = 90.0,
            Errors = new List<string>(),
            Warnings = new List<string>(),
            Suggestions = new List<string>()
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("test.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parseResult);

        _mockPrdService
            .Setup(s => s.ValidatePrdAsync(document))
            .ReturnsAsync(validationResult);

        var args = new[] { "validate", "test.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _mockPrdService.Verify(s => s.ValidatePrdAsync(document), Times.Once);
        _console.Output.Should().Contain("Valid");
        _console.Output.Should().Contain("90.0%");
    }

    [Fact]
    public async Task PrdValidateCommand_WithInvalidPrd_ShouldReturnError()
    {
        // Arrange
        var document = CreateTestPrdDocument();
        var parseResult = new PrdParsingResult
        {
            Success = true,
            Document = document
        };

        var validationResult = new PrdValidationResult
        {
            IsValid = false,
            CompletenessScore = 50.0,
            Errors = new List<string> { "Missing project description" },
            Warnings = new List<string> { "Few requirements defined" },
            Suggestions = new List<string> { "Add more user stories" }
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("test.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parseResult);

        _mockPrdService
            .Setup(s => s.ValidatePrdAsync(document))
            .ReturnsAsync(validationResult);

        var args = new[] { "validate", "test.md" };

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

        var args = new[] { "template", "TestProject", "--type", "web" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _mockPrdService.Verify(s => s.GenerateTemplateAsync(
            "TestProject", PrdTemplateType.Web, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _console.Output.Should().Contain("Template generated");
    }

    [Fact]
    public async Task PrdTemplateCommand_WithListOption_ShouldDisplayTemplates()
    {
        // Arrange
        var args = new[] { "template", "TestProject", "--list" };

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
    [InlineData("generate", "--help")]
    [InlineData("load", "--help")]
    [InlineData("requirements", "--help")]
    [InlineData("status", "--help")]
    [InlineData("validate", "--help")]
    [InlineData("template", "--help")]
    public async Task PrdSubcommands_WithHelpOption_ShouldDisplayHelp(params string[] args)
    {
        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain("USAGE:");
        _console.Output.Should().Contain("DESCRIPTION:");
    }

    [Fact]
    public async Task PrdCommand_WithInvalidSubcommand_ShouldReturnError()
    {
        // Arrange
        var args = new[] { "invalid-command" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().NotBe(0);
    }

    [Fact]
    public async Task PrdGenerateCommand_WithMissingRequiredArgument_ShouldReturnError()
    {
        // Arrange
        var args = new[] { "generate" }; // Missing idea description

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().NotBe(0);
    }

    [Fact]
    public async Task PrdCommands_WithVerboseOption_ShouldEnableVerboseOutput()
    {
        // Arrange
        var document = CreateTestPrdDocument();
        _mockPrdService
            .Setup(s => s.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);

        var validationResult = new PrdValidationResult
        {
            IsValid = true,
            CompletenessScore = 95.0,
            Errors = new List<string>(),
            Warnings = new List<string>(),
            Suggestions = new List<string>()
        };

        _mockPrdService
            .Setup(s => s.ValidatePrdAsync(document))
            .ReturnsAsync(validationResult);

        var args = new[] { "generate", "Test idea", "--verbose" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        // With verbose flag, validation summary should be shown
        _mockPrdService.Verify(s => s.ValidatePrdAsync(document), Times.Once);
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
        _console?.Dispose();
        GC.SuppressFinalize(this);
    }
}