using PKS.CLI.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace PKS.CLI.Tests.Integration.GitHub
{
    /// <summary>
    /// Base class for GitHub integration tests
    /// Provides common setup and utilities for testing GitHub API interactions
    /// </summary>
    public abstract class IntegrationTestBase : TestBase, IAsyncLifetime
    {
        protected IntegrationTestBase()
        {
            // Additional setup for integration tests
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            
            // Configure services specifically for integration tests
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });
        }

        /// <summary>
        /// Initialize async resources for integration tests
        /// </summary>
        public virtual Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Cleanup async resources after integration tests
        /// </summary>
        public virtual Task DisposeAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Check if integration tests should be skipped due to missing configuration
        /// </summary>
        protected bool ShouldSkipIntegrationTest(out string reason)
        {
            var testToken = Environment.GetEnvironmentVariable("GITHUB_TEST_TOKEN");
            if (string.IsNullOrEmpty(testToken))
            {
                reason = "GITHUB_TEST_TOKEN environment variable not set";
                return true;
            }

            var testRepository = Environment.GetEnvironmentVariable("GITHUB_TEST_REPOSITORY");
            if (string.IsNullOrEmpty(testRepository))
            {
                reason = "GITHUB_TEST_REPOSITORY environment variable not set";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        /// <summary>
        /// Skip test with informative message if integration test environment is not configured
        /// </summary>
        protected void SkipIfNotConfigured()
        {
            if (ShouldSkipIntegrationTest(out var reason))
            {
                Skip.If(true, reason);
            }
        }

        /// <summary>
        /// Get test GitHub token from environment
        /// </summary>
        protected string GetTestToken()
        {
            return Environment.GetEnvironmentVariable("GITHUB_TEST_TOKEN") 
                ?? throw new InvalidOperationException("GITHUB_TEST_TOKEN not configured");
        }

        /// <summary>
        /// Get test repository URL from environment
        /// </summary>
        protected string GetTestRepository()
        {
            return Environment.GetEnvironmentVariable("GITHUB_TEST_REPOSITORY") 
                ?? "https://github.com/pksorensen/pks-cli";
        }

        /// <summary>
        /// Check if issue creation is allowed in the test environment
        /// </summary>
        protected bool IsIssueCreationAllowed()
        {
            return Environment.GetEnvironmentVariable("GITHUB_ALLOW_ISSUE_CREATION") == "true";
        }
    }
}