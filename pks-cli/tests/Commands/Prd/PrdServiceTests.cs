using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Commands.Prd;

public class PrdServiceTests
{
    private readonly IPrdService _prdService;

    public PrdServiceTests()
    {
        _prdService = new PrdService();
    }

    [Fact]
    public async Task GeneratePrdAsync_WithValidRequest_ShouldReturnPrdDocument()
    {
        // Arrange
        var request = new PrdGenerationRequest
        {
            IdeaDescription = "Build a task management application for teams",
            ProjectName = "TaskMaster",
            TargetAudience = "Development teams",
            Stakeholders = new List<string> { "Product Manager", "Development Team", "End Users" },
            BusinessContext = "Improve team productivity and task tracking"
        };

        // Act
        var result = await _prdService.GeneratePrdAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.OutputFile);
        Assert.NotEmpty(result.Sections);
        Assert.True(result.WordCount > 0);
    }

    [Fact]
    public async Task GeneratePrdAsync_WithMinimalRequest_ShouldGenerateBasicPrd()
    {
        // Arrange
        var request = new PrdGenerationRequest
        {
            IdeaDescription = "Simple web application",
            ProjectName = "WebApp"
        };

        // Act
        var result = await _prdService.GeneratePrdAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.OutputFile);
        Assert.NotEmpty(result.Sections);
    }

    [Fact]
    public async Task LoadPrdAsync_WithNonExistentFile_ShouldReturnFailure()
    {
        // Arrange
        var filePath = "non-existent-file.md";

        // Act
        var result = await _prdService.LoadPrdAsync(filePath);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public async Task SavePrdAsync_WithValidDocument_ShouldSaveSuccessfully()
    {
        // Arrange
        var document = CreateTestPrdDocument();
        var tempPath = Path.GetTempFileName() + ".md";

        try
        {
            // Act
            var result = await _prdService.SavePrdAsync(document, tempPath);

            // Assert
            Assert.True(result);
            Assert.True(File.Exists(tempPath));

            var content = await File.ReadAllTextAsync(tempPath);
            Assert.Contains("Test Project", content);
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task GetPrdStatusAsync_WithExistingFile_ShouldReturnValidStatus()
    {
        // Arrange
        var document = CreateTestPrdDocument();
        var tempPath = Path.GetTempFileName() + ".md";

        try
        {
            await _prdService.SavePrdAsync(document, tempPath);

            // Act
            var status = await _prdService.GetPrdStatusAsync(tempPath);

            // Assert
            Assert.True(status.Exists);
            Assert.Equal(tempPath, status.FilePath);
            Assert.Equal(2, status.TotalRequirements);
            Assert.Equal(1, status.CompletedRequirements);
            Assert.Equal(1, status.TotalUserStories);
            Assert.Equal(50.0, status.CompletionPercentage);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task GetRequirementsAsync_WithStatusFilter_ShouldReturnFilteredResults()
    {
        // Arrange
        var document = CreateTestPrdDocument();

        // Act
        var completedRequirements = await _prdService.GetRequirementsAsync(
            document, RequirementStatus.Completed);
        var draftRequirements = await _prdService.GetRequirementsAsync(
            document, RequirementStatus.Draft);

        // Assert
        Assert.Single(completedRequirements);
        Assert.Single(draftRequirements);
        Assert.Equal(RequirementStatus.Completed, completedRequirements[0].Status);
        Assert.Equal(RequirementStatus.Draft, draftRequirements[0].Status);
    }

    [Fact]
    public async Task ValidatePrdAsync_WithValidDocument_ShouldReturnValidResult()
    {
        // Arrange
        var document = CreateTestPrdDocument();
        var tempPath = Path.GetTempFileName() + ".md";

        try
        {
            // Save the test document to a temp file for validation
            await _prdService.SavePrdAsync(document, tempPath);

            // Act
            var validationOptions = new PrdValidationOptions
            {
                FilePath = tempPath,
                Strictness = "standard",
                IncludeSuggestions = true
            };
            var validation = await _prdService.ValidatePrdAsync(validationOptions);

            // Assert
            Assert.True(validation.IsValid);
            Assert.Empty(validation.Errors ?? new List<string>());
            Assert.True(validation.CompletenessScore > 0);
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ValidatePrdAsync_WithIncompleteDocument_ShouldReturnErrors()
    {
        // Arrange - Create a temporary empty PRD file
        var tempPath = Path.GetTempFileName() + ".md";
        
        try
        {
            // Create a minimal PRD file with empty configuration
            var emptyPrdContent = @"# Empty Project - Product Requirements Document

**Version:** 1.0.0
**Author:** 
**Created:** 2024-01-01
**Updated:** 2024-01-01

## Overview


## Requirements


## User Stories

";
            await File.WriteAllTextAsync(tempPath, emptyPrdContent);

            // Act
            var validationOptions = new PrdValidationOptions
            {
                FilePath = tempPath,
                Strictness = "strict",
                IncludeSuggestions = true
            };
            var validation = await _prdService.ValidatePrdAsync(validationOptions);

            // Assert
            Assert.False(validation.IsValid);
            Assert.NotEmpty(validation.Errors ?? new List<object>());
            Assert.True(validation.CompletenessScore < 100);
        }
        finally
        {
            // Cleanup
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task FindPrdFilesAsync_WithValidDirectory_ShouldFindPrdFiles()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var prdFile1 = Path.Combine(tempDir, "PRD.md");
        var prdFile2 = Path.Combine(tempDir, "requirements.md");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(prdFile1, "# PRD Content");
            await File.WriteAllTextAsync(prdFile2, "# Requirements Content");

            // Act
            var foundFiles = await _prdService.FindPrdFilesAsync(tempDir, false);

            // Assert
            Assert.Contains(prdFile1, foundFiles);
            Assert.Contains(prdFile2, foundFiles);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GenerateTemplateAsync_WithValidParameters_ShouldCreateTemplate()
    {
        // Arrange
        var projectName = "TestProject";
        var templateType = PrdTemplateType.Standard;
        var tempPath = Path.GetTempFileName() + ".md";

        try
        {
            // Act
            var result = await _prdService.GenerateTemplateAsync(
                projectName, templateType, tempPath);

            // Assert
            Assert.Equal(tempPath, result);
            Assert.True(File.Exists(tempPath));

            var content = await File.ReadAllTextAsync(tempPath);
            Assert.Contains(projectName, content);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task AddRequirementAsync_WithValidRequirement_ShouldAddSuccessfully()
    {
        // Arrange
        var document = CreateTestPrdDocument();
        var initialCount = document.Requirements.Count;
        var newRequirement = new PrdRequirement
        {
            Title = "New Requirement",
            Description = "A new requirement for testing",
            Type = RequirementType.Functional,
            Priority = RequirementPriority.Medium,
            Status = RequirementStatus.Draft
        };

        // Act
        var result = await _prdService.AddRequirementAsync(document, newRequirement);

        // Assert
        Assert.True(result);
        Assert.Equal(initialCount + 1, document.Requirements.Count);
        Assert.Contains(document.Requirements, r => r.Title == "New Requirement");
        Assert.NotEmpty(newRequirement.Id);
    }

    [Fact]
    public async Task UpdateRequirementAsync_WithValidId_ShouldUpdateSuccessfully()
    {
        // Arrange
        var document = CreateTestPrdDocument();
        var requirementId = document.Requirements[0].Id;
        var originalTitle = document.Requirements[0].Title;

        // Act
        var result = await _prdService.UpdateRequirementAsync(
            document, requirementId, req => req.Title = "Updated Title");

        // Assert
        Assert.True(result);
        Assert.Equal("Updated Title", document.Requirements[0].Title);
        Assert.NotEqual(originalTitle, document.Requirements[0].Title);
    }

    private PrdDocument CreateTestPrdDocument()
    {
        return new PrdDocument
        {
            Configuration = new PrdConfiguration
            {
                ProjectName = "Test Project",
                Description = "A test project for unit testing",
                Author = "Test Author",
                Version = "1.0.0"
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
                    Status = RequirementStatus.Completed
                },
                new()
                {
                    Id = "REQ-002",
                    Title = "Data Storage",
                    Description = "System must store user data",
                    Type = RequirementType.Technical,
                    Priority = RequirementPriority.Critical,
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
}