using Xunit;
using Spectre.Console.Testing;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using PKS.CLI.Tests.Infrastructure;
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Net;

namespace PKS.CLI.Tests.Commands
{
    /// <summary>
    /// Comprehensive error scenario tests for ReportCommand
    /// Tests various failure modes and edge cases
    /// </summary>
    [Trait("Category", "Unit")]
    [Trait("Command", "Report")]
    [Trait("TestType", "Error Scenarios")]
    public class ReportCommandErrorTests : TestBase
    {
        private Mock<IGitHubService> _mockGitHubService = null!;
        private Mock<ILogger<ReportCommand>> _mockLogger = null!;
        private Mock<IConfigurationService> _mockConfigurationService = null!;

        public ReportCommandErrorTests()
        {
            InitializeMocks();
        }

        private void InitializeMocks()
        {
            _mockGitHubService = new Mock<IGitHubService>();
            _mockLogger = new Mock<ILogger<ReportCommand>>();
            _mockConfigurationService = new Mock<IConfigurationService>();

            // Setup default configuration
            _mockConfigurationService.Setup(x => x.GetAsync("github.token"))
                .ReturnsAsync("ghp_testtoken123456789");
            _mockConfigurationService.Setup(x => x.GetAsync("github.repository"))
                .ReturnsAsync("https://github.com/pksorensen/pks-cli");
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

        [Fact]
        [Trait("ErrorType", "Network")]
        public async Task Execute_ShouldHandleNetworkTimeout_WhenGitHubApiTimesOut()
        {
            // Arrange
            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .ThrowsAsync(new TaskCanceledException("The operation was canceled."));

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail on network timeout");
            AssertConsoleOutput("Network timeout occurred");
            AssertConsoleOutput("Please check your internet connection and try again");
        }

        [Fact]
        [Trait("ErrorType", "Network")]
        public async Task Execute_ShouldHandleHttpException_WhenGitHubApiReturnsError()
        {
            // Arrange
            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .ThrowsAsync(new HttpRequestException("Service unavailable", null, HttpStatusCode.ServiceUnavailable));

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail on HTTP error");
            AssertConsoleOutput("GitHub API error");
            AssertConsoleOutput("Service unavailable");
        }

        [Fact]
        [Trait("ErrorType", "Authentication")]
        public async Task Execute_ShouldHandleExpiredToken_WhenTokenIsExpired()
        {
            // Arrange
            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .ReturnsAsync(new GitHubTokenValidation
                {
                    IsValid = false,
                    ErrorMessage = "Token expired",
                    ValidatedAt = DateTime.UtcNow
                });

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description", 
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail with expired token");
            AssertConsoleOutput("Token expired");
            AssertConsoleOutput("Please reconfigure your GitHub authentication");
        }

        [Fact]
        [Trait("ErrorType", "Rate Limiting")]
        public async Task Execute_ShouldHandleRateLimit_WhenGitHubRateLimitExceeded()
        {
            // Arrange
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
                .ThrowsAsync(new HttpRequestException("API rate limit exceeded", null, HttpStatusCode.Forbidden));

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail when rate limited");
            AssertConsoleOutput("GitHub API rate limit exceeded");
            AssertConsoleOutput("Please wait before trying again");
        }

        [Fact]
        [Trait("ErrorType", "Configuration")]
        public async Task Execute_ShouldHandleCorruptedConfiguration_WhenConfigFileIsCorrupted()
        {
            // Arrange
            _mockConfigurationService.Setup(x => x.GetAsync("github.token"))
                .ThrowsAsync(new InvalidOperationException("Configuration file is corrupted"));

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail with corrupted configuration");
            AssertConsoleOutput("Configuration error");
            AssertConsoleOutput("Configuration file is corrupted");
            AssertConsoleOutput("Try reconfiguring your GitHub authentication");
        }

        [Fact]
        [Trait("ErrorType", "Permission")]
        public async Task Execute_ShouldHandleReadOnlyRepository_WhenRepositoryIsReadOnly()
        {
            // Arrange
            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .ReturnsAsync(new GitHubTokenValidation
                {
                    IsValid = true,
                    Scopes = new[] { "public_repo" }, // Limited scope
                    ValidatedAt = DateTime.UtcNow
                });

            _mockGitHubService.Setup(x => x.CheckRepositoryAccessAsync(It.IsAny<string>()))
                .ReturnsAsync(new GitHubAccessLevel
                {
                    HasAccess = true,
                    CanWrite = false,
                    AccessLevel = "read",
                    ErrorMessage = "Read-only access"
                });

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail with read-only repository");
            AssertConsoleOutput("Insufficient repository permissions");
            AssertConsoleOutput("Read-only access");
            AssertConsoleOutput("Write access required to create issues");
        }

        [Fact]
        [Trait("ErrorType", "Repository")]
        public async Task Execute_ShouldHandlePrivateRepositoryAccess_WhenTokenLacksAccess()
        {
            // Arrange
            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .ReturnsAsync(new GitHubTokenValidation
                {
                    IsValid = true,
                    Scopes = new[] { "public_repo" }, // No private repo access
                    ValidatedAt = DateTime.UtcNow
                });

            _mockGitHubService.Setup(x => x.CheckRepositoryAccessAsync(It.IsAny<string>()))
                .ReturnsAsync(new GitHubAccessLevel
                {
                    HasAccess = false,
                    CanWrite = false,
                    AccessLevel = "none",
                    ErrorMessage = "Repository not found or access denied"
                });

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug",
                Repository = "https://github.com/private-owner/private-repo"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail with private repository access denied");
            AssertConsoleOutput("Repository access denied");
            AssertConsoleOutput("Repository not found or access denied");
            AssertConsoleOutput("Ensure your token has access to private repositories");
        }

        [Fact]
        [Trait("ErrorType", "Input Validation")]
        public async Task Execute_ShouldHandleExtremelyLongTitle_WhenTitleExceedsGitHubLimits()
        {
            // Arrange
            var extremelyLongTitle = new string('A', 300); // GitHub title limit is typically 256 characters

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = extremelyLongTitle,
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail with extremely long title");
            AssertConsoleOutput("Title is too long");
            AssertConsoleOutput("Maximum length is 256 characters");
        }

        [Fact]
        [Trait("ErrorType", "Input Validation")]
        public async Task Execute_ShouldHandleExtremelyLongDescription_WhenDescriptionExceedsGitHubLimits()
        {
            // Arrange
            var extremelyLongDescription = new string('B', 70000); // GitHub body limit is typically 65536 characters

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = extremelyLongDescription,
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail with extremely long description");
            AssertConsoleOutput("Description is too long");
            AssertConsoleOutput("Maximum length is 65536 characters");
        }

        [Fact]
        [Trait("ErrorType", "Repository URL")]
        public async Task Execute_ShouldHandleInvalidRepositoryUrl_WhenUrlIsInvalid()
        {
            // Arrange
            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug",
                Repository = "not-a-valid-url"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail with invalid repository URL");
            AssertConsoleOutput("Invalid repository URL format");
            AssertConsoleOutput("Expected format: https://github.com/owner/repo");
        }

        [Fact]
        [Trait("ErrorType", "System Resource")]
        public async Task Execute_ShouldHandleDiskSpaceFull_WhenSystemResourcesExhausted()
        {
            // Arrange
            _mockConfigurationService.Setup(x => x.GetAsync(It.IsAny<string>()))
                .ThrowsAsync(new IOException("There is not enough space on the disk"));

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail when disk space is full");
            AssertConsoleOutput("System resource error");
            AssertConsoleOutput("There is not enough space on the disk");
        }

        [Fact]
        [Trait("ErrorType", "Concurrent Access")]
        public async Task Execute_ShouldHandleConcurrentModification_WhenConfigurationChangesWhileRunning()
        {
            // Arrange
            var callCount = 0;
            _mockConfigurationService.Setup(x => x.GetAsync("github.token"))
                .Returns(() =>
                {
                    callCount++;
                    if (callCount == 1)
                        return Task.FromResult<string?>("ghp_token123");
                    else
                        throw new InvalidOperationException("Configuration was modified by another process");
                });

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail when configuration is modified concurrently");
            AssertConsoleOutput("Configuration was modified by another process");
            AssertConsoleOutput("Please try the command again");
        }

        [Fact]
        [Trait("ErrorType", "Memory")]
        public async Task Execute_ShouldHandleOutOfMemory_WhenSystemResourcesExhausted()
        {
            // Arrange
            _mockGitHubService.Setup(x => x.CreateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()))
                .ThrowsAsync(new OutOfMemoryException("Insufficient memory to continue execution"));

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

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail when out of memory");
            AssertConsoleOutput("System memory exhausted");
            AssertConsoleOutput("Please try again or contact support");
        }

