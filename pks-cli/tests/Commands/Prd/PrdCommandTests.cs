using PKS.Commands.Prd;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Moq;
using Spectre.Console.Cli;
using Xunit;

namespace PKS.CLI.Tests.Commands.Prd;

public class PrdCommandTests
{
    private readonly Mock<IPrdService> _mockPrdService;
    private readonly CommandContext _context;

    public PrdCommandTests()
    {
        _mockPrdService = new Mock<IPrdService>();
        _context = new CommandContext(Mock.Of<IRemainingArguments>(), "prd", null);
    }

    [Fact]
    public void PrdCommand_Execute_ShouldReturnZero()
    {
        // Arrange
        var command = new PrdCommand();

        // Act
        var settings = new PrdMainSettings();
        var result = command.Execute(_context, settings);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task PrdGenerateCommand_WithValidSettings_ShouldGeneratePrd()
    {
        // Arrange
        var settings = new PrdGenerateSettings
        {
            IdeaDescription = "Build a task management app",
            ProjectName = "TaskMaster",
            OutputPath = "test-prd.md",
            Template = "standard"
        };

        var expectedDocument = new PrdDocument
        {
            Configuration = new PrdConfiguration
            {
                ProjectName = "TaskMaster",
                Description = "Build a task management app"
            },
            Requirements = new List<PrdRequirement>
            {
                new()
                {
                    Id = "REQ-001",
                    Title = "User Authentication",
                    Type = RequirementType.Functional,
                    Priority = RequirementPriority.High,
                    Status = RequirementStatus.Draft
                }
            },
            UserStories = new List<UserStory>
            {
                new()
                {
                    Id = "US-001",
                    Title = "User Login",
                    AsA = "user",
                    IWant = "to log in",
                    SoThat = "I can access the app"
                }
            },
            Sections = new List<PrdSection>()
        };

        _mockPrdService
            .Setup(s => s.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrdGenerationResult
            {
                Success = true,
                OutputFile = "test-prd.md",
                Sections = new List<string> { "Overview", "Requirements" },
                WordCount = 1500,
                EstimatedReadTime = "7 minutes",
                Message = "PRD generated successfully"
            });

        var command = new PrdGenerateCommand(_mockPrdService.Object);

        // Act
        var result = await command.ExecuteAsync(_context, settings);

        // Assert
        Assert.Equal(0, result);
        _mockPrdService.Verify(s => s.GeneratePrdAsync(
            It.Is<PrdGenerationRequest>(r =>
                r.IdeaDescription == "Build a task management app" &&
                r.ProjectName == "TaskMaster"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrdGenerateCommand_WithEmptyIdeaDescription_ShouldReturnError()
    {
        // Arrange
        var settings = new PrdGenerateSettings
        {
            IdeaDescription = "",
            ProjectName = "TestProject"
        };

        var command = new PrdGenerateCommand(_mockPrdService.Object);

        // Act
        var result = await command.ExecuteAsync(_context, settings);

        // Assert
        Assert.Equal(1, result);
        _mockPrdService.Verify(s => s.GeneratePrdAsync(
            It.IsAny<PrdGenerationRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PrdLoadCommand_WithValidFile_ShouldLoadPrd()
    {
        // Arrange
        var settings = new PrdLoadSettings
        {
            FilePath = "existing-prd.md",
            Validate = true
        };

        var document = new PrdDocument
        {
            Configuration = new PrdConfiguration
            {
                ProjectName = "Loaded Project",
                Description = "A loaded PRD for testing"
            },
            Requirements = new List<PrdRequirement>(),
            UserStories = new List<UserStory>(),
            Sections = new List<PrdSection>()
        };

        var loadResult = new PrdLoadResult
        {
            Success = true,
            ProductName = "Loaded Project",
            Template = "standard",
            Sections = new List<string> { "Overview", "Requirements" },
            Message = "PRD loaded successfully"
        };

        var validationResult = new PrdValidationResult
        {
            Success = true,
            IsValid = true,
            CompletenessScore = 85.0,
            Errors = new List<object>(),
            Warnings = new List<object>(),
            Suggestions = new List<PrdSuggestion>()
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("existing-prd.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(loadResult);

        _mockPrdService
            .Setup(s => s.ValidatePrdAsync(It.IsAny<PrdValidationOptions>()))
            .ReturnsAsync(validationResult);

        var command = new PrdLoadCommand(_mockPrdService.Object);

        // Act
        var result = await command.ExecuteAsync(_context, settings);

        // Assert
        Assert.Equal(0, result);
        _mockPrdService.Verify(s => s.LoadPrdAsync("existing-prd.md", It.IsAny<CancellationToken>()), Times.Once);
        _mockPrdService.Verify(s => s.ValidatePrdAsync(It.IsAny<PrdValidationOptions>()), Times.Once);
    }

    [Fact]
    public async Task PrdLoadCommand_WithNonExistentFile_ShouldReturnError()
    {
        // Arrange
        var settings = new PrdLoadSettings
        {
            FilePath = "non-existent.md"
        };

        var loadResult = new PrdLoadResult
        {
            Success = false,
            Message = "File not found"
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("non-existent.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(loadResult);

        var command = new PrdLoadCommand(_mockPrdService.Object);

        // Act
        var result = await command.ExecuteAsync(_context, settings);

        // Assert
        Assert.Equal(1, result);
        _mockPrdService.Verify(s => s.LoadPrdAsync("non-existent.md", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrdRequirementsCommand_WithValidFile_ShouldDisplayRequirements()
    {
        // Arrange
        var settings = new PrdRequirementsSettings
        {
            FilePath = "test-prd.md",
            Status = "draft",
            Priority = "high"
        };

        var document = CreateTestDocument();
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
                Title = "High Priority Draft Requirement",
                Status = RequirementStatus.Draft,
                Priority = RequirementPriority.High
            }
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("test-prd.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(loadResult);

        _mockPrdService
            .Setup(s => s.GetRequirementsAsync(document, RequirementStatus.Draft, RequirementPriority.High))
            .ReturnsAsync(filteredRequirements);

        var command = new PrdRequirementsCommand(_mockPrdService.Object);

        // Act
        var result = await command.ExecuteAsync(_context, settings);

        // Assert
        Assert.Equal(0, result);
        _mockPrdService.Verify(s => s.LoadPrdAsync("test-prd.md", It.IsAny<CancellationToken>()), Times.Once);
        _mockPrdService.Verify(s => s.GetRequirementsAsync(
            document, RequirementStatus.Draft, RequirementPriority.High), Times.Once);
    }

    [Fact]
    public async Task PrdStatusCommand_WithValidFile_ShouldDisplayStatus()
    {
        // Arrange
        var settings = new PrdStatusSettings
        {
            FilePath = "test-prd.md"
        };

        var status = new PrdStatus
        {
            FilePath = "test-prd.md",
            Exists = true,
            TotalRequirements = 10,
            CompletedRequirements = 7,
            InProgressRequirements = 2,
            PendingRequirements = 1,
            TotalUserStories = 5,
            LastModified = DateTime.Now
        };

        _mockPrdService
            .Setup(s => s.GetPrdStatusAsync("test-prd.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var command = new PrdStatusCommand(_mockPrdService.Object);

        // Act
        var result = await command.ExecuteAsync(_context, settings);

        // Assert
        Assert.Equal(0, result);
        _mockPrdService.Verify(s => s.GetPrdStatusAsync("test-prd.md", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrdValidateCommand_WithValidFile_ShouldValidatePrd()
    {
        // Arrange
        var settings = new PrdValidateSettings
        {
            FilePath = "test-prd.md",
            Strict = true
        };

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
            CompletenessScore = 92.5,
            Errors = new List<object>(),
            Warnings = new List<object> { "Consider adding more user stories" },
            Suggestions = new List<PrdSuggestion>
            {
                new() { Type = "Performance", Description = "Add performance requirements" }
            }
        };

        _mockPrdService
            .Setup(s => s.LoadPrdAsync("test-prd.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(loadResult);

        _mockPrdService
            .Setup(s => s.ValidatePrdAsync(It.IsAny<PrdValidationOptions>()))
            .ReturnsAsync(validationResult);

        var command = new PrdValidateCommand(_mockPrdService.Object);

        // Act
        var result = await command.ExecuteAsync(_context, settings);

        // Assert
        Assert.Equal(0, result);
        _mockPrdService.Verify(s => s.LoadPrdAsync("test-prd.md", It.IsAny<CancellationToken>()), Times.Once);
        _mockPrdService.Verify(s => s.ValidatePrdAsync(It.IsAny<PrdValidationOptions>()), Times.Once);
    }

    [Fact]
    public async Task PrdTemplateCommand_WithValidSettings_ShouldGenerateTemplate()
    {
        // Arrange
        var settings = new PrdTemplateSettings
        {
            ProjectName = "NewProject",
            TemplateType = "standard",
            OutputPath = "template-output.md"
        };

        _mockPrdService
            .Setup(s => s.GenerateTemplateAsync(
                "NewProject",
                PrdTemplateType.Standard,
                "template-output.md",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("template-output.md");

        var command = new PrdTemplateCommand(_mockPrdService.Object);

        // Act
        var result = await command.ExecuteAsync(_context, settings);

        // Assert
        Assert.Equal(0, result);
        _mockPrdService.Verify(s => s.GenerateTemplateAsync(
            "NewProject",
            PrdTemplateType.Standard,
            "template-output.md",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrdTemplateCommand_WithListTemplates_ShouldDisplayTemplates()
    {
        // Arrange
        var settings = new PrdTemplateSettings
        {
            ProjectName = "Test",
            ListTemplates = true
        };

        var command = new PrdTemplateCommand(_mockPrdService.Object);

        // Act
        var result = await command.ExecuteAsync(_context, settings);

        // Assert
        Assert.Equal(0, result);
        _mockPrdService.Verify(s => s.GenerateTemplateAsync(
            It.IsAny<string>(),
            It.IsAny<PrdTemplateType>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private PrdDocument CreateTestDocument()
    {
        return new PrdDocument
        {
            Configuration = new PrdConfiguration
            {
                ProjectName = "Test Project",
                Description = "A test project",
                Author = "Test Author"
            },
            Requirements = new List<PrdRequirement>
            {
                new()
                {
                    Id = "REQ-001",
                    Title = "Test Requirement",
                    Status = RequirementStatus.Draft,
                    Priority = RequirementPriority.High,
                    Type = RequirementType.Functional
                }
            },
            UserStories = new List<UserStory>
            {
                new()
                {
                    Id = "US-001",
                    Title = "Test User Story",
                    AsA = "user",
                    IWant = "to test",
                    SoThat = "it works"
                }
            },
            Sections = new List<PrdSection>
            {
                new()
                {
                    Id = "overview",
                    Title = "Overview",
                    Content = "Test overview"
                }
            }
        };
    }
}