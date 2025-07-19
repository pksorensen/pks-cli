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
/// Tests for PRD command error handling and validation scenarios
/// </summary>
public class PrdErrorHandlingTests : IDisposable
{
    private readonly ServiceCollection _services;
    private readonly TestConsole _console;
    private readonly CommandApp _app;
    private readonly Mock<IPrdService> _mockPrdService;

    public PrdErrorHandlingTests()
    {
        _services = new ServiceCollection();
        _console = new TestConsole();
        _mockPrdService = new Mock<IPrdService>();
        
        // Setup services
        _services.AddSingleton(_mockPrdService.Object);
        
        // Create command app
        _app = new CommandApp(new TypeRegistrar(_services));
        _app.Configure(config =>
        {
            config.SetApplicationName("pks");
            
            config.AddCommand<PrdGenerateCommand>("generate");
            config.AddCommand<PrdLoadCommand>("load");
            config.AddCommand<PrdRequirementsCommand>("requirements");
            config.AddCommand<PrdStatusCommand>("status");
            config.AddCommand<PrdValidateCommand>("validate");
            config.AddCommand<PrdTemplateCommand>("template");
        });
    }

    [Fact]
    public async Task PrdGenerateCommand_WithEmptyIdeaDescription_ShouldReturnError()
    {
        // Arrange
        var args = new[] { "generate", "" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Idea description is required");
        _mockPrdService.Verify(s => s.GeneratePrdAsync(
            It.IsAny<PrdGenerationRequest>(), 
            It.IsAny<string>(), 
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PrdGenerateCommand_WithInvalidTemplateType_ShouldReturnError()
    {
        // Arrange
        var args = new[] { "generate", "Test idea", "--template", "invalid-template" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Invalid template type");
        _console.Output.Should().Contain("Valid types: standard, technical, mobile, web, api, minimal, enterprise");
        _mockPrdService.Verify(s => s.GeneratePrdAsync(
            It.IsAny<PrdGenerationRequest>(), 
            It.IsAny<string>(), 
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PrdGenerateCommand_WhenServiceThrowsException_ShouldHandleGracefully()
    {
        // Arrange
        _mockPrdService
            .Setup(s => s.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Service error"));

        var args = new[] { "generate", "Test idea" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Service error");
    }

    [Fact]
    public async Task PrdLoadCommand_WithNonExistentFile_ShouldReturnError()
    {
        // Arrange
        var parseResult = new PrdParsingResult
        {
            Success = false,
            ErrorMessage = "File not found: nonexistent.md"
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
        _console.Output.Should().Contain("File not found");
    }

    [Fact]
    public async Task PrdLoadCommand_WithCorruptedFile_ShouldReturnError()
    {
        // Arrange
        var parseResult = new PrdParsingResult
        {
            Success = false,
            ErrorMessage = "Invalid PRD format: corrupted markdown structure"
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("corrupted.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parseResult);

        var args = new[] { "load", "corrupted.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Failed to load PRD");
        _console.Output.Should().Contain("Invalid PRD format");
    }

    [Fact]
    public async Task PrdRequirementsCommand_WithInvalidStatus_ShouldReturnError()
    {
        // Arrange
        var document = CreateTestPrdDocument();
        var parseResult = new PrdParsingResult
        {
            Success = true,
            Document = document
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parseResult);

        var args = new[] { "requirements", "test.md", "--status", "invalid-status" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Invalid status: invalid-status");
        _mockPrdService.Verify(s => s.GetRequirementsAsync(
            It.IsAny<PrdDocument>(), 
            It.IsAny<RequirementStatus?>(), 
            It.IsAny<RequirementPriority?>()), Times.Never);
    }

    [Fact]
    public async Task PrdRequirementsCommand_WithInvalidPriority_ShouldReturnError()
    {
        // Arrange
        var document = CreateTestPrdDocument();
        var parseResult = new PrdParsingResult
        {
            Success = true,
            Document = document
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parseResult);

        var args = new[] { "requirements", "test.md", "--priority", "invalid-priority" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Invalid priority: invalid-priority");
    }

    [Fact]
    public async Task PrdRequirementsCommand_WhenNoPrdFileExists_ShouldProvideHelpfulMessage()
    {
        // Arrange
        var args = new[] { "requirements" }; // No file path, defaults to docs/PRD.md

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("PRD file not found");
        _console.Output.Should().Contain("Use pks prd generate to create a new PRD");
    }

    [Fact]
    public async Task PrdStatusCommand_WithNonExistentFile_ShouldDisplayHelpfulMessage()
    {
        // Arrange
        var status = new PrdStatus
        {
            FilePath = "nonexistent.md",
            Exists = false
        };

        _mockPrdService
            .Setup(s => s.GetPrdStatusAsync("nonexistent.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var args = new[] { "status", "nonexistent.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0); // Status command returns 0 even for non-existent files
        _console.Output.Should().Contain("PRD file not found");
        _console.Output.Should().Contain("Use pks prd generate to create a new PRD");
    }

    [Fact]
    public async Task PrdValidateCommand_WithParsingErrors_ShouldReturnError()
    {
        // Arrange
        var parseResult = new PrdParsingResult
        {
            Success = false,
            ErrorMessage = "Failed to parse PRD: invalid YAML frontmatter"
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("invalid.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parseResult);

        var args = new[] { "validate", "invalid.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Failed to load PRD");
        _console.Output.Should().Contain("invalid YAML frontmatter");
        _mockPrdService.Verify(s => s.ValidatePrdAsync(It.IsAny<PrdDocument>()), Times.Never);
    }

    [Fact]
    public async Task PrdValidateCommand_WithValidationFailures_ShouldReturnError()
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
            CompletenessScore = 45.0,
            Errors = new List<string> 
            { 
                "Missing project description",
                "No acceptance criteria defined",
                "Stakeholders not specified"
            },
            Warnings = new List<string> 
            { 
                "Only 2 requirements defined",
                "No user stories for requirements"
            },
            Suggestions = new List<string> 
            { 
                "Add performance requirements",
                "Define success metrics"
            }
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("incomplete.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parseResult);

        _mockPrdService
            .Setup(s => s.ValidatePrdAsync(document))
            .ReturnsAsync(validationResult);

        var args = new[] { "validate", "incomplete.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Issues Found");
        _console.Output.Should().Contain("Missing project description");
        _console.Output.Should().Contain("No acceptance criteria");
        _console.Output.Should().Contain("45.0%");
    }

    [Fact]
    public async Task PrdTemplateCommand_WithEmptyProjectName_ShouldReturnError()
    {
        // Arrange
        var args = new[] { "template", "" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Project name is required");
        _mockPrdService.Verify(s => s.GenerateTemplateAsync(
            It.IsAny<string>(), 
            It.IsAny<PrdTemplateType>(), 
            It.IsAny<string>(), 
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PrdTemplateCommand_WithInvalidTemplateType_ShouldReturnError()
    {
        // Arrange
        var args = new[] { "template", "TestProject", "--type", "invalid-type" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Invalid template type: invalid-type");
        _console.Output.Should().Contain("Available PRD Templates");
    }

    [Fact]
    public async Task PrdCommands_WithMissingRequiredArguments_ShouldReturnError()
    {
        // Test commands that require arguments
        var testCases = new[]
        {
            new { Args = new[] { "generate" }, ExpectedError = "required" },
            new { Args = new[] { "load" }, ExpectedError = "required" },
            new { Args = new[] { "template" }, ExpectedError = "required" }
        };

        foreach (var testCase in testCases)
        {
            // Arrange

            // Act
            var result = await _app.RunAsync(testCase.Args);

            // Assert
            result.Should().NotBe(0, $"Command should fail: {string.Join(" ", testCase.Args)}");
            _console.Output.Should().Contain(testCase.ExpectedError, 
                $"Error message should contain '{testCase.ExpectedError}' for command: {string.Join(" ", testCase.Args)}");
        }
    }

    [Fact]
    public async Task PrdCommands_WithFilePermissionErrors_ShouldHandleGracefully()
    {
        // Arrange
        _mockPrdService
            .Setup(s => s.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("Access denied to output directory"));

        var args = new[] { "generate", "Test idea", "--output", "/readonly/output.md" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("Access denied");
    }

    [Fact]
    public async Task PrdCommands_WithNetworkTimeouts_ShouldHandleGracefully()
    {
        // Arrange
        _mockPrdService
            .Setup(s => s.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Request timed out while generating PRD"));

        var args = new[] { "generate", "Complex application with many features" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("timed out");
    }

    [Fact]
    public async Task PrdCommands_WithCancellation_ShouldHandleGracefully()
    {
        // Arrange
        _mockPrdService
            .Setup(s => s.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Operation was cancelled"));

        var args = new[] { "generate", "Test idea" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(1);
        _console.Output.Should().Contain("cancelled");
    }

    [Fact]
    public async Task PrdExportCommands_WithInvalidExportFormat_ShouldReturnError()
    {
        // Arrange
        var document = CreateTestPrdDocument();
        var parseResult = new PrdParsingResult
        {
            Success = true,
            Document = document
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("test.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(parseResult);

        var args = new[] { "requirements", "test.md", "--export", "output.invalid" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0); // Command succeeds but export fails
        _console.Output.Should().Contain("Unsupported export format");
    }

    [Theory]
    [InlineData("nonexistent-command")]
    [InlineData("generate", "--invalid-option")]
    [InlineData("load", "--unknown-flag")]
    [InlineData("status", "--bad-option")]
    public async Task PrdCommands_WithInvalidArgumentsOrOptions_ShouldProvideHelpfulErrors(params string[] args)
    {
        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().NotBe(0);
        // Should provide helpful error message
        _console.Output.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task PrdCommands_WithVeryLongArguments_ShouldHandleGracefully()
    {
        // Arrange
        var veryLongIdea = new string('a', 10000); // 10KB of text
        var args = new[] { "generate", veryLongIdea };

        _mockPrdService
            .Setup(s => s.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestPrdDocument());

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _mockPrdService.Verify(s => s.GeneratePrdAsync(
            It.Is<PrdGenerationRequest>(r => r.IdeaDescription == veryLongIdea),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
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
                Stakeholders = new List<string> { "Product Manager" },
                Metadata = new Dictionary<string, object>()
            },
            Requirements = new List<PrdRequirement>
            {
                new()
                {
                    Id = "REQ-001",
                    Title = "Test Requirement",
                    Description = "A test requirement",
                    Type = RequirementType.Functional,
                    Priority = RequirementPriority.Medium,
                    Status = RequirementStatus.Draft,
                    Assignee = "Test User",
                    EstimatedEffort = 3,
                    AcceptanceCriteria = new List<string> { "Should work correctly" }
                }
            },
            UserStories = new List<UserStory>
            {
                new()
                {
                    Id = "US-001",
                    Title = "Test Story",
                    AsA = "user",
                    IWant = "to test",
                    SoThat = "it works",
                    Priority = UserStoryPriority.ShouldHave,
                    EstimatedPoints = 2
                }
            },
            Sections = new List<PrdSection>
            {
                new()
                {
                    Id = "overview",
                    Title = "Overview",
                    Content = "Test overview",
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