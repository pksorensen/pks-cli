using System.Net;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.GitHub;

/// <summary>
/// Comprehensive unit tests for GitHubIssuesService
/// </summary>
public class GitHubIssuesServiceTests
{
    private readonly Mock<IGitHubApiClient> _mockApiClient;
    private readonly Mock<IGitHubAuthenticationService> _mockAuthService;
    private readonly GitHubIssuesService _issuesService;

    public GitHubIssuesServiceTests()
    {
        _mockApiClient = new Mock<IGitHubApiClient>();
        _mockAuthService = new Mock<IGitHubAuthenticationService>();
        _issuesService = new GitHubIssuesService(_mockApiClient.Object, _mockAuthService.Object);

        // Setup default authentication
        _mockApiClient.Setup(x => x.IsAuthenticated).Returns(true);
    }

    [Fact]
    public async Task CreateIssueAsync_WithValidRequest_ShouldReturnCreatedIssue()
    {
        // Arrange
        var request = new CreateIssueRequest
        {
            Title = "Test Issue",
            Body = "Test description",
            Labels = new List<string> { "bug", "enhancement" },
            Assignees = new List<string> { "testuser" }
        };

        var mockResponse = new
        {
            id = 123456L,
            number = 42,
            title = "Test Issue",
            body = "Test description",
            state = "open",
            html_url = "https://github.com/owner/repo/issues/42",
            created_at = DateTime.UtcNow.ToString("O"),
            updated_at = DateTime.UtcNow.ToString("O"),
            closed_at = (string?)null,
            comments = 0,
            locked = false,
            labels = new[] { new { name = "bug" }, new { name = "enhancement" } },
            assignees = new[] { new { login = "testuser", name = "Test User" } },
            user = new { login = "creator", name = "Issue Creator" },
            milestone = (object?)null
        };

        _mockApiClient
            .Setup(x => x.PostAsync<dynamic>("repos/owner/repo/issues", It.IsAny<object>(), default))
            .ReturnsAsync(mockResponse);

        // Act
        var result = await _issuesService.CreateIssueAsync("owner", "repo", request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(123456L, result.Id);
        Assert.Equal(42, result.Number);
        Assert.Equal("Test Issue", result.Title);
        Assert.Equal("Test description", result.Body);
        Assert.Equal("open", result.State);
        Assert.Contains("bug", result.Labels);
        Assert.Contains("enhancement", result.Labels);
        Assert.Contains(result.Assignees, a => a.Login == "testuser");
    }

    [Fact]
    public async Task CreateIssueAsync_WhenNotAuthenticated_ShouldThrowUnauthorizedAccessException()
    {
        // Arrange
        _mockApiClient.Setup(x => x.IsAuthenticated).Returns(false);
        _mockAuthService.Setup(x => x.GetStoredTokenAsync(null)).ReturnsAsync((GitHubStoredToken?)null);

        var request = new CreateIssueRequest { Title = "Test Issue", Body = "Test body" };

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _issuesService.CreateIssueAsync("owner", "repo", request));
    }

