using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.GitHub;

/// <summary>
/// Unit tests for mock GitHub services to ensure they behave correctly in testing scenarios
/// </summary>
public class MockGitHubServicesTests
{
    [Fact]
    public async Task MockGitHubAuthenticationService_InitiateDeviceCodeFlow_ShouldReturnValidResponse()
    {
        // Arrange
        var service = new MockGitHubAuthenticationService();

        // Act
        var result = await service.InitiateDeviceCodeFlowAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.DeviceCode);
        Assert.NotEmpty(result.UserCode);
        Assert.Equal("https://github.com/login/device", result.VerificationUri);
        Assert.Equal(900, result.ExpiresIn);
        Assert.Equal(5, result.Interval);
    }

    [Fact]
    public async Task MockGitHubAuthenticationService_PollForAuthentication_ShouldReturnVariousStates()
    {
        // Arrange
        var service = new MockGitHubAuthenticationService();
        var results = new List<GitHubDeviceAuthStatus>();

        // Act - Poll multiple times to get different states
        for (int i = 0; i < 10; i++)
        {
            var result = await service.PollForAuthenticationAsync("test-device-code");
            results.Add(result);
        }

        // Assert
        Assert.NotEmpty(results);

        // Should have at least some pending responses
        Assert.Contains(results, r => r.Error == "authorization_pending");

        // Should have some variety in responses
        var uniqueStates = results.Select(r => r.IsAuthenticated ? "authenticated" : r.Error).Distinct().Count();
        Assert.True(uniqueStates > 1, "Mock should return varied responses");
    }

    [Fact]
    public async Task MockGitHubAuthenticationService_AuthenticateAsync_ShouldCompleteSuccessfully()
    {
        // Arrange
        var service = new MockGitHubAuthenticationService();
        var progressReports = new List<GitHubAuthProgress>();
        var progress = new Progress<GitHubAuthProgress>(p => progressReports.Add(p));

        // Act
        var result = await service.AuthenticateAsync(progressCallback: progress);

        // Assert
        Assert.True(result.IsAuthenticated);
        Assert.NotEmpty(result.AccessToken!);
        Assert.StartsWith("ghp_mock_", result.AccessToken!);
        Assert.NotEmpty(result.Scopes);

        // Verify progress was reported
        Assert.NotEmpty(progressReports);
        Assert.Contains(progressReports, p => p.CurrentStep == GitHubAuthStep.Complete);
        Assert.Contains(progressReports, p => p.IsComplete);
    }

    [Fact]
    public async Task MockGitHubAuthenticationService_ValidateToken_ShouldValidateCorrectFormat()
    {
        // Arrange
        var service = new MockGitHubAuthenticationService();

        // Act
        var validResult = await service.ValidateTokenAsync("ghp_mock_validtoken123");
        var invalidResult = await service.ValidateTokenAsync("invalid_token");

        // Assert
        Assert.True(validResult.IsValid);
        Assert.NotEmpty(validResult.Scopes);
        Assert.Null(validResult.ErrorMessage);

        Assert.False(invalidResult.IsValid);
        Assert.NotNull(invalidResult.ErrorMessage);
    }

    [Fact]
    public async Task MockGitHubAuthenticationService_TokenStorage_ShouldWorkCorrectly()
    {
        // Arrange
        var service = new MockGitHubAuthenticationService();
        var token = new GitHubStoredToken
        {
            AccessToken = "ghp_test_token",
            Scopes = new[] { "repo", "user:email" },
            CreatedAt = DateTime.UtcNow,
            IsValid = true
        };

        // Act
        var storeResult = await service.StoreTokenAsync(token);
        var retrievedToken = await service.GetStoredTokenAsync();
        var isAuthenticated = await service.IsAuthenticatedAsync();

        // Assert
        Assert.True(storeResult);
        Assert.NotNull(retrievedToken);
        Assert.Equal("ghp_test_token", retrievedToken.AccessToken);
        Assert.True(isAuthenticated);
    }

    [Fact]
    public async Task MockGitHubApiClient_GetAsync_ShouldReturnMockData()
    {
        // Arrange
        var client = new MockGitHubApiClient();
        client.SetAuthenticationToken("test-token");

        // Act
        var rateLimit = await client.GetRateLimitAsync();
        var repository = await client.GetAsync<dynamic>("repos/owner/repo");

        // Assert
        Assert.Equal(5000, rateLimit.Limit);
        Assert.Equal("core", rateLimit.Resource);
        Assert.NotNull(repository);
    }

    [Fact]
    public async Task MockGitHubApiClient_Authentication_ShouldWorkCorrectly()
    {
        // Arrange
        var client = new MockGitHubApiClient();

        // Act & Assert - Initially not authenticated
        Assert.False(client.IsAuthenticated);

        // Set token
        client.SetAuthenticationToken("test-token");
        Assert.True(client.IsAuthenticated);

        // Clear token
        client.ClearAuthenticationToken();
        Assert.False(client.IsAuthenticated);
    }

    [Fact]
    public async Task MockGitHubApiClient_CRUD_Operations_ShouldWork()
    {
        // Arrange
        var client = new MockGitHubApiClient();
        client.SetAuthenticationToken("test-token");

        // Act
        var getResult = await client.GetAsync<dynamic>("repos/owner/repo");
        var postResult = await client.PostAsync<dynamic>("repos/owner/repo/issues", new { title = "Test" });
        var putResult = await client.PutAsync<dynamic>("repos/owner/repo/issues/1", new { title = "Updated" });
        var patchResult = await client.PatchAsync<dynamic>("repos/owner/repo/issues/1", new { state = "closed" });
        var deleteResult = await client.DeleteAsync("repos/owner/repo/issues/1/labels/bug");

        // Assert
        Assert.NotNull(getResult);
        Assert.NotNull(postResult);
        Assert.NotNull(putResult);
        Assert.NotNull(patchResult);
        Assert.True(deleteResult);
    }

    [Fact]
    public async Task MockGitHubIssuesService_CreateIssue_ShouldReturnMockIssue()
    {
        // Arrange
        var service = new MockGitHubIssuesService();
        var request = new CreateIssueRequest
        {
            Title = "Test Issue",
            Body = "Test description",
            Labels = new List<string> { "bug", "enhancement" },
            Assignees = new List<string> { "testuser" }
        };

        // Act
        var result = await service.CreateIssueAsync("owner", "repo", request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Issue", result.Title);
        Assert.Equal("Test description", result.Body);
        Assert.Equal("open", result.State);
        Assert.Contains("bug", result.Labels);
        Assert.Contains("enhancement", result.Labels);
        Assert.Contains(result.Assignees, a => a.Login == "testuser");
        Assert.Equal("https://github.com/owner/repo", result.RepositoryUrl);
    }

    [Fact]
    public async Task MockGitHubIssuesService_BatchCreateIssues_ShouldProcessAll()
    {
        // Arrange
        var service = new MockGitHubIssuesService();
        var batchRequest = new GitHubBatchIssueRequest
        {
            Owner = "owner",
            Repository = "repo",
            Issues = new List<CreateIssueRequest>
            {
                new() { Title = "Issue 1", Body = "Body 1" },
                new() { Title = "Issue 2", Body = "Body 2" },
                new() { Title = "Issue 3", Body = "Body 3" }
            }
        };

        var progressReports = new List<int>();
        var progress = new Progress<int>(p => progressReports.Add(p));

        // Act
        var result = await service.CreateIssuesBatchAsync(batchRequest, progress);

        // Assert
        Assert.Equal(3, result.TotalRequested);
        Assert.True(result.SuccessfullyCreated > 0);
        Assert.Equal(result.TotalRequested - result.SuccessfullyCreated, result.Failed);
        Assert.NotEmpty(progressReports);
        Assert.True(result.TotalTime > TimeSpan.Zero);
    }

    [Fact]
    public async Task MockGitHubIssuesService_GetIssue_ShouldReturnExistingOrMock()
    {
        // Arrange
        var service = new MockGitHubIssuesService();

        // Act
        var result = await service.GetIssueAsync("owner", "repo", 42);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(42, result.Number);
        Assert.NotEmpty(result.Title);
        Assert.NotEmpty(result.Body);
        Assert.Equal("https://github.com/owner/repo", result.RepositoryUrl);
    }

    [Fact]
    public async Task MockGitHubIssuesService_ListIssues_ShouldReturnMockList()
    {
        // Arrange
        var service = new MockGitHubIssuesService();
        var filter = new GitHubIssueFilter
        {
            State = "open",
            PerPage = 10
        };

        // Act
        var result = await service.ListIssuesAsync("owner", "repo", filter);

        // Assert
        Assert.NotEmpty(result);
        Assert.True(result.Count <= 10);
        Assert.All(result, issue =>
        {
            Assert.NotEmpty(issue.Title);
            Assert.Equal("https://github.com/owner/repo", issue.RepositoryUrl);
        });
    }

    [Fact]
    public async Task MockGitHubIssuesService_UpdateIssue_ShouldModifyIssue()
    {
        // Arrange
        var service = new MockGitHubIssuesService();

        // First create an issue
        var createRequest = new CreateIssueRequest { Title = "Original Title", Body = "Original body" };
        var createdIssue = await service.CreateIssueAsync("owner", "repo", createRequest);

        var updateRequest = new UpdateIssueRequest
        {
            Title = "Updated Title",
            State = "closed",
            Labels = new List<string> { "resolved" }
        };

        // Act
        var result = await service.UpdateIssueAsync("owner", "repo", createdIssue.Number, updateRequest);

        // Assert
        Assert.Equal("Updated Title", result.Title);
        Assert.Equal("closed", result.State);
        Assert.Contains("resolved", result.Labels);
        Assert.True(result.UpdatedAt > createdIssue.UpdatedAt);
    }

    [Fact]
    public async Task MockGitHubIssuesService_CloseAndReopenIssue_ShouldUpdateState()
    {
        // Arrange
        var service = new MockGitHubIssuesService();
        var createRequest = new CreateIssueRequest { Title = "Test Issue", Body = "Test body" };
        var createdIssue = await service.CreateIssueAsync("owner", "repo", createRequest);

        // Act
        var closedIssue = await service.CloseIssueAsync("owner", "repo", createdIssue.Number);
        var reopenedIssue = await service.ReopenIssueAsync("owner", "repo", createdIssue.Number);

        // Assert
        Assert.Equal("closed", closedIssue.State);
        Assert.NotNull(closedIssue.ClosedAt);

        Assert.Equal("open", reopenedIssue.State);
        Assert.Null(reopenedIssue.ClosedAt);
    }

    [Fact]
    public async Task MockGitHubIssuesService_LabelManagement_ShouldWork()
    {
        // Arrange
        var service = new MockGitHubIssuesService();
        var createRequest = new CreateIssueRequest
        {
            Title = "Test Issue",
            Body = "Test body",
            Labels = new List<string> { "initial-label" }
        };
        var createdIssue = await service.CreateIssueAsync("owner", "repo", createRequest);

        // Act
        var addedLabels = await service.AddLabelsAsync("owner", "repo", createdIssue.Number,
            new List<string> { "bug", "priority-high" });

        var remainingLabels = await service.RemoveLabelsAsync("owner", "repo", createdIssue.Number,
            new List<string> { "initial-label" });

        // Assert
        Assert.Contains("bug", addedLabels);
        Assert.Contains("priority-high", addedLabels);

        Assert.DoesNotContain("initial-label", remainingLabels);
    }

    [Fact]
    public async Task MockGitHubIssuesService_SearchIssues_ShouldReturnResults()
    {
        // Arrange
        var service = new MockGitHubIssuesService();
        var query = new GitHubIssueSearchQuery
        {
            Keywords = "bug fix",
            Repository = "owner/repo",
            State = "open"
        };

        // Act
        var result = await service.SearchIssuesAsync(query);

        // Assert
        Assert.True(result.TotalCount > 0);
        Assert.NotEmpty(result.Issues);
        Assert.False(result.IncompleteResults);
    }

    [Fact]
    public async Task MockGitHubIssuesService_GetStatistics_ShouldReturnValidStats()
    {
        // Arrange
        var service = new MockGitHubIssuesService();

        // Act
        var result = await service.GetIssueStatisticsAsync("owner", "repo");

        // Assert
        Assert.True(result.OpenCount >= 0);
        Assert.True(result.ClosedCount >= 0);
        Assert.Equal(result.OpenCount + result.ClosedCount, result.TotalCount);
        Assert.NotEmpty(result.LabelDistribution);
        Assert.NotEmpty(result.AssigneeDistribution);
        Assert.True(result.LastUpdated <= DateTime.UtcNow);
    }

    [Fact]
    public async Task MockGitHubIssuesService_AssignUsers_ShouldReturnAssignees()
    {
        // Arrange
        var service = new MockGitHubIssuesService();
        var assignees = new List<string> { "user1", "user2", "user3" };

        // Act
        var result = await service.AssignUsersAsync("owner", "repo", 1, assignees);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Contains(result, u => u.Login == "user1");
        Assert.Contains(result, u => u.Login == "user2");
        Assert.Contains(result, u => u.Login == "user3");
    }

    [Fact]
    public void MockGitHubApiClient_Dispose_ShouldNotThrow()
    {
        // Arrange
        var client = new MockGitHubApiClient();

        // Act & Assert
        client.Dispose(); // Should not throw
    }

    [Fact]
    public async Task MockServices_CancellationToken_ShouldBeRespected()
    {
        // Arrange
        var authService = new MockGitHubAuthenticationService();
        var apiClient = new MockGitHubApiClient();
        var issuesService = new MockGitHubIssuesService();

        var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => authService.InitiateDeviceCodeFlowAsync(cancellationToken: cancellationTokenSource.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => apiClient.GetAsync<dynamic>("test", cancellationTokenSource.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => issuesService.CreateIssueAsync("owner", "repo",
                new CreateIssueRequest { Title = "Test" }, cancellationTokenSource.Token));
    }
}