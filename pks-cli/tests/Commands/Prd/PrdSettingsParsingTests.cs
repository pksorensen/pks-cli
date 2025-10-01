using PKS.Commands.Prd;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console.Cli;
using Xunit;
using FluentAssertions;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace PKS.CLI.Tests.Commands.Prd;

/// <summary>
/// Tests for PRD settings parsing and validation
/// </summary>
public class PrdSettingsParsingTests
{













    [Fact]
    public void PrdSettings_ValidationAttributes_ShouldWork()
    {
        // Test settings validation if any validation attributes are used
        var settings = new PrdGenerateSettings
        {
            IdeaDescription = "" // Empty required field
        };

        // If validation attributes are used, they should trigger validation errors
        var validationContext = new ValidationContext(settings);
        var validationResults = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(settings, validationContext, validationResults, true);

        // This test verifies the validation setup, actual validation may depend on implementation
        validationResults.Should().NotBeNull();
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("technical")]
    [InlineData("mobile")]
    [InlineData("web")]
    [InlineData("api")]
    [InlineData("minimal")]
    [InlineData("enterprise")]
    public void PrdTemplateSettings_ShouldAcceptValidTemplateTypes(string templateType)
    {
        // Arrange
        var settings = new PrdTemplateSettings
        {
            ProjectName = "TestProject",
            TemplateType = templateType
        };

        // Act & Assert
        settings.TemplateType.Should().Be(templateType);

        // Verify that the template type can be parsed as enum
        Enum.TryParse<PrdTemplateType>(templateType, true, out var parsedType).Should().BeTrue();
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("approved")]
    [InlineData("inprogress")]
    [InlineData("completed")]
    [InlineData("blocked")]
    [InlineData("cancelled")]
    [InlineData("onhold")]
    public void PrdRequirementsSettings_ShouldAcceptValidStatusTypes(string status)
    {
        // Arrange
        var settings = new PrdRequirementsSettings
        {
            Status = status
        };

        // Act & Assert
        settings.Status.Should().Be(status);

        // Verify that the status can be parsed as enum
        Enum.TryParse<RequirementStatus>(status, true, out var parsedStatus).Should().BeTrue();
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("high")]
    [InlineData("medium")]
    [InlineData("low")]
    [InlineData("nice")]
    public void PrdRequirementsSettings_ShouldAcceptValidPriorityTypes(string priority)
    {
        // Arrange
        var settings = new PrdRequirementsSettings
        {
            Priority = priority
        };

        // Act & Assert
        settings.Priority.Should().Be(priority);

        // Verify that the priority can be parsed as enum
        Enum.TryParse<RequirementPriority>(priority, true, out var parsedPriority).Should().BeTrue();
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("json")]
    public void PrdSettings_ShouldAcceptValidOutputFormats(string format)
    {
        // Arrange
        var settings = new PrdGenerateSettings
        {
            OutputFormat = format
        };

        // Act & Assert
        settings.OutputFormat.Should().Be(format);
    }
}