    [Fact]
    public async Task CreateIssuesBatchAsync_WithMultipleIssues_ShouldProcessAllSuccessfully()
    {
        // Arrange
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

        var mockResponses = new[]
        {
            CreateMockIssueResponse(1, "Issue 1"),
            CreateMockIssueResponse(2, "Issue 2"),
            CreateMockIssueResponse(3, "Issue 3")
        };

        _mockApiClient
            .SetupSequence(x => x.PostAsync<dynamic>("repos/owner/repo/issues", It.IsAny<object>(), default))
            .ReturnsAsync(mockResponses[0])
            .ReturnsAsync(mockResponses[1])
            .ReturnsAsync(mockResponses[2]);

        var progressReports = new List<int>();
        var progress = new Progress<int>(p => progressReports.Add(p));

        // Act
        var result = await _issuesService.CreateIssuesBatchAsync(batchRequest, progress);

        // Assert
        Assert.Equal(3, result.TotalRequested);
        Assert.Equal(3, result.SuccessfullyCreated);
        Assert.Equal(0, result.Failed);
        Assert.Equal(3, result.CreatedIssues.Count);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { 1, 2, 3 }, progressReports);
    }

    [Fact]
    public async Task CreateIssuesBatchAsync_WithPartialFailures_ShouldContinueOnError()
    {
        // Arrange
        var batchRequest = new GitHubBatchIssueRequest
        {
            Owner = "owner",
            Repository = "repo",
            ContinueOnError = true,
            Issues = new List<CreateIssueRequest>
            {
                new() { Title = "Issue 1", Body = "Body 1" },
                new() { Title = "Issue 2", Body = "Body 2" },
                new() { Title = "Issue 3", Body = "Body 3" }
            }
        };

        _mockApiClient
            .SetupSequence(x => x.PostAsync<dynamic>("repos/owner/repo/issues", It.IsAny<object>(), default))
            .ReturnsAsync(CreateMockIssueResponse(1, "Issue 1"))
            .ThrowsAsync(new GitHubApiException("API Error", HttpStatusCode.BadRequest))
            .ReturnsAsync(CreateMockIssueResponse(3, "Issue 3"));

        // Act
        var result = await _issuesService.CreateIssuesBatchAsync(batchRequest);

        // Assert
        Assert.Equal(3, result.TotalRequested);
        Assert.Equal(2, result.SuccessfullyCreated);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.CreatedIssues.Count);
        Assert.Single(result.Errors);
        Assert.Equal("Issue 2", result.Errors[0].RequestTitle);
    }

    [Fact]
    public async Task GetIssueAsync_WithValidIssueNumber_ShouldReturnIssue()
    {
        // Arrange
        var mockResponse = CreateMockIssueResponse(42, "Test Issue");

        _mockApiClient
            .Setup(x => x.GetAsync<dynamic>("repos/owner/repo/issues/42", default))
            .ReturnsAsync(mockResponse);

        // Act
        var result = await _issuesService.GetIssueAsync("owner", "repo", 42);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(42, result.Number);
        Assert.Equal("Test Issue", result.Title);
    }

    [Fact]
    public async Task ListIssuesAsync_WithFilter_ShouldApplyFilterParameters()
    {
        // Arrange
        var filter = new GitHubIssueFilter
        {
            State = "closed",
            Labels = new List<string> { "bug" },
            Sort = "updated",
            Direction = "asc",
            PerPage = 50
        };

        var mockResponse = new List<dynamic>
        {
            CreateMockIssueResponse(1, "Issue 1"),
            CreateMockIssueResponse(2, "Issue 2")
        };

        _mockApiClient
            .Setup(x => x.GetAsync<List<dynamic>>("repos/owner/repo/issues?state=closed&labels=bug&sort=updated&direction=asc&per_page=50&page=1", default))
            .ReturnsAsync(mockResponse);

        // Act
        var result = await _issuesService.ListIssuesAsync("owner", "repo", filter);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, issue => Assert.Equal("https://github.com/owner/repo", issue.RepositoryUrl));
    }

    [Fact]
    public async Task UpdateIssueAsync_WithValidRequest_ShouldReturnUpdatedIssue()
    {
        // Arrange
        var updateRequest = new UpdateIssueRequest
        {
            Title = "Updated Title",
            State = "closed",
            Labels = new List<string> { "resolved" }
        };

        var mockResponse = CreateMockIssueResponse(42, "Updated Title");

        _mockApiClient
            .Setup(x => x.PatchAsync<dynamic>("repos/owner/repo/issues/42", It.IsAny<object>(), default))
            .ReturnsAsync(mockResponse);

        // Act
        var result = await _issuesService.UpdateIssueAsync("owner", "repo", 42, updateRequest);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Updated Title", result.Title);
    }

    [Fact]
    public async Task CloseIssueAsync_ShouldUpdateIssueStateToClosed()
    {
        // Arrange
        var mockResponse = CreateMockIssueResponse(42, "Test Issue", "closed");

        _mockApiClient
            .Setup(x => x.PatchAsync<dynamic>("repos/owner/repo/issues/42", It.IsAny<object>(), default))
            .ReturnsAsync(mockResponse);

        // Act
        var result = await _issuesService.CloseIssueAsync("owner", "repo", 42);

        // Assert
        Assert.Equal("closed", result.State);
        _mockApiClient.Verify(x => x.PatchAsync<dynamic>(
            "repos/owner/repo/issues/42",
            It.Is<object>(o => o.ToString()!.Contains("closed")),
            default), Times.Once);
    }

    [Fact]
    public async Task ReopenIssueAsync_ShouldUpdateIssueStateToOpen()
    {
        // Arrange
        var mockResponse = CreateMockIssueResponse(42, "Test Issue", "open");

        _mockApiClient
            .Setup(x => x.PatchAsync<dynamic>("repos/owner/repo/issues/42", It.IsAny<object>(), default))
            .ReturnsAsync(mockResponse);

        // Act
        var result = await _issuesService.ReopenIssueAsync("owner", "repo", 42);

        // Assert
        Assert.Equal("open", result.State);
    }

    [Fact]
    public async Task AddLabelsAsync_ShouldReturnUpdatedLabels()
    {
        // Arrange
        var labelsToAdd = new List<string> { "bug", "priority-high" };
        var mockResponse = new List<dynamic>
        {
            new { name = "bug" },
            new { name = "priority-high" },
            new { name = "existing-label" }
        };

        _mockApiClient
            .Setup(x => x.PostAsync<List<dynamic>>("repos/owner/repo/issues/42/labels", labelsToAdd, default))
            .ReturnsAsync(mockResponse);

        // Act
        var result = await _issuesService.AddLabelsAsync("owner", "repo", 42, labelsToAdd);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Contains("bug", result);
        Assert.Contains("priority-high", result);
        Assert.Contains("existing-label", result);
    }

    [Fact]
    public async Task RemoveLabelsAsync_ShouldRemoveSpecifiedLabels()
    {
        // Arrange
        var labelsToRemove = new List<string> { "bug" };
        var remainingLabels = new List<dynamic>
        {
            new { name = "enhancement" },
            new { name = "documentation" }
        };

        _mockApiClient
            .Setup(x => x.DeleteAsync("repos/owner/repo/issues/42/labels/bug", default))
            .ReturnsAsync(true);

        _mockApiClient
            .Setup(x => x.GetAsync<List<dynamic>>("repos/owner/repo/issues/42/labels", default))
            .ReturnsAsync(remainingLabels);

        // Act
        var result = await _issuesService.RemoveLabelsAsync("owner", "repo", 42, labelsToRemove);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("enhancement", result);
        Assert.Contains("documentation", result);
        Assert.DoesNotContain("bug", result);
    }

    [Fact]
    public async Task AssignUsersAsync_ShouldReturnAssignedUsers()
    {
        // Arrange
        var assignees = new List<string> { "user1", "user2" };
        var mockResponse = new
        {
            assignees = new[]
            {
                new { login = "user1", name = "User One" },
                new { login = "user2", name = "User Two" }
            }
        };

        _mockApiClient
            .Setup(x => x.PostAsync<dynamic>("repos/owner/repo/issues/42/assignees", It.IsAny<object>(), default))
            .ReturnsAsync(mockResponse);

        // Act
        var result = await _issuesService.AssignUsersAsync("owner", "repo", 42, assignees);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, u => u.Login == "user1");
        Assert.Contains(result, u => u.Login == "user2");
    }

    [Fact]
    public async Task SearchIssuesAsync_WithQuery_ShouldReturnSearchResults()
    {
        // Arrange
        var query = new GitHubIssueSearchQuery
        {
            Keywords = "bug fix",
            Repository = "owner/repo",
            State = "open",
            Labels = new List<string> { "bug" }
        };

        var mockResponse = new
        {
            total_count = 25,
            incomplete_results = false,
            items = new[]
            {
                CreateMockIssueResponse(1, "Bug fix 1"),
                CreateMockIssueResponse(2, "Bug fix 2")
            }
        };

        _mockApiClient
            .Setup(x => x.GetAsync<dynamic>(It.Is<string>(s => s.StartsWith("search/issues")), default))
            .ReturnsAsync(mockResponse);

        // Act
        var result = await _issuesService.SearchIssuesAsync(query);

        // Assert
        Assert.Equal(25, result.TotalCount);
        Assert.False(result.IncompleteResults);
        Assert.Equal(2, result.Issues.Count);
    }

    [Fact]
    public async Task GetIssueStatisticsAsync_ShouldReturnStatistics()
    {
        // Arrange
        var openIssues = new List<dynamic>
        {
            CreateMockIssueResponseWithLabels(1, "Issue 1", new[] { "bug", "priority-high" }),
            CreateMockIssueResponseWithLabels(2, "Issue 2", new[] { "enhancement" })
        };

        var closedIssues = new List<dynamic>
        {
            CreateMockIssueResponseWithLabels(3, "Issue 3", new[] { "bug" })
        };

        _mockApiClient
            .Setup(x => x.GetAsync<List<dynamic>>("repos/owner/repo/issues?state=open&per_page=30&page=1", default))
            .ReturnsAsync(openIssues);

        _mockApiClient
            .Setup(x => x.GetAsync<List<dynamic>>("repos/owner/repo/issues?state=closed&per_page=100&page=1", default))
            .ReturnsAsync(closedIssues);

        // Act
        var result = await _issuesService.GetIssueStatisticsAsync("owner", "repo");

        // Assert
        Assert.Equal(2, result.OpenCount);
        Assert.Equal(1, result.ClosedCount);
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.LabelDistribution["bug"]);
        Assert.Equal(1, result.LabelDistribution["enhancement"]);
        Assert.Equal(1, result.LabelDistribution["priority-high"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("test-user")]
    public async Task CreateIssueAsync_WhenNotAuthenticatedButHasStoredToken_ShouldSetToken(string userId)
    {
        // Arrange
        _mockApiClient.Setup(x => x.IsAuthenticated).Returns(false);

        var storedToken = new GitHubStoredToken
        {
            AccessToken = "ghp_stored_token",
            IsValid = true
        };

        _mockAuthService
            .Setup(x => x.GetStoredTokenAsync(userId))
            .ReturnsAsync(storedToken);

        var mockResponse = CreateMockIssueResponse(1, "Test Issue");
        _mockApiClient
            .Setup(x => x.PostAsync<dynamic>("repos/owner/repo/issues", It.IsAny<object>(), default))
            .ReturnsAsync(mockResponse);

        var request = new CreateIssueRequest { Title = "Test Issue", Body = "Test body" };

        // Act
        var result = await _issuesService.CreateIssueAsync("owner", "repo", request);

        // Assert
        Assert.NotNull(result);
        _mockApiClient.Verify(x => x.SetAuthenticationToken("ghp_stored_token"), Times.Once);
    }

    private static dynamic CreateMockIssueResponse(int number, string title, string state = "open")
    {
        return new
        {
            id = (long)(100000 + number),
            number = number,
            title = title,
            body = $"Body for {title}",
            state = state,
            html_url = $"https://github.com/owner/repo/issues/{number}",
            created_at = DateTime.UtcNow.AddDays(-1).ToString("O"),
            updated_at = DateTime.UtcNow.ToString("O"),
            closed_at = state == "closed" ? DateTime.UtcNow.ToString("O") : null,
            comments = 0,
            locked = false,
            labels = new[] { new { name = "test" } },
            assignees = new[] { new { login = "testuser", name = "Test User" } },
            user = new { login = "creator", name = "Issue Creator" },
            milestone = (object?)null
        };
    }

    private static dynamic CreateMockIssueResponseWithLabels(int number, string title, string[] labels, string state = "open")
    {
        return new
        {
            id = (long)(100000 + number),
            number = number,
            title = title,
            body = $"Body for {title}",
            state = state,
            html_url = $"https://github.com/owner/repo/issues/{number}",
            created_at = DateTime.UtcNow.AddDays(-1).ToString("O"),
            updated_at = DateTime.UtcNow.ToString("O"),
            closed_at = state == "closed" ? DateTime.UtcNow.ToString("O") : null,
            comments = 0,
            locked = false,
            labels = labels.Select(l => new { name = l }).ToArray(),
            assignees = new[] { new { login = "testuser", name = "Test User" } },
            user = new { login = "creator", name = "Issue Creator" },
            milestone = (object?)null
        };
    }
}