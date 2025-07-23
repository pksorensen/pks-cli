using PKS.Commands.Prd;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using Moq;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;

namespace PKS.CLI.Tests.Commands.Prd;

/// <summary>
/// Tests for PRD command help system functionality
/// </summary>
public class PrdHelpSystemTests : IDisposable
{
    private readonly ServiceCollection _services;
    private readonly TestConsole _console;
    private readonly CommandApp _app;
    private readonly Mock<IPrdService> _mockPrdService;

    public PrdHelpSystemTests()
    {
        _services = new ServiceCollection();
        _console = new TestConsole();
        _mockPrdService = new Mock<IPrdService>();

        // Setup services
        _services.AddSingleton(_mockPrdService.Object);

        // Create command app with full configuration
        _app = new CommandApp(new TypeRegistrar(_services));
        _app.Configure(config =>
        {
            config.SetApplicationName("pks");
            config.SetApplicationVersion("1.0.0");

            config.AddCommand<PrdGenerateCommand>("generate")
                   .WithDescription("Generate a comprehensive PRD from an idea description")
                   .WithExample(new[] { "generate", "Build a task management app" })
                   .WithExample(new[] { "generate", "E-commerce platform", "--template", "web" });

            config.AddCommand<PrdLoadCommand>("load")
                   .WithDescription("Load and parse an existing PRD file")
                   .WithExample(new[] { "load", "docs/PRD.md" })
                   .WithExample(new[] { "load", "project.md", "--validate" });

            config.AddCommand<PrdRequirementsCommand>("requirements")
                   .WithDescription("List and filter requirements from a PRD document")
                   .WithExample(new[] { "requirements", "--status", "draft" })
                   .WithExample(new[] { "requirements", "docs/PRD.md", "--priority", "high" });

            config.AddCommand<PrdStatusCommand>("status")
                   .WithDescription("Display PRD status, progress, and statistics")
                   .WithExample(new[] { "status", "--watch" })
                   .WithExample(new[] { "status", "--check-all" });

            config.AddCommand<PrdValidateCommand>("validate")
                   .WithDescription("Validate PRD for completeness, consistency, and quality")
                   .WithExample(new[] { "validate", "--strict" })
                   .WithExample(new[] { "validate", "docs/PRD.md", "--report", "validation.json" });

            config.AddCommand<PrdTemplateCommand>("template")
                   .WithDescription("Generate PRD templates for different project types")
                   .WithExample(new[] { "template", "MyProject", "--type", "web" })
                   .WithExample(new[] { "template", "--list" });
        });
    }

