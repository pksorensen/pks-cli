using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for GitHubActionsService using a mocked IGitHubApiClient
/// </summary>
public class GitHubActionsServiceTests
{
    private readonly Mock<IGitHubApiClient> _mockApiClient;
    private readonly Mock<IGitHubAuthenticationService> _mockAuthService;
    private readonly GitHubActionsService _sut;

    public GitHubActionsServiceTests()
    {
        _mockApiClient = new Mock<IGitHubApiClient>();
        _mockAuthService = new Mock<IGitHubAuthenticationService>();
        _mockApiClient.Setup(c => c.IsAuthenticated).Returns(true);
        _sut = new GitHubActionsService(_mockApiClient.Object, _mockAuthService.Object);
    }

    // ── GenerateJitConfigAsync ──────────────────────────────────────────

    [Fact]
    public async Task GenerateJitConfigAsync_ReturnsJitConfig()
    {
        // Arrange
        var expectedResponse = new GitHubJitRunnerConfigResponse
        {
            Runner = new GitHubJitRunnerInfo
            {
                Id = 42,
                Name = "test-runner",
                Os = "linux",
                Status = "online"
            },
            EncodedJitConfig = "base64-encoded-config-string"
        };

        _mockApiClient
            .Setup(c => c.PostAsync<GitHubJitRunnerConfigResponse>(
                "repos/testowner/testrepo/actions/runners/generate-jitconfig",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await _sut.GenerateJitConfigAsync("testowner", "testrepo", "my-runner", new[] { "self-hosted", "linux" });

        // Assert
        result.Should().NotBeNull();
        result.RunnerId.Should().Be(42);
        result.EncodedJitConfig.Should().Be("base64-encoded-config-string");

        _mockApiClient.Verify(c => c.PostAsync<GitHubJitRunnerConfigResponse>(
            "repos/testowner/testrepo/actions/runners/generate-jitconfig",
            It.Is<object>(body => VerifyJitConfigRequestBody(body, "my-runner", new[] { "self-hosted", "linux" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateJitConfigAsync_WhenApiFails_ThrowsException()
    {
        // Arrange
        _mockApiClient
            .Setup(c => c.PostAsync<GitHubJitRunnerConfigResponse>(
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Not Found", HttpStatusCode.NotFound));

        // Act
        var act = () => _sut.GenerateJitConfigAsync("testowner", "testrepo", "my-runner", new[] { "self-hosted" });

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>()
            .Where(ex => ex.StatusCode == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GenerateJitConfigAsync_WhenApiReturnsNull_ThrowsException()
    {
        // Arrange
        _mockApiClient
            .Setup(c => c.PostAsync<GitHubJitRunnerConfigResponse>(
                It.IsAny<string>(),
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((GitHubJitRunnerConfigResponse?)null);

        // Act
        var act = () => _sut.GenerateJitConfigAsync("testowner", "testrepo", "my-runner", new[] { "self-hosted" });

        // Assert
        await act.Should().ThrowAsync<GitHubApiException>();
    }

    // ── GetQueuedRunsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetQueuedRunsAsync_ReturnsQueuedRuns()
    {
        // Arrange
        var apiResponse = new WorkflowRunsResponse
        {
            TotalCount = 2,
            WorkflowRuns = new List<QueuedWorkflowRun>
            {
                new()
                {
                    Id = 100,
                    Name = "CI Build",
                    Status = "queued",
                    HeadBranch = "main",
                    HeadSha = "abc123",
                    HtmlUrl = "https://github.com/testowner/testrepo/actions/runs/100",
                    CreatedAt = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc),
                    Labels = new List<string> { "self-hosted", "linux" }
                },
                new()
                {
                    Id = 101,
                    Name = "Deploy",
                    Status = "queued",
                    HeadBranch = "feature/test",
                    HeadSha = "def456",
                    HtmlUrl = "https://github.com/testowner/testrepo/actions/runs/101",
                    CreatedAt = new DateTime(2026, 1, 15, 10, 5, 0, DateTimeKind.Utc),
                    Labels = new List<string> { "self-hosted" }
                }
            }
        };

        _mockApiClient
            .Setup(c => c.GetAsync<WorkflowRunsResponse>(
                "repos/testowner/testrepo/actions/runs?status=queued&per_page=10",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResponse);

        // Act
        var result = await _sut.GetQueuedRunsAsync("testowner", "testrepo");

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result[0].Id.Should().Be(100);
        result[0].Name.Should().Be("CI Build");
        result[0].Status.Should().Be("queued");
        result[0].Labels.Should().Contain("self-hosted");
        result[1].Id.Should().Be(101);
        result[1].Name.Should().Be("Deploy");
    }

    [Fact]
    public async Task GetQueuedRunsAsync_WhenNoRuns_ReturnsEmptyList()
    {
        // Arrange
        var apiResponse = new WorkflowRunsResponse
        {
            TotalCount = 0,
            WorkflowRuns = new List<QueuedWorkflowRun>()
        };

        _mockApiClient
            .Setup(c => c.GetAsync<WorkflowRunsResponse>(
                "repos/testowner/testrepo/actions/runs?status=queued&per_page=10",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResponse);

        // Act
        var result = await _sut.GetQueuedRunsAsync("testowner", "testrepo");

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetQueuedRunsAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        // Arrange
        _mockApiClient
            .Setup(c => c.GetAsync<WorkflowRunsResponse>(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowRunsResponse?)null);

        // Act
        var result = await _sut.GetQueuedRunsAsync("testowner", "testrepo");

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    // ── CheckAdminPermissionAsync ───────────────────────────────────────

    [Fact]
    public async Task CheckAdminPermissionAsync_WhenAdmin_ReturnsTrue()
    {
        // Arrange
        var repoResponse = new GitHubRepositoryResponse
        {
            Id = 1,
            Name = "testrepo",
            FullName = "testowner/testrepo",
            Permissions = new GitHubRepositoryPermissions
            {
                Admin = true,
                Maintain = true,
                Push = true,
                Triage = true,
                Pull = true
            }
        };

        _mockApiClient
            .Setup(c => c.GetAsync<GitHubRepositoryResponse>(
                "repos/testowner/testrepo",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoResponse);

        // Act
        var result = await _sut.CheckAdminPermissionAsync("testowner", "testrepo");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAdminPermissionAsync_WhenNotAdmin_ReturnsFalse()
    {
        // Arrange
        var repoResponse = new GitHubRepositoryResponse
        {
            Id = 1,
            Name = "testrepo",
            FullName = "testowner/testrepo",
            Permissions = new GitHubRepositoryPermissions
            {
                Admin = false,
                Maintain = false,
                Push = true,
                Triage = true,
                Pull = true
            }
        };

        _mockApiClient
            .Setup(c => c.GetAsync<GitHubRepositoryResponse>(
                "repos/testowner/testrepo",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoResponse);

        // Act
        var result = await _sut.CheckAdminPermissionAsync("testowner", "testrepo");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAdminPermissionAsync_WhenApiThrows_ReturnsFalse()
    {
        // Arrange
        _mockApiClient
            .Setup(c => c.GetAsync<GitHubRepositoryResponse>(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Forbidden", HttpStatusCode.Forbidden));

        // Act
        var result = await _sut.CheckAdminPermissionAsync("testowner", "testrepo");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAdminPermissionAsync_WhenPermissionsNull_ReturnsFalse()
    {
        // Arrange
        var repoResponse = new GitHubRepositoryResponse
        {
            Id = 1,
            Name = "testrepo",
            FullName = "testowner/testrepo",
            Permissions = null
        };

        _mockApiClient
            .Setup(c => c.GetAsync<GitHubRepositoryResponse>(
                "repos/testowner/testrepo",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoResponse);

        // Act
        var result = await _sut.CheckAdminPermissionAsync("testowner", "testrepo");

        // Assert
        result.Should().BeFalse();
    }

    // ── GetJobsForRunAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetJobsForRunAsync_ReturnsJobs()
    {
        // Arrange
        var apiResponse = new WorkflowJobsResponse
        {
            TotalCount = 2,
            Jobs = new List<WorkflowJob>
            {
                new()
                {
                    Id = 200,
                    RunId = 100,
                    Name = "build",
                    Status = "completed",
                    Conclusion = "success",
                    Labels = new List<string> { "self-hosted", "linux" },
                    HtmlUrl = "https://github.com/testowner/testrepo/actions/runs/100/jobs/200",
                    StartedAt = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    Id = 201,
                    RunId = 100,
                    Name = "test",
                    Status = "queued",
                    Conclusion = null,
                    Labels = new List<string> { "self-hosted" },
                    HtmlUrl = "https://github.com/testowner/testrepo/actions/runs/100/jobs/201",
                    StartedAt = null
                }
            }
        };

        _mockApiClient
            .Setup(c => c.GetAsync<WorkflowJobsResponse>(
                "repos/testowner/testrepo/actions/runs/100/jobs?filter=latest&per_page=100",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResponse);

        // Act
        var result = await _sut.GetJobsForRunAsync("testowner", "testrepo", 100);

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result[0].Id.Should().Be(200);
        result[0].Name.Should().Be("build");
        result[0].Labels.Should().Contain("self-hosted");
        result[1].Id.Should().Be(201);
        result[1].Name.Should().Be("test");
    }

    [Fact]
    public async Task GetJobsForRunAsync_WhenNoJobs_ReturnsEmptyList()
    {
        // Arrange
        var apiResponse = new WorkflowJobsResponse
        {
            TotalCount = 0,
            Jobs = new List<WorkflowJob>()
        };

        _mockApiClient
            .Setup(c => c.GetAsync<WorkflowJobsResponse>(
                "repos/testowner/testrepo/actions/runs/100/jobs?filter=latest&per_page=100",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResponse);

        // Act
        var result = await _sut.GetJobsForRunAsync("testowner", "testrepo", 100);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetJobsForRunAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        // Arrange
        _mockApiClient
            .Setup(c => c.GetAsync<WorkflowJobsResponse>(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowJobsResponse?)null);

        // Act
        var result = await _sut.GetJobsForRunAsync("testowner", "testrepo", 100);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static bool VerifyJitConfigRequestBody(object body, string expectedName, string[] expectedLabels)
    {
        // Serialize and re-parse to inspect the anonymous object
        var json = JsonSerializer.Serialize(body);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var hasName = root.TryGetProperty("name", out var nameProp) && nameProp.GetString() == expectedName;
        var hasRunnerGroupId = root.TryGetProperty("runner_group_id", out var groupProp) && groupProp.GetInt32() == 1;
        var hasLabels = root.TryGetProperty("labels", out var labelsProp) && labelsProp.GetArrayLength() == expectedLabels.Length;

        return hasName && hasRunnerGroupId && hasLabels;
    }
}
