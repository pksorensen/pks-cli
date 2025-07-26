using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Integration.GitHub;

/// <summary>
/// Integration tests for GitHub services with actual GitHub API
/// These tests require optional authentication and will be skipped if no token is provided
/// </summary>
[Collection("GitHub Integration Tests")]
public class GitHubIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly HttpClient _httpClient;
    private readonly TestConfigurationService _configService;
    private readonly GitHubAuthenticationService _authService;
    private readonly GitHubApiClient _apiClient;
    private readonly GitHubIssuesService _issuesService;
    private readonly string? _testToken;
    private readonly string? _testRepository;
    private readonly bool _runIntegrationTests;

    public GitHubIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _httpClient = new HttpClient();
        _configService = new TestConfigurationService();

        // Check for test configuration
        _testToken = Environment.GetEnvironmentVariable("GITHUB_TEST_TOKEN");
        _testRepository = Environment.GetEnvironmentVariable("GITHUB_TEST_REPOSITORY") ?? "pksorensen/pks-cli-test";
        _runIntegrationTests = !string.IsNullOrEmpty(_testToken);

        if (!_runIntegrationTests)
        {
            _output.WriteLine("GitHub integration tests skipped - no GITHUB_TEST_TOKEN environment variable found");
            _output.WriteLine("To run these tests, set GITHUB_TEST_TOKEN with a valid GitHub personal access token");
            _output.WriteLine("Optionally set GITHUB_TEST_REPOSITORY to specify a test repository (default: pksorensen/pks-cli-test)");
        }

        var config = new GitHubAuthConfig
        {
            ClientId = "test-client-id",
            ApiBaseUrl = "https://api.github.com"
        };

        _authService = new GitHubAuthenticationService(_httpClient, _configService, config);
        _apiClient = new GitHubApiClient(_httpClient, config);
        _issuesService = new GitHubIssuesService(_apiClient, _authService);

        // Set up authentication if token is available
        if (!string.IsNullOrEmpty(_testToken))
        {
            _apiClient.SetAuthenticationToken(_testToken);
            
            // Store token for auth service
            var storedToken = new GitHubStoredToken
            {
                AccessToken = _testToken,
                IsValid = true,
                CreatedAt = DateTime.UtcNow,
                LastValidated = DateTime.UtcNow,
                Scopes = new[] { "repo", "user:email" }
            };
            
            // Use async method properly
            Task.Run(async () => await _authService.StoreTokenAsync(storedToken)).Wait();
        }
    }

    [Fact]
    public async Task ValidateToken_WithActualToken_ShouldValidateSuccessfully()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test - no test token configured");
            return;
        }

        // Act
        var result = await _authService.ValidateTokenAsync(_testToken!);

        // Assert
        Assert.True(result.IsValid, $"Token validation failed: {result.ErrorMessage}");
        Assert.NotEmpty(result.Scopes);
        _output.WriteLine($"Token validated successfully with scopes: {string.Join(", ", result.Scopes)}");
    }

    [Fact]
    public async Task GetRateLimit_WithActualAPI_ShouldReturnCurrentLimits()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test - no test token configured");
            return;
        }

        // Act
        var result = await _apiClient.GetRateLimitAsync();

        // Assert
        Assert.True(result.Limit > 0);
        Assert.True(result.Remaining >= 0);
        Assert.True(result.Used >= 0);
        Assert.Equal("core", result.Resource);
        
        _output.WriteLine($"Rate limit: {result.Remaining}/{result.Limit} remaining, resets at {result.ResetTime}");
    }

    [Fact]
    public async Task GetRepository_WithTestRepository_ShouldReturnRepositoryInfo()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test - no test token configured");
            return;
        }

        // Arrange
        var parts = _testRepository!.Split('/');
        var owner = parts[0];
        var repo = parts[1];

        // Act
        var result = await _apiClient.GetAsync<dynamic>($"repos/{owner}/{repo}");

        // Assert
        Assert.NotNull(result);
        _output.WriteLine($"Retrieved repository: {result?.name} (ID: {result?.id})");
    }

    [Fact]
    public async Task CreateAndDeleteIssue_WithTestRepository_ShouldWorkCorrectly()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test - no test token configured");
            return;
        }

        // Arrange
        var parts = _testRepository!.Split('/');
        var owner = parts[0];
        var repo = parts[1];
        
        var createRequest = new CreateIssueRequest
        {
            Title = $"Integration Test Issue - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}",
            Body = "This issue was created by the PKS CLI integration tests and will be closed automatically.",
            Labels = new List<string> { "test", "automated" }
        };

        GitHubIssueDetailed? createdIssue = null;

        try
        {
            // Act - Create issue
            createdIssue = await _issuesService.CreateIssueAsync(owner, repo, createRequest);

            // Assert creation
            Assert.NotNull(createdIssue);
            Assert.Equal(createRequest.Title, createdIssue.Title);
            Assert.Equal(createRequest.Body, createdIssue.Body);
            Assert.Equal("open", createdIssue.State);
            
            _output.WriteLine($"Created issue #{createdIssue.Number}: {createdIssue.Title}");

            // Act - Close issue
            var closedIssue = await _issuesService.CloseIssueAsync(owner, repo, createdIssue.Number, "Automated test cleanup");

            // Assert closure
            Assert.Equal("closed", closedIssue.State);
            _output.WriteLine($"Closed issue #{closedIssue.Number}");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Integration test failed: {ex.Message}");
            
            // Cleanup attempt if issue was created
            if (createdIssue != null)
            {
                try
                {
                    await _issuesService.CloseIssueAsync(owner, repo, createdIssue.Number, "Cleanup after failed test");
                    _output.WriteLine($"Cleaned up issue #{createdIssue.Number} after test failure");
                }
                catch (Exception cleanupEx)
                {
                    _output.WriteLine($"Failed to cleanup issue: {cleanupEx.Message}");
                }
            }
            
            throw;
        }
    }

    [Fact]
    public async Task ListIssues_WithTestRepository_ShouldReturnIssues()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test - no test token configured");
            return;
        }

        // Arrange
        var parts = _testRepository!.Split('/');
        var owner = parts[0];
        var repo = parts[1];
        
        var filter = new GitHubIssueFilter
        {
            State = "all", // Get both open and closed
            PerPage = 10
        };

        // Act
        var result = await _issuesService.ListIssuesAsync(owner, repo, filter);

        // Assert
        Assert.NotNull(result);
        _output.WriteLine($"Retrieved {result.Count} issues from {_testRepository}");
        
        foreach (var issue in result.Take(3)) // Log first 3 issues
        {
            _output.WriteLine($"  Issue #{issue.Number}: {issue.Title} ({issue.State})");
        }
    }

    [Fact]
    public async Task SearchIssues_WithTestQuery_ShouldReturnResults()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test - no test token configured");     
            return;
        }

        // Arrange
        var query = new GitHubIssueSearchQuery
        {
            Repository = _testRepository,
            State = "all",
            PerPage = 5
        };

        // Act
        var result = await _issuesService.SearchIssuesAsync(query);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.TotalCount >= 0);
        
        _output.WriteLine($"Search returned {result.TotalCount} total issues, showing {result.Issues.Count}");
        
        foreach (var issue in result.Issues.Take(3))
        {
            _output.WriteLine($"  Found: #{issue.Number} - {issue.Title}");
        }
    }

    [Fact]
    public async Task GetIssueStatistics_WithTestRepository_ShouldReturnStats()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test - no test token configured");
            return;
        }

        // Arrange
        var parts = _testRepository!.Split('/');
        var owner = parts[0];
        var repo = parts[1];

        // Act
        var result = await _issuesService.GetIssueStatisticsAsync(owner, repo);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.TotalCount >= 0);
        Assert.Equal(result.OpenCount + result.ClosedCount, result.TotalCount);
        
        _output.WriteLine($"Repository statistics:");
        _output.WriteLine($"  Open issues: {result.OpenCount}");
        _output.WriteLine($"  Closed issues: {result.ClosedCount}");
        _output.WriteLine($"  Total issues: {result.TotalCount}");
        
        if (result.LabelDistribution.Any())
        {
            _output.WriteLine($"  Top labels: {string.Join(", ", result.LabelDistribution.Take(5).Select(kv => $"{kv.Key}({kv.Value})"))}");
        }
    }

    [Fact]
    public async Task HandleRateLimiting_ShouldRespectLimits()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test - no test token configured");
            return;
        }

        // Act - Get initial rate limit
        var initialRateLimit = await _apiClient.GetRateLimitAsync();
        _output.WriteLine($"Initial rate limit: {initialRateLimit.Remaining}/{initialRateLimit.Limit}");

        // Make a few API calls
        for (int i = 0; i < 3; i++)
        {
            await _apiClient.GetAsync<dynamic>("user");
        }

        // Act - Get updated rate limit
        var updatedRateLimit = await _apiClient.GetRateLimitAsync();
        _output.WriteLine($"Updated rate limit: {updatedRateLimit.Remaining}/{updatedRateLimit.Limit}");

        // Assert - Rate limit should have decreased (or stayed the same if cached)
        Assert.True(updatedRateLimit.Remaining <= initialRateLimit.Remaining);
    }

    [Fact]
    public async Task ErrorHandling_WithInvalidEndpoint_ShouldThrowCorrectException()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test - no test token configured");
            return;
        }

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => _apiClient.GetAsync<dynamic>("nonexistent/endpoint/that/does/not/exist"));
        
        Assert.Equal(System.Net.HttpStatusCode.NotFound, exception.StatusCode);
        _output.WriteLine($"Correctly caught exception: {exception.Message}");
    }

    [Fact]
    public async Task BatchOperations_WithTestRepository_ShouldHandleMultipleRequests()
    {
        if (!_runIntegrationTests)
        {
            _output.WriteLine("Skipping integration test - no test token configured");
            return;
        }

        // Arrange
        var parts = _testRepository!.Split('/');
        var owner = parts[0];
        var repo = parts[1];

        var batchRequest = new GitHubBatchIssueRequest
        {
            Owner = owner,
            Repository = repo,
            ContinueOnError = true,
            Issues = new List<CreateIssueRequest>
            {
                new() 
                { 
                    Title = $"Batch Test Issue 1 - {DateTime.UtcNow:HH:mm:ss}",
                    Body = "First batch test issue",
                    Labels = new List<string> { "test", "batch" }
                },
                new() 
                { 
                    Title = $"Batch Test Issue 2 - {DateTime.UtcNow:HH:mm:ss}",
                    Body = "Second batch test issue",
                    Labels = new List<string> { "test", "batch" }
                }
            }
        };

        var progressReports = new List<int>();
        var progress = new Progress<int>(p => progressReports.Add(p));
        var createdIssues = new List<GitHubIssueDetailed>();

        try
        {
            // Act
            var result = await _issuesService.CreateIssuesBatchAsync(batchRequest, progress);
            createdIssues.AddRange(result.CreatedIssues);

            // Assert
            Assert.True(result.SuccessfullyCreated > 0);
            Assert.Equal(2, progressReports.Count);
            
            _output.WriteLine($"Batch operation completed: {result.SuccessfullyCreated}/{result.TotalRequested} successful");
            _output.WriteLine($"Total time: {result.TotalTime.TotalSeconds:F2} seconds");

            // Cleanup created issues
            foreach (var issue in createdIssues)
            {
                await _issuesService.CloseIssueAsync(owner, repo, issue.Number, "Cleanup batch test");
                _output.WriteLine($"Cleaned up issue #{issue.Number}");
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"Batch test failed: {ex.Message}");
            
            // Cleanup any created issues
            foreach (var issue in createdIssues)
            {
                try
                {
                    await _issuesService.CloseIssueAsync(owner, repo, issue.Number, "Cleanup after failed batch test");
                }
                catch (Exception cleanupEx)
                {
                    _output.WriteLine($"Failed to cleanup issue #{issue.Number}: {cleanupEx.Message}");
                }
            }
            
            throw;
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        _apiClient?.Dispose();
    }
}