    [Fact]
    public async Task PrdBranch_WithHelpFlag_ShouldDisplayBranchHelp()
    {
        // Arrange
        var args = new[] { "--help" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain("Manage Product Requirements Documents");
        _console.Output.Should().Contain("AI-powered generation");
        _console.Output.Should().Contain("COMMANDS:");
        _console.Output.Should().Contain("generate");
        _console.Output.Should().Contain("load");
        _console.Output.Should().Contain("requirements");
        _console.Output.Should().Contain("status");
        _console.Output.Should().Contain("validate");
        _console.Output.Should().Contain("template");
    }

    [Fact]
    public async Task PrdGenerate_WithHelpFlag_ShouldDisplayCommandHelp()
    {
        // Arrange
        var args = new[] { "generate", "--help" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain("Generate a comprehensive PRD from an idea description");
        _console.Output.Should().Contain("USAGE:");
        _console.Output.Should().Contain("<IDEA_DESCRIPTION>");
        _console.Output.Should().Contain("OPTIONS:");
        _console.Output.Should().Contain("--name");
        _console.Output.Should().Contain("--output");
        _console.Output.Should().Contain("--template");
        _console.Output.Should().Contain("EXAMPLES:");
    }

    [Fact]
    public async Task PrdLoad_WithHelpFlag_ShouldDisplayCommandHelp()
    {
        // Arrange
        var args = new[] { "load", "--help" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain("Load and parse an existing PRD file");
        _console.Output.Should().Contain("<FILE_PATH>");
        _console.Output.Should().Contain("--validate");
        _console.Output.Should().Contain("--export");
        _console.Output.Should().Contain("--show-metadata");
    }

    [Fact]
    public async Task PrdRequirements_WithHelpFlag_ShouldDisplayCommandHelp()
    {
        // Arrange
        var args = new[] { "requirements", "--help" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain("List and filter requirements from a PRD document");
        _console.Output.Should().Contain("--status");
        _console.Output.Should().Contain("--priority");
        _console.Output.Should().Contain("--type");
        _console.Output.Should().Contain("--assignee");
        _console.Output.Should().Contain("--show-details");
    }

    [Fact]
    public async Task PrdStatus_WithHelpFlag_ShouldDisplayCommandHelp()
    {
        // Arrange
        var args = new[] { "status", "--help" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain("Display PRD status, progress, and statistics");
        _console.Output.Should().Contain("--watch");
        _console.Output.Should().Contain("--check-all");
        _console.Output.Should().Contain("--export");
        _console.Output.Should().Contain("--include-history");
    }

    [Fact]
    public async Task PrdValidate_WithHelpFlag_ShouldDisplayCommandHelp()
    {
        // Arrange
        var args = new[] { "requirements", "--help" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain("--strict");
        _console.Output.Should().Contain("--fix");
        _console.Output.Should().Contain("--report");
    }

    [Fact]
    public async Task PrdTemplate_WithHelpFlag_ShouldDisplayCommandHelp()
    {
        // Arrange
        var args = new[] { "template", "--help" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain("Generate PRD templates for different project types");
        _console.Output.Should().Contain("<PROJECT_NAME>");
        _console.Output.Should().Contain("--type");
        _console.Output.Should().Contain("--output");
        _console.Output.Should().Contain("--list");
    }

    [Fact]
    public void PrdBranchCommand_ShouldHaveCorrectDescription()
    {
        // Arrange
        var command = new PrdBranchCommand();

        // Act
        var helpContent = command.GetHelpContent();

        // Assert
        helpContent.Should().Contain("Manage Product Requirements Documents (PRDs)");
        helpContent.Should().Contain("AI-powered generation");
        helpContent.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(typeof(PrdGenerateCommand))]
    [InlineData(typeof(PrdLoadCommand))]
    [InlineData(typeof(PrdRequirementsCommand))]
    [InlineData(typeof(PrdStatusCommand))]
    [InlineData(typeof(PrdValidateCommand))]
    [InlineData(typeof(PrdTemplateCommand))]
    public void PrdCommands_ShouldHaveDescriptionAttributes(Type commandType)
    {
        // Arrange & Act
        var descriptionAttribute = commandType.GetCustomAttributes(typeof(DescriptionAttribute), false)
            .Cast<DescriptionAttribute>().FirstOrDefault();

        // Assert
        descriptionAttribute.Should().NotBeNull($"{commandType.Name} should have a Description attribute");
        descriptionAttribute!.Description.Should().NotBeNullOrEmpty($"{commandType.Name} description should not be empty");
    }

    [Fact]
    public async Task PrdCommands_ShouldDisplayExamplesInHelp()
    {
        // Test that examples are properly displayed in help
        var testCases = new[]
        {
            new { Command = new[] { "prd", "generate", "--help" }, ExpectedExample = "Build a task management app" },
            new { Command = new[] { "prd", "load", "--help" }, ExpectedExample = "docs/PRD.md" },
            new { Command = new[] { "prd", "requirements", "--help" }, ExpectedExample = "--status draft" },
            new { Command = new[] { "prd", "status", "--help" }, ExpectedExample = "--watch" },
            new { Command = new[] { "prd", "validate", "--help" }, ExpectedExample = "--strict" },
            new { Command = new[] { "prd", "template", "--help" }, ExpectedExample = "--type web" }
        };

        foreach (var testCase in testCases)
        {
            // Arrange

            // Act
            var result = await _app.RunAsync(testCase.Command);

            // Assert
            result.Should().Be(0);
            _console.Output.Should().Contain("EXAMPLES:");
            _console.Output.Should().Contain(testCase.ExpectedExample);
        }
    }

    [Fact]
    public async Task PrdBranch_ShouldDisplayUsageInformation()
    {
        // Arrange
        var args = new[] { "--help" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain("USAGE:");
        _console.Output.Should().Contain("pks prd [COMMAND]");
        _console.Output.Should().Contain("Use 'pks prd [COMMAND] --help' for more information");
    }

    [Theory]
    [InlineData("generate", "Generate a comprehensive PRD from an idea description")]
    [InlineData("load", "Load and parse an existing PRD file")]
    [InlineData("requirements", "List and filter requirements from a PRD document")]
    [InlineData("status", "Display PRD status, progress, and statistics")]
    [InlineData("validate", "Validate PRD for completeness, consistency, and quality")]
    [InlineData("template", "Generate PRD templates for different project types")]
    public async Task PrdBranch_ShouldDisplayCorrectCommandDescriptions(string commandName, string expectedDescription)
    {
        // Arrange
        var args = new[] { "--help" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain(commandName);
        _console.Output.Should().Contain(expectedDescription);
    }

    [Fact]
    public async Task PrdCommand_WithInvalidSubcommand_ShouldSuggestHelp()
    {
        // Arrange
        var args = new[] { "invalid-command" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().NotBe(0);
        // The error should suggest using --help or list available commands
        var output = _console.Output;
        var containsHelp = output.Contains("help", StringComparison.OrdinalIgnoreCase);
        var containsAvailable = output.Contains("available", StringComparison.OrdinalIgnoreCase);
        (containsHelp || containsAvailable).Should().BeTrue("Error should suggest help or show available commands");
    }

    [Fact]
    public void PrdSettings_ShouldHaveDetailedDescriptions()
    {
        // Test that all option descriptions are meaningful and helpful
        var settingsTypes = new[]
        {
            typeof(PrdGenerateSettings),
            typeof(PrdLoadSettings),
            typeof(PrdRequirementsSettings),
            typeof(PrdStatusSettings),
            typeof(PrdValidateSettings),
            typeof(PrdTemplateSettings)
        };

        foreach (var settingsType in settingsTypes)
        {
            var properties = settingsType.GetProperties();
            foreach (var property in properties)
            {
                var commandAttributes = property.GetCustomAttributes(typeof(CommandArgumentAttribute), false)
                    .Concat(property.GetCustomAttributes(typeof(CommandOptionAttribute), false));

                if (commandAttributes.Any())
                {
                    var descriptionAttribute = property.GetCustomAttributes(typeof(DescriptionAttribute), false)
                        .Cast<DescriptionAttribute>().FirstOrDefault();

                    descriptionAttribute.Should().NotBeNull($"{settingsType.Name}.{property.Name} should have a description");
                    descriptionAttribute!.Description.Should().NotBeNullOrEmpty($"{settingsType.Name}.{property.Name} description should not be empty");
                    descriptionAttribute.Description.Length.Should().BeGreaterThan(10, $"{settingsType.Name}.{property.Name} description should be meaningful");
                }
            }
        }
    }

    [Fact]
    public async Task PrdTemplate_WithListOption_ShouldDisplayTemplateHelp()
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
        _console.Output.Should().Contain("mobile");
        _console.Output.Should().Contain("web");
        _console.Output.Should().Contain("api");
        _console.Output.Should().Contain("minimal");
        _console.Output.Should().Contain("enterprise");
        _console.Output.Should().Contain("Usage:");
    }

    [Fact]
    public async Task PrdCommands_WithInvalidOptions_ShouldDisplayHelpfulError()
    {
        // Test that invalid options show helpful error messages
        var testCases = new[]
        {
            new[] { "prd", "generate", "idea", "--invalid-option" },
            new[] { "prd", "load", "file.md", "--unknown-flag" },
            new[] { "prd", "status", "--invalid-flag" }
        };

        foreach (var args in testCases)
        {
            // Arrange

            // Act
            var result = await _app.RunAsync(args);

            // Assert
            result.Should().NotBe(0, $"Command should fail for invalid options: {string.Join(" ", args)}");
            // Should suggest using --help
            var output = _console.Output;
            var containsHelp = output.Contains("help", StringComparison.OrdinalIgnoreCase);
            var containsOption = output.Contains("option", StringComparison.OrdinalIgnoreCase);
            var containsUsage = output.Contains("usage", StringComparison.OrdinalIgnoreCase);
            (containsHelp || containsOption || containsUsage).Should().BeTrue("Error should suggest help, mention option, or show usage");
        }
    }

    [Fact]
    public async Task PrdBranch_WithVersionFlag_ShouldDisplayVersion()
    {
        // Arrange
        var args = new[] { "--version" };

        // Act
        var result = await _app.RunAsync(args);

        // Assert
        result.Should().Be(0);
        _console.Output.Should().Contain("1.0.0");
    }

    public void Dispose()
    {
        _console?.Dispose();
        GC.SuppressFinalize(this);
    }
}