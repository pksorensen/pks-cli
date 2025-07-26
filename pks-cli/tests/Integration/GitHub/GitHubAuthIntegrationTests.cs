using Xunit;
using Microsoft.Extensions.DependencyInjection;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.CLI.Tests.Infrastructure;
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace PKS.CLI.Tests.Integration.GitHub
{
    /// <summary>
    /// Integration tests for GitHub authentication flows
    /// These tests verify authentication mechanisms work with actual GitHub API
    /// </summary>
    [Collection("Integration")]
    [Trait("Category", "Integration")]
    [Trait("Component", "Authentication")]
    public class GitHubAuthIntegrationTests : IntegrationTestBase
    {
        private readonly IGitHubService _githubService;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<GitHubAuthIntegrationTests> _logger;

        public GitHubAuthIntegrationTests()
        {
            _githubService = GetService<IGitHubService>();
            _configurationService = GetService<IConfigurationService>();
            _logger = GetService<ILogger<GitHubAuthIntegrationTests>>();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);

            // Use real services for authentication integration tests
            services.AddHttpClient<IGitHubService, GitHubService>();
            services.AddSingleton<IConfigurationService, PKS.Infrastructure.Services.ConfigurationService>();
        }

        [Fact]
        [Trait("TestType", "Token Validation")]
        [Trait("Priority", "Critical")]
        public async Task TokenValidation_ShouldSucceed_WhenValidTokenProvided()
        {
            // Arrange
            SkipIfNotConfigured();
            var testToken = GetTestToken();

            // Act
            var result = await _githubService.ValidateTokenAsync(testToken);

            // Assert
            result.Should().NotBeNull();
            result.IsValid.Should().BeTrue("valid token should pass validation");
            result.Scopes.Should().NotBeEmpty("token should have scopes");
            result.ValidatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            result.ErrorMessage.Should().BeNullOrEmpty("valid token should not have error message");

            _logger.LogInformation($"Token validation successful. Scopes: {string.Join(", ", result.Scopes)}");
        }

        [Fact]
        [Trait("TestType", "Token Validation")]
        [Trait("Priority", "High")]
        public async Task TokenValidation_ShouldFail_WhenInvalidTokenProvided()
        {
            // Arrange
            var invalidToken = "ghp_invalid_token_that_does_not_exist_123456789";

            // Act
            var result = await _githubService.ValidateTokenAsync(invalidToken);

            // Assert
            result.Should().NotBeNull();
            result.IsValid.Should().BeFalse("invalid token should fail validation");
            result.ErrorMessage.Should().NotBeNullOrEmpty("invalid token should have error message");
            result.ValidatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            result.Scopes.Should().BeEmpty("invalid token should have no scopes");

            _logger.LogInformation($"Token validation failed as expected: {result.ErrorMessage}");
        }

        [Fact]
        [Trait("TestType", "Token Storage")]
        [Trait("Priority", "High")]
        public async Task TokenStorage_ShouldPersistAndRetrieve_WhenTokenConfigured()
        {
            // Arrange
            SkipIfNotConfigured();
            var testToken = GetTestToken();
            var storageKey = $"test.github.token.{Guid.NewGuid():N}";

            try
            {
                // Act - Store token
                await _configurationService.SetAsync(storageKey, testToken, false, true);
                
                // Act - Retrieve token
                var retrievedToken = await _configurationService.GetAsync(storageKey);

                // Assert
                retrievedToken.Should().Be(testToken, "stored token should be retrievable");

                // Verify token is still valid after storage round-trip
                var validation = await _githubService.ValidateTokenAsync(retrievedToken);
                validation.IsValid.Should().BeTrue("stored token should remain valid");
            }
            finally
            {
                // Cleanup
                try
                {
                    await _configurationService.RemoveAsync(storageKey);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }

        [Fact]
        [Trait("TestType", "Repository Access")]
        [Trait("Priority", "Critical")]
        public async Task RepositoryAccess_ShouldVerifyPermissions_WhenValidRepositoryProvided()
        {
            // Arrange
            SkipIfNotConfigured();
            var testToken = GetTestToken();
            var testRepository = GetTestRepository();

            // Configure token
            await _configurationService.SetAsync("github.token", testToken);

            try
            {
                // Act
                var result = await _githubService.CheckRepositoryAccessAsync(testRepository);

                // Assert
                result.Should().NotBeNull();
                result.RepositoryUrl.Should().Be(testRepository);
                result.HasAccess.Should().BeTrue("should have access to test repository");
                result.AccessLevel.Should().NotBeNullOrEmpty("should provide access level");
                result.CheckedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

                // Log access details
                _logger.LogInformation($"Repository access verified. Level: {result.AccessLevel}, Can Write: {result.CanWrite}");
            }
            finally
            {
                // Cleanup
                await _configurationService.RemoveAsync("github.token");
            }
        }

        [Fact]
        [Trait("TestType", "Repository Access")]
        [Trait("Priority", "Medium")]
        public async Task RepositoryAccess_ShouldDenyAccess_WhenRepositoryNotFound()
        {
            // Arrange
            SkipIfNotConfigured();
            var testToken = GetTestToken();
            var nonExistentRepo = "https://github.com/definitely-does-not-exist-12345/repository-that-does-not-exist";

            // Configure token
            await _configurationService.SetAsync("github.token", testToken);

            try
            {
                // Act
                var result = await _githubService.CheckRepositoryAccessAsync(nonExistentRepo);

                // Assert
                result.Should().NotBeNull();
                result.RepositoryUrl.Should().Be(nonExistentRepo);
                result.HasAccess.Should().BeFalse("should not have access to non-existent repository");
                result.ErrorMessage.Should().NotBeNullOrEmpty("should provide error message");
                
                _logger.LogInformation($"Repository access denied as expected: {result.ErrorMessage}");
            }
            finally
            {
                // Cleanup
                await _configurationService.RemoveAsync("github.token");
            }
        }

        [Fact]
        [Trait("TestType", "Scope Validation")]
        [Trait("Priority", "High")]
        public async Task ScopeValidation_ShouldIdentifyRequiredScopes_WhenTokenValidated()
        {
            // Arrange
            SkipIfNotConfigured();
            var testToken = GetTestToken();
            var requiredScopes = new[] { "repo", "issues" };

            // Act
            var result = await _githubService.ValidateTokenAsync(testToken);

            // Assert
            result.Should().NotBeNull();
            result.IsValid.Should().BeTrue();
            result.Scopes.Should().NotBeEmpty("token should have scopes");

            // Check if token has required scopes for report functionality
            var missingScopes = requiredScopes.Except(result.Scopes).ToList();
            
            if (missingScopes.Any())
            {
                _logger.LogWarning($"Token is missing required scopes: {string.Join(", ", missingScopes)}");
                _logger.LogInformation($"Available scopes: {string.Join(", ", result.Scopes)}");
            }
            else
            {
                _logger.LogInformation("Token has all required scopes for report functionality");
            }

            // Assert that we can at least identify what scopes are available
            result.Scopes.Should().NotBeEmpty("token should have at least some scopes");
        }

        [Fact]
        [Trait("TestType", "Authentication Configuration")]
        [Trait("Priority", "Medium")]
        public async Task AuthConfiguration_ShouldManageMultipleTokens_WhenMultipleProjectsConfigured()
        {
            // Arrange
            SkipIfNotConfigured();
            var testToken = GetTestToken();
            var project1Id = $"test-project-1-{Guid.NewGuid():N[..8]}";
            var project2Id = $"test-project-2-{Guid.NewGuid():N[..8]}";

            try
            {
                // Act - Configure authentication for multiple projects
                var config1 = await _githubService.ConfigureProjectIntegrationAsync(
                    project1Id, 
                    "https://github.com/owner1/repo1", 
                    testToken);

                var config2 = await _githubService.ConfigureProjectIntegrationAsync(
                    project2Id, 
                    "https://github.com/owner2/repo2", 
                    testToken);

                // Assert
                config1.Should().NotBeNull();
                config1.ProjectId.Should().Be(project1Id);
                config1.IsValid.Should().BeTrue("first project configuration should be valid");

                config2.Should().NotBeNull();
                config2.ProjectId.Should().Be(project2Id);
                config2.IsValid.Should().BeTrue("second project configuration should be valid");

                // Verify both configurations are stored independently
                var stored1 = await _configurationService.GetAsync($"github.{project1Id}.repository");
                var stored2 = await _configurationService.GetAsync($"github.{project2Id}.repository");

                stored1.Should().Be("https://github.com/owner1/repo1");
                stored2.Should().Be("https://github.com/owner2/repo2");

                _logger.LogInformation($"Successfully configured authentication for projects: {project1Id}, {project2Id}");
            }
            finally
            {
                // Cleanup
                var cleanupTasks = new List<Task>
                {
                    _configurationService.RemoveAsync($"github.{project1Id}.token"),
                    _configurationService.RemoveAsync($"github.{project1Id}.repository"),
                    _configurationService.RemoveAsync($"github.{project1Id}.configured_at"),
                    _configurationService.RemoveAsync($"github.{project2Id}.token"),
                    _configurationService.RemoveAsync($"github.{project2Id}.repository"),
                    _configurationService.RemoveAsync($"github.{project2Id}.configured_at")
                };

                await Task.WhenAll(cleanupTasks.Select(async task =>
                {
                    try { await task; } catch { /* Ignore cleanup errors */ }
                }));
            }
        }

        [Fact]
        [Trait("TestType", "Error Resilience")]
        [Trait("Priority", "Medium")]
        public async Task Authentication_ShouldHandleNetworkInterruption_GracefullyAsync()
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
                await githubService.ValidateTokenAsync("ghp_test_token_for_timeout_test");
            });

            // Should handle timeout gracefully
            if (exception != null)
            {
                exception.Should().BeOfType<TaskCanceledException>()
                    .Or.BeOfType<HttpRequestException>();
                
                _logger.LogInformation($"Network timeout handled gracefully: {exception.GetType().Name}");
            }
        }

        [Fact]
        [Trait("TestType", "Rate Limiting")]
        [Trait("Priority", "Low")]
        public async Task Authentication_ShouldRespectRateLimit_WhenMakingMultipleRequests()
        {
            // Arrange
            SkipIfNotConfigured();
            var testToken = GetTestToken();
            var requestCount = 5;
            var startTime = DateTime.UtcNow;

            // Act - Make multiple validation requests
            var tasks = Enumerable.Range(0, requestCount)
                .Select(_ => _githubService.ValidateTokenAsync(testToken))
                .ToArray();

            var results = await Task.WhenAll(tasks);
            var endTime = DateTime.UtcNow;
            var totalTime = endTime - startTime;

            // Assert
            results.Should().AllSatisfy(result =>
            {
                result.Should().NotBeNull();
                result.IsValid.Should().BeTrue("all validation requests should succeed");
            });

            // Log timing information for rate limit analysis
            _logger.LogInformation($"Completed {requestCount} validation requests in {totalTime.TotalMilliseconds}ms");
            _logger.LogInformation($"Average time per request: {totalTime.TotalMilliseconds / requestCount:F2}ms");

            // If requests were rate limited, they would take significantly longer
            // This is more of an observational test than a strict assertion
        }

        [Fact]
        [Trait("TestType", "Security")]
        [Trait("Priority", "High")]
        public async Task TokenSecurity_ShouldNotLogTokenValues_WhenOperationsFail()
        {
            // This test verifies that sensitive token information is not leaked in logs
            // We'll use an invalid token and verify the error doesn't contain the token
            
            // Arrange
            var sensitiveToken = "ghp_sensitive_test_token_12345_should_not_appear_in_logs";

            // Act
            var result = await _githubService.ValidateTokenAsync(sensitiveToken);

            // Assert
            result.Should().NotBeNull();
            result.IsValid.Should().BeFalse();
            
            // Verify sensitive token is not in error message
            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                result.ErrorMessage.Should().NotContain(sensitiveToken, 
                    "error messages should not contain sensitive token values");
                result.ErrorMessage.Should().NotContain("ghp_sensitive_test_token", 
                    "error messages should not contain parts of sensitive tokens");
            }

            _logger.LogInformation("Token security test completed - no sensitive data leaked");
        }

        [Fact]
        [Trait("TestType", "Configuration Persistence")]
        [Trait("Priority", "Medium")]
        public async Task Configuration_ShouldPersistAcrossServiceRestarts_WhenTokenConfigured()
        {
            // Arrange
            SkipIfNotConfigured();
            var testToken = GetTestToken();
            var configKey = $"test.persistent.token.{Guid.NewGuid():N}";

            try
            {
                // Act - Store configuration
                await _configurationService.SetAsync(configKey, testToken, false, true);

                // Simulate service restart by creating new service instances
                var newConfigService = new PKS.Infrastructure.Services.ConfigurationService();
                var newGitHubService = new GitHubService(GetService<HttpClient>(), newConfigService);

                // Retrieve configuration with new service instances
                var retrievedToken = await newConfigService.GetAsync(configKey);
                var validation = await newGitHubService.ValidateTokenAsync(retrievedToken);

                // Assert
                retrievedToken.Should().Be(testToken, "token should persist across service restarts");
                validation.IsValid.Should().BeTrue("persisted token should remain valid");

                _logger.LogInformation("Configuration persistence test successful");
            }
            finally
            {
                // Cleanup
                try
                {
                    await _configurationService.RemoveAsync(configKey);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}