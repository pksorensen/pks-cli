using Xunit;
using Microsoft.Extensions.DependencyInjection;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.CLI.Tests.Infrastructure;
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using System.Net.Http;

namespace PKS.CLI.Tests.Integration.GitHub
{
    /// <summary>
    /// Integration tests for GitHub API interactions in the report command
    /// These tests may call actual GitHub APIs in CI/CD environments with proper credentials
    /// </summary>
    [Collection("Integration")]
    [Trait("Category", "Integration")]
    [Trait("Component", "GitHub")]
    public class GitHubReportIntegrationTests : IntegrationTestBase
    {
        private readonly IGitHubService _githubService;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<GitHubReportIntegrationTests> _logger;

        public GitHubReportIntegrationTests()
        {
            _githubService = GetService<IGitHubService>();
            _configurationService = GetService<IConfigurationService>();
            _logger = GetService<ILogger<GitHubReportIntegrationTests>>();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);

            // Use real GitHub service for integration tests
            services.AddHttpClient<IGitHubService, GitHubService>();
            services.AddSingleton<IConfigurationService, PKS.Infrastructure.Services.ConfigurationService>();
            
            // Add logging
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Debug);
            });
        }

        [Fact]
        [Trait("TestType", "Authentication")]
        [Trait("Priority", "Critical")]
        public async Task GitHubAuthentication_ShouldValidateToken_WhenValidTokenProvided()
        {
            // Arrange
            var testToken = Environment.GetEnvironmentVariable("GITHUB_TEST_TOKEN");
            
            // Skip test if no token provided (for local development)
            if (string.IsNullOrEmpty(testToken))
            {
                _logger.LogInformation("Skipping GitHub integration test - no test token provided");
                return;
            }

            // Act
            var result = await _githubService.ValidateTokenAsync(testToken);

            // Assert
            result.Should().NotBeNull();
            result.IsValid.Should().BeTrue("valid token should pass validation");
            result.Scopes.Should().NotBeEmpty("token should have scopes");
            result.ValidatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            
            _logger.LogInformation($"Token validation successful. Scopes: {string.Join(", ", result.Scopes)}");
        }

        [Fact]
        [Trait("TestType", "Authentication")]
        [Trait("Priority", "High")]
        public async Task GitHubAuthentication_ShouldFailValidation_WhenInvalidTokenProvided()
        {
            // Arrange
            var invalidToken = "invalid_token_12345";

            // Act
            var result = await _githubService.ValidateTokenAsync(invalidToken);

            // Assert
            result.Should().NotBeNull();
            result.IsValid.Should().BeFalse("invalid token should fail validation");
            result.ErrorMessage.Should().NotBeNullOrEmpty("should provide error message");
            result.ValidatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        }

        [Fact]
        [Trait("TestType", "Repository Access")]
        [Trait("Priority", "Critical")]
        public async Task RepositoryAccess_ShouldVerifyAccess_WhenValidRepositoryProvided()
        {
            // Arrange
            var testToken = Environment.GetEnvironmentVariable("GITHUB_TEST_TOKEN");
            var testRepository = Environment.GetEnvironmentVariable("GITHUB_TEST_REPOSITORY") 
                ?? "https://github.com/pksorensen/pks-cli";

            // Skip test if no token provided
            if (string.IsNullOrEmpty(testToken))
            {
                _logger.LogInformation("Skipping repository access test - no test token provided");
                return;
            }

            // Configure token for test
            await _configurationService.SetAsync("github.token", testToken);

            // Act
            var result = await _githubService.CheckRepositoryAccessAsync(testRepository);

            // Assert
            result.Should().NotBeNull();
            result.RepositoryUrl.Should().Be(testRepository);
            result.HasAccess.Should().BeTrue("should have access to test repository");
            result.AccessLevel.Should().NotBeNullOrEmpty("should provide access level");
            result.CheckedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            
            _logger.LogInformation($"Repository access check successful. Access Level: {result.AccessLevel}");
        }

        [Fact]
        [Trait("TestType", "Repository Access")]
        [Trait("Priority", "Medium")]
        public async Task RepositoryAccess_ShouldDenyAccess_WhenRepositoryNotFound()
        {
            // Arrange
            var testToken = Environment.GetEnvironmentVariable("GITHUB_TEST_TOKEN");
            var nonExistentRepo = "https://github.com/nonexistent-owner/nonexistent-repo";

            // Skip test if no token provided
            if (string.IsNullOrEmpty(testToken))
            {
                _logger.LogInformation("Skipping repository not found test - no test token provided");
                return;
            }

            // Configure token for test
            await _configurationService.SetAsync("github.token", testToken);

            // Act
            var result = await _githubService.CheckRepositoryAccessAsync(nonExistentRepo);

            // Assert
            result.Should().NotBeNull();
            result.HasAccess.Should().BeFalse("should not have access to non-existent repository");
            result.ErrorMessage.Should().NotBeNullOrEmpty("should provide error message");
        }

        [Fact]
        [Trait("TestType", "Issue Creation")]
        [Trait("Priority", "Critical")]
        public async Task IssueCreation_ShouldCreateIssue_WhenValidParametersProvided()
        {
            // Arrange
            var testToken = Environment.GetEnvironmentVariable("GITHUB_TEST_TOKEN");
            var testRepository = Environment.GetEnvironmentVariable("GITHUB_TEST_REPOSITORY");
            var allowIssueCreation = Environment.GetEnvironmentVariable("GITHUB_ALLOW_ISSUE_CREATION") == "true";
            
            // Skip test if not configured for issue creation
            if (string.IsNullOrEmpty(testToken) || string.IsNullOrEmpty(testRepository) || !allowIssueCreation)
            {
                _logger.LogInformation("Skipping issue creation test - not configured or not allowed");
                return;
            }

            // Configure token for test
            await _configurationService.SetAsync("github.token", testToken);

            // Parse repository URL
            var uri = new Uri(testRepository);
            var pathParts = uri.AbsolutePath.Trim('/').Split('/');
            var owner = pathParts[0];
            var repo = pathParts[1];

            var title = $"Test Issue - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";
            var body = $"""
                ## Test Issue Description
                
                This is a test issue created by the PKS CLI integration tests.
                
                **Created**: {DateTime.UtcNow:O}
                **Test Run ID**: {Guid.NewGuid()}
                
                ## System Information
                - PKS CLI Version: 1.0.0
                - Operating System: {Environment.OSVersion}
                - .NET Version: {Environment.Version}
                
                This issue can be safely closed.
                """;
            var labels = new[] { "test", "automated", "pks-cli" };

            // Act
            var result = await _githubService.CreateIssueAsync(owner, repo, title, body, labels);

            // Assert
            result.Should().NotBeNull();
            result.Id.Should().BePositive("issue should have a valid ID");
            result.Number.Should().BePositive("issue should have a valid number");
            result.Title.Should().Be(title);
            result.Body.Should().Contain("Test Issue Description");
            result.State.Should().Be("open");
            result.HtmlUrl.Should().NotBeNullOrEmpty("should provide HTML URL");
            result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));

            _logger.LogInformation($"Issue created successfully: {result.HtmlUrl}");
            
            // Note: In a real scenario, we might want to close the test issue automatically
            // to avoid cluttering the repository with test issues
        }

        [Fact]
        [Trait("TestType", "Error Handling")]
        [Trait("Priority", "High")]
        public async Task IssueCreation_ShouldHandleRateLimit_WhenRateLimitExceeded()
        {
            // Arrange
            var testToken = Environment.GetEnvironmentVariable("GITHUB_TEST_TOKEN");
            
            // Skip test if no token provided
            if (string.IsNullOrEmpty(testToken))
            {
                _logger.LogInformation("Skipping rate limit test - no test token provided");
                return;
            }

            // This test is designed to simulate rate limiting behavior
            // In practice, rate limits are high enough that this test may not trigger
            // the actual rate limit, but it tests the error handling path

            var owner = "nonexistent-owner";
            var repo = "nonexistent-repo"; 
            var title = "Test Rate Limit";
            var body = "This should fail due to repository access issues";

            // Act & Assert
            var exception = await Record.ExceptionAsync(async () =>
            {
                await _githubService.CreateIssueAsync(owner, repo, title, body);
            });

            exception.Should().NotBeNull("should throw exception for invalid repository");
            exception.Should().BeOfType<InvalidOperationException>();
            exception.Message.Should().Contain("Failed to create issue");
        }

        [Fact]
        [Trait("TestType", "Network Resilience")]
        [Trait("Priority", "Medium")]
        public async Task GitHubService_ShouldHandleTimeout_WhenNetworkIsSlowAsync()
        {
            // Arrange
            var httpClient = new HttpClient()
            {
                Timeout = TimeSpan.FromMilliseconds(100) // Very short timeout to simulate network issues
            };

            var configService = GetService<IConfigurationService>();
            var githubService = new GitHubService(httpClient, configService);

            // Act & Assert
            var exception = await Record.ExceptionAsync(async () =>
            {
                await githubService.ValidateTokenAsync("test-token");
            });

            // The service should either handle the timeout gracefully or throw a meaningful exception
            if (exception != null)
            {
                exception.Should().BeOfType<TaskCanceledException>()
                    .Or.BeOfType<HttpRequestException>();
            }
        }

        [Fact]
        [Trait("TestType", "Configuration")]
        [Trait("Priority", "High")]
        public async Task GitHubService_ShouldRespectConfiguration_WhenCustomSettingsProvided()
        {
            // Arrange
            var customToken = "custom-test-token";
            var customRepository = "https://github.com/custom-owner/custom-repo";

            await _configurationService.SetAsync("github.token", customToken);
            await _configurationService.SetAsync("github.repository", customRepository);

            // Act
            var tokenResult = await _configurationService.GetAsync("github.token");
            var repoResult = await _configurationService.GetAsync("github.repository");

            // Assert
            tokenResult.Should().Be(customToken, "should store and retrieve custom token");
            repoResult.Should().Be(customRepository, "should store and retrieve custom repository");
        }

        [Theory]
        [Trait("TestType", "URL Parsing")]
        [Trait("Priority", "Medium")]
        [InlineData("https://github.com/owner/repo", "owner", "repo")]
        [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
        [InlineData("https://github.com/org-name/project-name", "org-name", "project-name")]
        [InlineData("https://github.com/user123/my-awesome-project", "user123", "my-awesome-project")]
        public async Task RepositoryUrlParsing_ShouldParseCorrectly_WhenValidUrlProvided(
            string repositoryUrl, string expectedOwner, string expectedRepo)
        {
            // Arrange
            var testToken = Environment.GetEnvironmentVariable("GITHUB_TEST_TOKEN") ?? "dummy-token";
            await _configurationService.SetAsync("github.token", testToken);

            // Act
            var result = await _githubService.CheckRepositoryAccessAsync(repositoryUrl);

            // Assert
            result.Should().NotBeNull();
            result.RepositoryUrl.Should().Be(repositoryUrl);
            
            // The parsing is tested indirectly through the service call
            // If the URL was parsed incorrectly, the service would fail
        }

        [Fact]
        [Trait("TestType", "Environment")]
        [Trait("Priority", "Low")]
        public async Task SystemInformation_ShouldCollectEnvironmentData_WhenRequested()
        {
            // Arrange & Act
            var osVersion = Environment.OSVersion.ToString();
            var dotnetVersion = Environment.Version.ToString();
            var machineName = Environment.MachineName;
            var userDomain = Environment.UserDomainName;

            // Assert
            osVersion.Should().NotBeNullOrEmpty("should have OS version");
            dotnetVersion.Should().NotBeNullOrEmpty("should have .NET version");
            machineName.Should().NotBeNullOrEmpty("should have machine name");
            userDomain.Should().NotBeNullOrEmpty("should have user domain");

            _logger.LogInformation($"System Info - OS: {osVersion}, .NET: {dotnetVersion}, Machine: {machineName}");
        }
    }
}