        [Fact]
        [Trait("ErrorType", "Malformed Response")]
        public async Task Execute_ShouldHandleMalformedApiResponse_WhenGitHubReturnsInvalidJson()
        {
            // Arrange
            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .ThrowsAsync(new System.Text.Json.JsonException("Invalid JSON format in API response"));

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report", 
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail with malformed API response");
            AssertConsoleOutput("Invalid response from GitHub API");
            AssertConsoleOutput("Invalid JSON format in API response");
        }

        [Fact]
        [Trait("ErrorType", "Security")]
        public async Task Execute_ShouldHandleSecurityException_WhenTokenIsRevoked()
        {
            // Arrange
            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .ThrowsAsync(new UnauthorizedAccessException("Token has been revoked"));

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail when token is revoked");
            AssertConsoleOutput("Authentication failed");
            AssertConsoleOutput("Token has been revoked");
            AssertConsoleOutput("Please reconfigure your GitHub authentication");
        }

        [Fact]
        [Trait("ErrorType", "Repository State")]
        public async Task Execute_ShouldHandleArchivedRepository_WhenRepositoryIsArchived()
        {
            // Arrange
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
                    CanWrite = false,
                    AccessLevel = "read",
                    ErrorMessage = "Repository is archived"
                });

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail with archived repository");
            AssertConsoleOutput("Repository is archived");
            AssertConsoleOutput("Cannot create issues in archived repositories");
        }

        [Fact]
        [Trait("ErrorType", "Retry Logic")]
        public async Task Execute_ShouldExhaustRetries_WhenTransientErrorsPersist()
        {
            // Arrange
            var attempts = 0;
            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .Returns(() =>
                {
                    attempts++;
                    throw new HttpRequestException("Temporary server error", null, HttpStatusCode.InternalServerError);
                });

            var command = CreateReportCommand();
            var settings = new ReportCommand.Settings
            {
                Title = "Test Report",
                Description = "Test description",
                Type = "bug"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail after exhausting retries");
            attempts.Should().BeGreaterThan(1, "should have made multiple attempts");
            AssertConsoleOutput("Maximum retry attempts reached");
            AssertConsoleOutput("Temporary server error");
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
}