/// <summary>
/// Test configuration service for integration tests
/// </summary>
public class TestConfigurationService : IConfigurationService
{
    private readonly Dictionary<string, string> _config = new();

    public async Task<string?> GetAsync(string key)
    {
        await Task.Delay(1); // Simulate async operation
        return _config.TryGetValue(key, out var value) ? value : null;
    }

    public async Task SetAsync(string key, string value, bool global = false, bool encrypt = false)
    {
        await Task.Delay(1); // Simulate async operation
        _config[key] = value; // For tests, ignore encryption
    }

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        await Task.Delay(1); // Simulate async operation
        return new Dictionary<string, string>(_config);
    }

    public async Task DeleteAsync(string key)
    {
        await Task.Delay(1); // Simulate async operation
        _config.Remove(key);
    }
}

/// <summary>
/// Test collection to ensure integration tests don't run in parallel
/// </summary>
[CollectionDefinition("GitHub Integration Tests")]
public class GitHubIntegrationTestCollection : ICollectionFixture<GitHubIntegrationTestFixture>
{
}

/// <summary>
/// Fixture for GitHub integration tests
/// </summary>
public class GitHubIntegrationTestFixture
{
    public GitHubIntegrationTestFixture()
    {
        // Any shared setup for integration tests can go here
    }
}