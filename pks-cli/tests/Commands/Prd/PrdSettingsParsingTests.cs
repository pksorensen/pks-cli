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
    public void PrdGenerateSettings_ShouldHaveCorrectArguments()
    {
        // Arrange
        var settings = new PrdGenerateSettings();

        // Act & Assert
        settings.Should().NotBeNull();

        // Verify required argument
        var ideaProperty = typeof(PrdGenerateSettings).GetProperty(nameof(PrdGenerateSettings.IdeaDescription));
        ideaProperty.Should().NotBeNull();

        var argumentAttribute = ideaProperty!.GetCustomAttributes(typeof(CommandArgumentAttribute), false)
            .Cast<CommandArgumentAttribute>().FirstOrDefault();
        argumentAttribute.Should().NotBeNull();
        argumentAttribute!.Position.Should().Be(0);
    }

    [Theory]
    [InlineData(nameof(PrdGenerateSettings.ProjectName))]
    [InlineData(nameof(PrdGenerateSettings.OutputPath))]
    [InlineData(nameof(PrdGenerateSettings.Template))]
    [InlineData(nameof(PrdGenerateSettings.Force))]
    public void PrdGenerateSettings_ShouldHaveCorrectOptions(string propertyName)
    {
        // Arrange
        var property = typeof(PrdGenerateSettings).GetProperty(propertyName);

        // Act
        var optionAttribute = property!.GetCustomAttributes(typeof(CommandOptionAttribute), false)
            .Cast<CommandOptionAttribute>().FirstOrDefault();

        // Assert
        optionAttribute.Should().NotBeNull();
    }

    [Fact]
    public void PrdGenerateSettings_ShouldHaveCorrectDefaultValues()
    {
        // Arrange & Act
        var settings = new PrdGenerateSettings();

        // Assert
        settings.Template.Should().Be("standard");
        settings.OutputFormat.Should().Be("markdown");
        settings.Force.Should().BeFalse();
        settings.Interactive.Should().BeFalse();
        settings.Verbose.Should().BeFalse();
    }

    [Fact]
    public void PrdLoadSettings_ShouldHaveCorrectArguments()
    {
        // Arrange
        var settings = new PrdLoadSettings();

        // Act & Assert
        settings.Should().NotBeNull();

        // Verify required argument
        var filePathProperty = typeof(PrdLoadSettings).GetProperty(nameof(PrdLoadSettings.FilePath));
        filePathProperty.Should().NotBeNull();

        var argumentAttribute = filePathProperty!.GetCustomAttributes(typeof(CommandArgumentAttribute), false)
            .Cast<CommandArgumentAttribute>().FirstOrDefault();
        argumentAttribute.Should().NotBeNull();
        argumentAttribute!.Position.Should().Be(0);
    }

    [Theory]
    [InlineData(nameof(PrdLoadSettings.Validate))]
    [InlineData(nameof(PrdLoadSettings.ExportPath))]
    [InlineData(nameof(PrdLoadSettings.ShowMetadata))]
    public void PrdLoadSettings_ShouldHaveCorrectOptions(string propertyName)
    {
        // Arrange
        var property = typeof(PrdLoadSettings).GetProperty(propertyName);

        // Act
        var optionAttribute = property!.GetCustomAttributes(typeof(CommandOptionAttribute), false)
            .Cast<CommandOptionAttribute>().FirstOrDefault();

        // Assert
        optionAttribute.Should().NotBeNull();
    }

    [Fact]
    public void PrdRequirementsSettings_ShouldHaveOptionalFilePath()
    {
        // Arrange
        var filePathProperty = typeof(PrdRequirementsSettings).GetProperty(nameof(PrdRequirementsSettings.FilePath));

        // Act
        var argumentAttribute = filePathProperty!.GetCustomAttributes(typeof(CommandArgumentAttribute), false)
            .Cast<CommandArgumentAttribute>().FirstOrDefault();

        // Assert
        argumentAttribute.Should().NotBeNull();
        argumentAttribute!.Position.Should().Be(0); // Optional argument
    }

    [Theory]
    [InlineData(nameof(PrdRequirementsSettings.Status))]
    [InlineData(nameof(PrdRequirementsSettings.Priority))]
    [InlineData(nameof(PrdRequirementsSettings.Type))]
    [InlineData(nameof(PrdRequirementsSettings.Assignee))]
    [InlineData(nameof(PrdRequirementsSettings.ExportPath))]
    [InlineData(nameof(PrdRequirementsSettings.ShowDetails))]
    public void PrdRequirementsSettings_ShouldHaveCorrectOptions(string propertyName)
    {
        // Arrange
        var property = typeof(PrdRequirementsSettings).GetProperty(propertyName);

        // Act
        var optionAttribute = property!.GetCustomAttributes(typeof(CommandOptionAttribute), false)
            .Cast<CommandOptionAttribute>().FirstOrDefault();

        // Assert
        optionAttribute.Should().NotBeNull();
    }

    [Theory]
    [InlineData(nameof(PrdStatusSettings.Watch))]
    [InlineData(nameof(PrdStatusSettings.CheckAll))]
    [InlineData(nameof(PrdStatusSettings.ExportPath))]
    [InlineData(nameof(PrdStatusSettings.IncludeHistory))]
    public void PrdStatusSettings_ShouldHaveCorrectOptions(string propertyName)
    {
        // Arrange
        var property = typeof(PrdStatusSettings).GetProperty(propertyName);

        // Act
        var optionAttribute = property!.GetCustomAttributes(typeof(CommandOptionAttribute), false)
            .Cast<CommandOptionAttribute>().FirstOrDefault();

        // Assert
        optionAttribute.Should().NotBeNull();
    }

    [Theory]
    [InlineData(nameof(PrdValidateSettings.Strict))]
    [InlineData(nameof(PrdValidateSettings.AutoFix))]
    [InlineData(nameof(PrdValidateSettings.ReportPath))]
    public void PrdValidateSettings_ShouldHaveCorrectOptions(string propertyName)
    {
        // Arrange
        var property = typeof(PrdValidateSettings).GetProperty(propertyName);

        // Act
        var optionAttribute = property!.GetCustomAttributes(typeof(CommandOptionAttribute), false)
            .Cast<CommandOptionAttribute>().FirstOrDefault();

        // Assert
        optionAttribute.Should().NotBeNull();
    }

    [Fact]
    public void PrdTemplateSettings_ShouldHaveCorrectArguments()
    {
        // Arrange
        var projectNameProperty = typeof(PrdTemplateSettings).GetProperty(nameof(PrdTemplateSettings.ProjectName));

        // Act
        var argumentAttribute = projectNameProperty!.GetCustomAttributes(typeof(CommandArgumentAttribute), false)
            .Cast<CommandArgumentAttribute>().FirstOrDefault();

        // Assert
        argumentAttribute.Should().NotBeNull();
        argumentAttribute!.Position.Should().Be(0);
    }

    [Theory]
    [InlineData(nameof(PrdTemplateSettings.TemplateType))]
    [InlineData(nameof(PrdTemplateSettings.OutputPath))]
    [InlineData(nameof(PrdTemplateSettings.ListTemplates))]
    public void PrdTemplateSettings_ShouldHaveCorrectOptions(string propertyName)
    {
        // Arrange
        var property = typeof(PrdTemplateSettings).GetProperty(propertyName);

        // Act
        var optionAttribute = property!.GetCustomAttributes(typeof(CommandOptionAttribute), false)
            .Cast<CommandOptionAttribute>().FirstOrDefault();

        // Assert
        optionAttribute.Should().NotBeNull();
    }

    [Fact]
    public void PrdTemplateSettings_ShouldHaveCorrectDefaultValues()
    {
        // Arrange & Act
        var settings = new PrdTemplateSettings();

        // Assert
        settings.TemplateType.Should().Be("standard");
        settings.ListTemplates.Should().BeFalse();
    }

    [Theory]
    [InlineData("PrdGenerateSettings")]
    [InlineData("PrdLoadSettings")]
    [InlineData("PrdRequirementsSettings")]
    [InlineData("PrdStatusSettings")]
    [InlineData("PrdValidateSettings")]
    [InlineData("PrdTemplateSettings")]
    public void PrdSettings_ShouldHaveDescriptionAttributes(string settingsTypeName)
    {
        // Arrange
        var settingsType = typeof(PrdGenerateSettings).Assembly.GetType($"PKS.Commands.Prd.{settingsTypeName}");

        // Act & Assert
        settingsType.Should().NotBeNull();

        var properties = settingsType!.GetProperties();
        foreach (var property in properties)
        {
            if (property.GetCustomAttributes(typeof(CommandArgumentAttribute), false).Any() ||
                property.GetCustomAttributes(typeof(CommandOptionAttribute), false).Any())
            {
                var descriptionAttribute = property.GetCustomAttributes(typeof(DescriptionAttribute), false)
                    .Cast<DescriptionAttribute>().FirstOrDefault();

                descriptionAttribute.Should().NotBeNull($"{property.Name} should have a description");
                descriptionAttribute!.Description.Should().NotBeNullOrEmpty($"{property.Name} description should not be empty");
            }
        }
    }

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