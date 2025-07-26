using Xunit;
using Spectre.Console.Testing;
using Spectre.Console.Cli;
using Spectre.Console;
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
    /// Unit tests for AuthCommand focusing on GitHub authentication flows
    /// </summary>
    [Trait("Category", "Unit")]
    [Trait("Command", "Auth")]
    public class AuthCommandTests : TestBase
    {
        private Mock<IGitHubService> _mockGitHubService = null!;
        private Mock<IConfigurationService> _mockConfigurationService = null!;
        private Mock<ILogger<AuthCommand>> _mockLogger = null!;
        private string _testWorkingDirectory = null!;

        public AuthCommandTests()
        {
            _testWorkingDirectory = CreateTempDirectory();
            InitializeMocks();
        }

        private void InitializeMocks()
        {
            _mockGitHubService = new Mock<IGitHubService>();
            _mockConfigurationService = new Mock<IConfigurationService>();
            _mockLogger = new Mock<ILogger<AuthCommand>>();

            // Setup default successful authentication
            SetupSuccessfulAuthMocks();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);

            if (_mockGitHubService == null)
            {
                InitializeMocks();
            }

            services.AddSingleton<IGitHubService>(_mockGitHubService.Object);
            services.AddSingleton<IConfigurationService>(_mockConfigurationService.Object);
            services.AddSingleton<ILogger<AuthCommand>>(_mockLogger.Object);
            services.AddTransient<AuthCommand>();
        }

        private void SetupSuccessfulAuthMocks()
        {
            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .ReturnsAsync(new GitHubTokenValidation
                {
                    IsValid = true,
                    Scopes = new[] { "repo", "issues", "write:packages" },
                    ValidatedAt = DateTime.UtcNow
                });

            _mockConfigurationService.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .Returns(Task.CompletedTask);

            _mockConfigurationService.Setup(x => x.GetAsync("github.token"))
                .ReturnsAsync((string?)null);
        }

        [Fact]
        [Trait("TestType", "Token Authentication")]
        public async Task Execute_ShouldConfigureToken_WhenValidTokenProvided()
        {
            // Arrange
            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github",
                Token = "ghp_validtoken123456789"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "command should succeed with valid token");
            
            // Verify token validation was called
            _mockGitHubService.Verify(x => x.ValidateTokenAsync("ghp_validtoken123456789"), Times.Once);
            
            // Verify token was stored securely
            _mockConfigurationService.Verify(x => x.SetAsync("github.token", "ghp_validtoken123456789", false, true), Times.Once);
            
            AssertConsoleOutput("GitHub authentication configured successfully");
            AssertConsoleOutput("Token scopes: repo, issues, write:packages");
        }

        [Fact]
        [Trait("TestType", "Token Authentication")]
        public async Task Execute_ShouldFailWithError_WhenInvalidTokenProvided()
        {
            // Arrange
            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .ReturnsAsync(new GitHubTokenValidation
                {
                    IsValid = false,
                    ErrorMessage = "Bad credentials",
                    ValidatedAt = DateTime.UtcNow
                });

            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github",
                Token = "invalid_token"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "command should fail with invalid token");
            AssertConsoleOutput("Token validation failed");
            AssertConsoleOutput("Bad credentials");
            
            // Verify token was not stored
            _mockConfigurationService.Verify(x => x.SetAsync("github.token", It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        [Trait("TestType", "Interactive Authentication")]
        public async Task Execute_ShouldPromptForToken_WhenNoTokenProvided()
        {
            // Arrange
            TestConsole.Input.PushTextWithEnter("ghp_interactivetoken123456789");
            
            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "command should succeed after prompting");
            AssertConsoleOutput("Enter your GitHub Personal Access Token:");
            
            _mockGitHubService.Verify(x => x.ValidateTokenAsync("ghp_interactivetoken123456789"), Times.Once);
            _mockConfigurationService.Verify(x => x.SetAsync("github.token", "ghp_interactivetoken123456789", false, true), Times.Once);
        }

        [Fact]
        [Trait("TestType", "Device Code Flow")]
        public async Task Execute_ShouldInitiateDeviceCodeFlow_WhenDeviceCodeFlagSet()
        {
            // Arrange
            // Mock device code flow responses
            var deviceCodeResponse = new GitHubDeviceCodeResponse
            {
                DeviceCode = "device_code_123",
                UserCode = "ABCD-1234", 
                VerificationUri = "https://github.com/login/device",
                ExpiresIn = 900,
                Interval = 5
            };

            var tokenResponse = new GitHubDeviceTokenResponse
            {
                AccessToken = "gho_deviceflowtoken123456789",
                TokenType = "bearer",
                Scope = "repo issues"
            };

            _mockGitHubService.Setup(x => x.InitiateDeviceCodeFlowAsync())
                .ReturnsAsync(deviceCodeResponse);

            _mockGitHubService.Setup(x => x.PollDeviceCodeTokenAsync(It.IsAny<string>()))
                .ReturnsAsync(tokenResponse);

            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github",
                UseDeviceCode = true
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "device code flow should succeed");
            
            AssertConsoleOutput($"Visit: {deviceCodeResponse.VerificationUri}");
            AssertConsoleOutput($"Enter code: {deviceCodeResponse.UserCode}");
            AssertConsoleOutput("Waiting for authorization...");
            AssertConsoleOutput("Device authentication successful");
            
            _mockGitHubService.Verify(x => x.InitiateDeviceCodeFlowAsync(), Times.Once);
            _mockGitHubService.Verify(x => x.PollDeviceCodeTokenAsync(deviceCodeResponse.DeviceCode), Times.AtLeastOnce);
            _mockConfigurationService.Verify(x => x.SetAsync("github.token", tokenResponse.AccessToken, false, true), Times.Once);
        }

        [Fact]
        [Trait("TestType", "Device Code Flow")]
        public async Task Execute_ShouldHandleDeviceCodeTimeout_WhenUserDoesNotAuthorize()
        {
            // Arrange
            var deviceCodeResponse = new GitHubDeviceCodeResponse
            {
                DeviceCode = "device_code_timeout",
                UserCode = "TIMEOUT",
                VerificationUri = "https://github.com/login/device",
                ExpiresIn = 1, // Very short expiry for test
                Interval = 1
            };

            _mockGitHubService.Setup(x => x.InitiateDeviceCodeFlowAsync())
                .ReturnsAsync(deviceCodeResponse);

            _mockGitHubService.Setup(x => x.PollDeviceCodeTokenAsync(It.IsAny<string>()))
                .ThrowsAsync(new TimeoutException("Device code expired"));

            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github", 
                UseDeviceCode = true
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail when device code times out");
            AssertConsoleOutput("Device code expired");
            AssertConsoleOutput("Please try again");
        }

        [Fact]
        [Trait("TestType", "Status Check")]
        public async Task Execute_ShouldShowCurrentStatus_WhenStatusFlagSet()
        {
            // Arrange
            _mockConfigurationService.Setup(x => x.GetAsync("github.token"))
                .ReturnsAsync("ghp_existingtoken123456789");

            _mockGitHubService.Setup(x => x.ValidateTokenAsync("ghp_existingtoken123456789"))
                .ReturnsAsync(new GitHubTokenValidation
                {
                    IsValid = true,
                    Scopes = new[] { "repo", "issues" },
                    ValidatedAt = DateTime.UtcNow.AddMinutes(-5)
                });

            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github",
                ShowStatus = true
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "status check should succeed");
            AssertConsoleOutput("GitHub Authentication Status");
            AssertConsoleOutput("Status: Authenticated");
            AssertConsoleOutput("Scopes: repo, issues");
            AssertConsoleOutput("Last validated:");
        }

        [Fact]
        [Trait("TestType", "Status Check")]
        public async Task Execute_ShouldShowNotConfigured_WhenNoTokenExists()
        {
            // Arrange
            _mockConfigurationService.Setup(x => x.GetAsync("github.token"))
                .ReturnsAsync((string?)null);

            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github",
                ShowStatus = true
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "status check should succeed even when not configured");
            AssertConsoleOutput("GitHub Authentication Status");
            AssertConsoleOutput("Status: Not configured");
            AssertConsoleOutput("Use 'pks auth github --token <your-token>' to configure");
        }

        [Fact]
        [Trait("TestType", "Token Removal")]
        public async Task Execute_ShouldRemoveToken_WhenRemoveFlagSet()
        {
            // Arrange
            _mockConfigurationService.Setup(x => x.GetAsync("github.token"))
                .ReturnsAsync("ghp_existingtoken123456789");

            _mockConfigurationService.Setup(x => x.RemoveAsync("github.token"))
                .Returns(Task.CompletedTask);

            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github",
                Remove = true
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "token removal should succeed");
            AssertConsoleOutput("GitHub authentication removed successfully");
            
            _mockConfigurationService.Verify(x => x.RemoveAsync("github.token"), Times.Once);
        }

        [Fact]
        [Trait("TestType", "Token Removal")]
        public async Task Execute_ShouldHandleNoTokenToRemove_GracefullyAsync()
        {
            // Arrange
            _mockConfigurationService.Setup(x => x.GetAsync("github.token"))
                .ReturnsAsync((string?)null);

            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github",
                Remove = true
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "should succeed even when no token to remove");
            AssertConsoleOutput("No GitHub authentication configured to remove");
        }

        [Theory]
        [Trait("TestType", "Validation")]
        [InlineData("invalid-provider")]
        [InlineData("GITHUB")]
        [InlineData("Git-Hub")]
        public async Task Execute_ShouldFailValidation_WhenInvalidProviderSpecified(string invalidProvider)
        {
            // Arrange
            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = invalidProvider
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail with invalid provider");
            AssertConsoleOutput("Invalid authentication provider");
            AssertConsoleOutput("Supported providers: github");
        }

        [Fact]
        [Trait("TestType", "Token Format")]
        public async Task Execute_ShouldValidateTokenFormat_WhenTokenProvided()
        {
            // Arrange
            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github",
                Token = "invalid-token-format" // Not starting with ghp_ or gho_
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1, "should fail with invalid token format");
            AssertConsoleOutput("Invalid GitHub token format");
            AssertConsoleOutput("GitHub tokens should start with 'ghp_' (personal) or 'gho_' (OAuth)");
        }

        [Fact]
        [Trait("TestType", "Scope Validation")]
        public async Task Execute_ShouldWarnAboutMissingScopes_WhenRequiredScopesNotPresent()
        {
            // Arrange
            _mockGitHubService.Setup(x => x.ValidateTokenAsync(It.IsAny<string>()))
                .ReturnsAsync(new GitHubTokenValidation
                {
                    IsValid = true,
                    Scopes = new[] { "public_repo" }, // Missing 'issues' scope
                    ValidatedAt = DateTime.UtcNow
                });

            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github",
                Token = "ghp_limitedscope123456789"
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "should succeed but show warnings");
            AssertConsoleOutput("Warning: Missing recommended scopes");
            AssertConsoleOutput("Missing: issues");
            AssertConsoleOutput("Some features may not work correctly");
        }

        [Fact]
        [Trait("TestType", "Repository Configuration")]
        public async Task Execute_ShouldConfigureRepository_WhenRepositoryOptionProvided()
        {
            // Arrange
            var repositoryUrl = "https://github.com/custom-owner/custom-repo";
            
            _mockGitHubService.Setup(x => x.CheckRepositoryAccessAsync(repositoryUrl))
                .ReturnsAsync(new GitHubAccessLevel
                {
                    HasAccess = true,
                    CanWrite = true,
                    AccessLevel = "write",
                    RepositoryUrl = repositoryUrl
                });

            var command = CreateAuthCommand();
            var settings = new AuthCommand.Settings
            {
                Provider = "github",
                Token = "ghp_validtoken123456789",
                Repository = repositoryUrl
            };

            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(0, "should succeed with repository configuration");
            
            _mockConfigurationService.Verify(x => x.SetAsync("github.repository", repositoryUrl, It.IsAny<bool>(), It.IsAny<bool>()), Times.Once);
            _mockGitHubService.Verify(x => x.CheckRepositoryAccessAsync(repositoryUrl), Times.Once);
            
            AssertConsoleOutput("Repository access verified");
            AssertConsoleOutput("write access");
        }

        private AuthCommand CreateAuthCommand()
        {
            return new AuthCommand(_mockGitHubService.Object, _mockConfigurationService.Object, _mockLogger.Object, TestConsole);
        }

        private async Task<int> ExecuteCommandAsync(AuthCommand command, AuthCommand.Settings settings)
        {
            var context = new CommandContext(Mock.Of<IRemainingArguments>(), "auth", null);
            return await command.ExecuteAsync(context, settings);
        }

        public override void Dispose()
        {
            try
            {
                if (Directory.Exists(_testWorkingDirectory))
                {
                    Directory.Delete(_testWorkingDirectory, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            base.Dispose();
        }
    }

    // Mock AuthCommand class to define expected interface
    public class AuthCommand : AsyncCommand<AuthCommand.Settings>
    {
        private readonly IGitHubService _githubService;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<AuthCommand> _logger;
        private readonly IAnsiConsole _console;

        public AuthCommand(IGitHubService githubService, IConfigurationService configurationService,
            ILogger<AuthCommand> logger, IAnsiConsole console)
        {
            _githubService = githubService;
            _configurationService = configurationService;
            _logger = logger;
            _console = console;
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            // Mock implementation - will be replaced by actual command
            await Task.Delay(10);
            return 0;
        }

        public class Settings : CommandSettings
        {
            [CommandArgument(0, "[PROVIDER]")]
            public string Provider { get; init; } = "github";

            [CommandOption("-t|--token")]
            public string? Token { get; init; }

            [CommandOption("--device-code")]
            public bool UseDeviceCode { get; init; }

            [CommandOption("-s|--status")]
            public bool ShowStatus { get; init; }

            [CommandOption("-r|--remove")]
            public bool Remove { get; init; }

            [CommandOption("--repository")]
            public string? Repository { get; init; }
        }
    }

    // Additional mock models for device code flow
    public class GitHubDeviceCodeResponse
    {
        public string DeviceCode { get; set; } = string.Empty;
        public string UserCode { get; set; } = string.Empty;
        public string VerificationUri { get; set; } = string.Empty;
        public int ExpiresIn { get; set; }
        public int Interval { get; set; }
    }

    public class GitHubDeviceTokenResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public string TokenType { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
    }
}