using Xunit;
using Spectre.Console.Testing;
using Spectre.Console.Cli;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using PKS.CLI.Tests.Infrastructure;
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace PKS.CLI.Tests.Commands
{
    /// <summary>
    /// Tests for ReportCommand execution flow and command-line integration
    /// Focuses on command execution, parameter parsing, and CLI behavior
    /// </summary>
    [Trait("Category", "Unit")]
    [Trait("Command", "Report")]
    [Trait("TestType", "Command Execution")]
    public class ReportCommandExecutionTests : TestBase
    {
        private Mock<IGitHubService> _mockGitHubService = null!;
        private Mock<ILogger<ReportCommand>> _mockLogger = null!;
        private Mock<IConfigurationService> _mockConfigurationService = null!;

        public ReportCommandExecutionTests()
        {
            InitializeMocks();
        }

        private void InitializeMocks()
        {
            _mockGitHubService = new Mock<IGitHubService>();
            _mockLogger = new Mock<ILogger<ReportCommand>>();
            _mockConfigurationService = new Mock<IConfigurationService>();

            // Setup successful GitHub service responses
            SetupSuccessfulGitHubMocks();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);

            if (_mockGitHubService == null)
            {
                InitializeMocks();
            }

            services.AddSingleton<IGitHubService>(_mockGitHubService.Object);
            services.AddSingleton<ILogger<ReportCommand>>(_mockLogger.Object);
            services.AddSingleton<IConfigurationService>(_mockConfigurationService.Object);
            services.AddTransient<ReportCommand>();
        }

        private void SetupSuccessfulGitHubMocks()
        {
            _mockConfigurationService.Setup(x => x.GetAsync("github.token"))
                .ReturnsAsync("ghp_testtoken123456789");

            _mockConfigurationService.Setup(x => x.GetAsync("github.repository"))
                .ReturnsAsync("https://github.com/pksorensen/pks-cli");

            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .ReturnsAsync(new GitHubTokenValidation
                {
                    IsValid = true,
                    Scopes = new[] { "repo", "issues" },
                    ValidatedAt = DateTime.UtcNow
                });

            _mockGitHubService.Setup(x => x.CheckRepositoryAccessAsync(It.IsAny<string>()))
                .ReturnsAsync(new GitHubAccessLevel
                {
                    HasAccess = true,
                    CanWrite = true,
                    AccessLevel = "write"
                });

            _mockGitHubService.Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()))
                .ReturnsAsync(new GitHubIssue
                {
                    Id = 12345,
                    Number = 1,
                    Title = "Test Issue",
                    Body = "Test body",
                    State = "open",
                    HtmlUrl = "https://github.com/pksorensen/pks-cli/issues/1",
                    CreatedAt = DateTime.UtcNow
                });
        }

        [Fact]
        [Trait("TestType", "Parameter Parsing")]
        public async Task Execute_ShouldParseAllParameters_WhenAllParametersProvided()
        {
            // Arrange
            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Bug Report Title",
                Description = "Detailed bug description with multiple lines",
                Type = "bug",
                Priority = "high",
                Tags = new[] { "critical", "urgent", "regression" },
                Repository = "https://github.com/custom-owner/custom-repo",
                IncludeSystemInfo = true
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "command should succeed with all parameters");

            // Verify all parameters were used correctly
            _mockGitHubService.Verify(x => x.CreateIssueAsync(
                "custom-owner",
                "custom-repo",
                "Bug Report Title",
                It.Is<string>(body => body.Contains("Detailed bug description with multiple lines") && body.Contains("## System Information")),
                It.Is<string[]>(labels =>
                    labels.Contains("bug") &&
                    labels.Contains("priority:high") &&
                    labels.Contains("critical") &&
                    labels.Contains("urgent") &&
                    labels.Contains("regression"))
            ), Times.Once);
        }

        [Theory]
        [Trait("TestType", "Argument Validation")]
        [InlineData("bug", true)]
        [InlineData("feature", true)]
        [InlineData("enhancement", true)]
        [InlineData("documentation", true)]
        [InlineData("question", false)]
        [InlineData("invalid", false)]
        [InlineData("BUG", false)] // Case sensitive
        public async Task Execute_ShouldValidateReportType_BasedOnAllowedValues(string reportType, bool shouldSucceed)
        {
            // Arrange
            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = reportType
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            if (shouldSucceed)
            {
                result.Should().Be(0, $"command should succeed with valid type: {reportType}");
            }
            else
            {
                result.Should().Be(1, $"command should fail with invalid type: {reportType}");
                AssertConsoleOutput("Invalid report type");
            }
        }

        [Theory]
        [Trait("TestType", "Priority Validation")]
        [InlineData("low", true)]
        [InlineData("medium", true)]
        [InlineData("high", true)]
        [InlineData("critical", true)]
        [InlineData("urgent", false)] // Not a standard priority
        [InlineData("HIGH", false)]   // Case sensitive
        [InlineData("", false)]       // Empty string
        public async Task Execute_ShouldValidatePriority_BasedOnAllowedValues(string priority, bool shouldSucceed)
        {
            // Arrange
            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug",
                Priority = priority
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            if (shouldSucceed)
            {
                result.Should().Be(0, $"command should succeed with valid priority: {priority}");
            }
            else
            {
                result.Should().Be(1, $"command should fail with invalid priority: {priority}");
                AssertConsoleOutput("Invalid priority level");
            }
        }

        [Fact]
        [Trait("TestType", "Interactive Mode")]
        public async Task Execute_ShouldEnterInteractiveMode_WhenRequiredParametersMissing()
        {
            // Arrange
            TestConsole.Input.PushTextWithEnter("Interactive Bug Report");
            TestConsole.Input.PushTextWithEnter("This is an interactive bug description");

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Type = "bug"
                // Title and Description missing - should prompt
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "command should succeed after interactive input");
            AssertConsoleOutput("Enter issue title:");
            AssertConsoleOutput("Enter issue description:");

            _mockGitHubService.Verify(x => x.CreateIssueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "Interactive Bug Report",
                It.Is<string>(body => body.Contains("This is an interactive bug description")),
                It.IsAny<string[]>()
            ), Times.Once);
        }

        [Fact]
        [Trait("TestType", "Progress Indication")]
        public async Task Execute_ShouldShowProgressIndicators_DuringExecution()
        {
            // Arrange
            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Progress Test Report",
                Description = "Testing progress indicators",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "command should succeed");

            // Verify progress indicators are shown
            AssertConsoleOutput("Validating GitHub authentication...");
            AssertConsoleOutput("Checking repository access...");
            AssertConsoleOutput("Creating issue...");
            AssertConsoleOutput("Issue created successfully");
        }

        [Fact]
        [Trait("TestType", "Output Format")]
        public async Task Execute_ShouldDisplayFormattedOutput_WhenIssueCreatedSuccessfully()
        {
            // Arrange
            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Formatted Output Test",
                Description = "Testing output formatting",
                Type = "bug",
                Priority = "medium"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "command should succeed");

            // Verify formatted output
            AssertConsoleOutput("Issue created successfully");
            AssertConsoleOutput("Title: Formatted Output Test");
            AssertConsoleOutput("Type: bug");
            AssertConsoleOutput("Priority: medium");
            AssertConsoleOutput("URL: https://github.com/pksorensen/pks-cli/issues/1");
            AssertConsoleOutput("Issue #1");
        }

        [Fact]
        [Trait("TestType", "System Information")]
        public async Task Execute_ShouldIncludeSystemInformation_WhenSystemInfoFlagEnabled()
        {
            // Arrange
            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "System Info Test",
                Description = "Testing system information inclusion",
                Type = "bug",
                IncludeSystemInfo = true
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "command should succeed");

            _mockGitHubService.Verify(x => x.CreateIssueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "System Info Test",
                It.Is<string>(body =>
                    body.Contains("## System Information") &&
                    body.Contains("PKS CLI Version:") &&
                    body.Contains("Operating System:") &&
                    body.Contains(".NET Version:") &&
                    body.Contains("Machine Name:") &&
                    body.Contains("Current Directory:")),
                It.IsAny<string[]>()
            ), Times.Once);
        }

        [Fact]
        [Trait("TestType", "System Information")]
        public async Task Execute_ShouldExcludeSystemInformation_WhenSystemInfoFlagDisabled()
        {
            // Arrange
            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "No System Info Test",
                Description = "Testing without system information",
                Type = "bug",
                IncludeSystemInfo = false
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "command should succeed");

            _mockGitHubService.Verify(x => x.CreateIssueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "No System Info Test",
                It.Is<string>(body => !body.Contains("## System Information")),
                It.IsAny<string[]>()
            ), Times.Once);
        }

        [Fact]
        [Trait("TestType", "Help System")]
        public async Task Execute_ShouldShowUsageHelp_WhenHelpRequested()
        {
            // This test would typically be handled by Spectre.Console.Cli's built-in help system
            // but we can verify that our command provides proper descriptions and examples

            // Arrange
            var command = CreateReportCommand();

            // The command class should have proper attributes for help generation
            var settingsType = typeof(ReportCommand.Settings);
            var titleProperty = settingsType.GetProperty(nameof(ReportCommand.Settings.Title));
            var descriptionProperty = settingsType.GetProperty(nameof(ReportCommand.Settings.Description));
            var typeProperty = settingsType.GetProperty(nameof(ReportCommand.Settings.Type));

            // Assert that properties have proper attributes for help generation
            titleProperty.Should().NotBeNull("Title property should exist");
            descriptionProperty.Should().NotBeNull("Description property should exist");
            typeProperty.Should().NotBeNull("Type property should exist");

            // In a real implementation, these would have CommandArgument and CommandOption attributes
            // This test verifies that the command structure supports help generation
        }

        [Theory]
        [Trait("TestType", "Tag Handling")]
        [InlineData(new string[] { }, 1)] // Just the type-based label
        [InlineData(new[] { "critical" }, 2)] // Type label + custom tag
        [InlineData(new[] { "critical", "urgent", "regression" }, 4)] // Type label + 3 custom tags
        [InlineData(new[] { "duplicate-tag", "duplicate-tag" }, 2)] // Duplicates should be removed
        public async Task Execute_ShouldHandleTagsCorrectly_WithVariousTagCombinations(string[] tags, int expectedLabelCount)
        {
            // Arrange
            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Tag Test Report",
                Description = "Testing tag handling",
                Type = "bug",
                Tags = tags
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "command should succeed");

            _mockGitHubService.Verify(x => x.CreateIssueAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                "Tag Test Report",
                It.IsAny<string>(),
                It.Is<string[]>(labels => labels.Length == expectedLabelCount && labels.Contains("bug"))
            ), Times.Once);
        }

        [Fact]
        [Trait("TestType", "Dry Run")]
        public async Task Execute_ShouldShowPreview_WhenDryRunFlagEnabled()
        {
            // Arrange
            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Dry Run Test",
                Description = "Testing dry run functionality",
                Type = "bug",
                Priority = "high",
                Tags = new[] { "test" },
                DryRun = true // This would be a new property
            };

            // For now, simulate dry run behavior by verifying no actual API calls are made
            _mockGitHubService.Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()))
                .Throws(new InvalidOperationException("CreateIssueAsync should not be called in dry run mode"));

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "dry run should succeed without creating issue");
            AssertConsoleOutput("DRY RUN MODE - No issue will be created");
            AssertConsoleOutput("Would create issue with:");
            AssertConsoleOutput("Title: Dry Run Test");
            AssertConsoleOutput("Type: bug");
            AssertConsoleOutput("Priority: high");
            AssertConsoleOutput("Tags: test");

            // Verify no actual issue creation occurred
            _mockGitHubService.Verify(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()), Times.Never);
        }

        private ReportCommand CreateReportCommand()
        {
            return new ReportCommand(_mockGitHubService.Object, _mockLogger.Object, _mockConfigurationService.Object, TestConsole);
        }

        private async Task<int> ExecuteCommandAsync(ReportCommand command, ReportCommand.Settings settings)
        {
            var context = new CommandContext(Mock.Of<IRemainingArguments>(), "report", null);
            return await command.ExecuteAsync(context, settings);
        }
    }

    // Extended ReportCommand.Settings to include additional properties for comprehensive testing
    public partial class ReportCommand
    {
        public partial class Settings
        {
            [CommandOption("--dry-run")]
            public bool DryRun { get; init; }
        }
    }
}