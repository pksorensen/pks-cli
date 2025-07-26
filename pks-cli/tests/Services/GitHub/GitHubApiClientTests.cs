using System.Net;
using System.Net.Http;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.GitHub;

/// <summary>
/// Comprehensive unit tests for GitHubApiClient
/// </summary>
public class GitHubApiClientTests : IDisposable
{
    private readonly MockHttpMessageHandler _httpMessageHandler;
    private readonly HttpClient _httpClient;
    private readonly GitHubAuthConfig _config;
    private readonly GitHubRetryPolicy _retryPolicy;
    private readonly GitHubApiClient _apiClient;

    public GitHubApiClientTests()
    {
        _httpMessageHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_httpMessageHandler);
        _config = new GitHubAuthConfig
        {
            ApiBaseUrl = "https://api.github.com",
            UserAgent = "PKS-CLI-Test/1.0.0"
        };
        _retryPolicy = new GitHubRetryPolicy
        {
            MaxRetries = 2,
            BaseDelay = TimeSpan.FromMilliseconds(10), // Fast for tests
            BackoffMultiplier = 1.5,
            MaxDelay = TimeSpan.FromSeconds(1)
        };

        _apiClient = new GitHubApiClient(_httpClient, _config, _retryPolicy);
    }

    [Fact]
    public async Task GetAsync_WithValidResponse_ShouldReturnDeserializedObject()
    {
        // Arrange
        var testData = new { id = 123, name = "test-repo", full_name = "owner/test-repo" };
        var jsonResponse = JsonSerializer.Serialize(testData);

        _httpMessageHandler.SetupResponse(
            HttpMethod.Get,
            "https://api.github.com/repos/owner/test-repo",
            jsonResponse,
            HttpStatusCode.OK);

        // Act
        var result = await _apiClient.GetAsync<dynamic>("repos/owner/test-repo");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task PostAsync_WithValidPayload_ShouldSendCorrectRequest()
    {
        // Arrange
        var requestData = new { title = "Test Issue", body = "Test body" };
        var responseData = new { id = 456, number = 1, title = "Test Issue", body = "Test body" };
        var jsonResponse = JsonSerializer.Serialize(responseData);

        _httpMessageHandler.SetupResponse(
            HttpMethod.Post,
            "https://api.github.com/repos/owner/repo/issues",
            jsonResponse,
            HttpStatusCode.Created);

        // Act
        var result = await _apiClient.PostAsync<dynamic>("repos/owner/repo/issues", requestData);

        // Assert
        Assert.NotNull(result);
        var lastRequest = _httpMessageHandler.GetLastRequest();
        Assert.NotNull(lastRequest);
        Assert.Equal(HttpMethod.Post, lastRequest.Method);
        Assert.Equal("application/json", lastRequest.Content?.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PutAsync_WithValidPayload_ShouldSendCorrectRequest()
    {
        // Arrange
        var requestData = new { title = "Updated Issue" };
        var responseData = new { id = 456, title = "Updated Issue" };
        var jsonResponse = JsonSerializer.Serialize(responseData);

        _httpMessageHandler.SetupResponse(
            HttpMethod.Put,
            "https://api.github.com/repos/owner/repo/issues/1",
            jsonResponse,
            HttpStatusCode.OK);

        // Act
        var result = await _apiClient.PutAsync<dynamic>("repos/owner/repo/issues/1", requestData);

        // Assert
        Assert.NotNull(result);
        var lastRequest = _httpMessageHandler.GetLastRequest();
        Assert.Equal(HttpMethod.Put, lastRequest!.Method);
    }

    [Fact]
    public async Task PatchAsync_WithValidPayload_ShouldSendCorrectRequest()
    {
        // Arrange
        var requestData = new { state = "closed" };
        var responseData = new { id = 456, state = "closed" };
        var jsonResponse = JsonSerializer.Serialize(responseData);

        _httpMessageHandler.SetupResponse(
            new HttpMethod("PATCH"),
            "https://api.github.com/repos/owner/repo/issues/1",
            jsonResponse,
            HttpStatusCode.OK);

        // Act
        var result = await _apiClient.PatchAsync<dynamic>("repos/owner/repo/issues/1", requestData);

        // Assert
        Assert.NotNull(result);
        var lastRequest = _httpMessageHandler.GetLastRequest();
        Assert.Equal("PATCH", lastRequest!.Method.Method);
    }

    [Fact]
    public async Task DeleteAsync_WithValidEndpoint_ShouldReturnTrue()
    {
        // Arrange
        _httpMessageHandler.SetupResponse(
            HttpMethod.Delete,
            "https://api.github.com/repos/owner/repo/issues/1/labels/bug",
            "",
            HttpStatusCode.NoContent);

        // Act
        var result = await _apiClient.DeleteAsync("repos/owner/repo/issues/1/labels/bug");

        // Assert
        Assert.True(result);
        var lastRequest = _httpMessageHandler.GetLastRequest();
        Assert.Equal(HttpMethod.Delete, lastRequest!.Method);
    }

    [Fact]
    public async Task GetRateLimitAsync_ShouldReturnRateLimitInfo()
    {
        // Arrange
        var rateLimitResponse = new
        {
            resources = new
            {
                core = new
                {
                    limit = 5000,
                    remaining = 4999,
                    reset = 1640995200, // Unix timestamp
                    used = 1
                }
            }
        };

        _httpMessageHandler.SetupResponse(
            HttpMethod.Get,
            "https://api.github.com/rate_limit",
            JsonSerializer.Serialize(rateLimitResponse),
            HttpStatusCode.OK);

        // Act
        var result = await _apiClient.GetRateLimitAsync();

        // Assert
        Assert.Equal(5000, result.Limit);
        Assert.Equal(4999, result.Remaining);
        Assert.Equal(1, result.Used);
        Assert.Equal("core", result.Resource);
    }

    [Fact]
    public void SetAuthenticationToken_ShouldConfigureAuthHeader()
    {
        // Arrange
        const string token = "ghp_test_token";

        // Act
        _apiClient.SetAuthenticationToken(token);

        // Assert
        Assert.True(_apiClient.IsAuthenticated);
    }

    [Fact]
    public void ClearAuthenticationToken_ShouldRemoveAuthHeader()
    {
        // Arrange
        _apiClient.SetAuthenticationToken("ghp_test_token");
        Assert.True(_apiClient.IsAuthenticated);

        // Act
        _apiClient.ClearAuthenticationToken();

        // Assert
        Assert.False(_apiClient.IsAuthenticated);
    }

    [Fact]
    public async Task GetAsync_WithRateLimitError_ShouldRetryAfterReset()
    {
        // Arrange
        var resetTime = DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeSeconds();

        // First response: rate limited
        _httpMessageHandler.SetupSequentialResponses(
            HttpMethod.Get,
            "https://api.github.com/repos/owner/repo",
            new[]
            {
                ("Rate limit exceeded", HttpStatusCode.Forbidden, new Dictionary<string, string>
                {
                    ["X-RateLimit-Remaining"] = "0",
                    ["X-RateLimit-Reset"] = resetTime.ToString()
                }),
                (JsonSerializer.Serialize(new { id = 123, name = "repo" }), HttpStatusCode.OK, null)
            });

        // Act
        var result = await _apiClient.GetAsync<dynamic>("repos/owner/repo");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetAsync_WithRetryableError_ShouldRetryWithBackoff()
    {
        // Arrange
        _httpMessageHandler.SetupSequentialResponses(
            HttpMethod.Get,
            "https://api.github.com/repos/owner/repo",
            new[]
            {
                ("Internal Server Error", HttpStatusCode.InternalServerError, null),
                ("Bad Gateway", HttpStatusCode.BadGateway, null),
                (JsonSerializer.Serialize(new { id = 123, name = "repo" }), HttpStatusCode.OK, null)
            });

        // Act
        var result = await _apiClient.GetAsync<dynamic>("repos/owner/repo");

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetAsync_WithNonRetryableError_ShouldThrowImmediately()
    {
        // Arrange
        var errorResponse = new
        {
            message = "Not Found",
            documentation_url = "https://docs.github.com/rest"
        };

        _httpMessageHandler.SetupResponse(
            HttpMethod.Get,
            "https://api.github.com/repos/owner/nonexistent",
            JsonSerializer.Serialize(errorResponse),
            HttpStatusCode.NotFound);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => _apiClient.GetAsync<dynamic>("repos/owner/nonexistent"));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Contains("Not Found", exception.Message);
    }

    [Fact]
    public async Task GetAsync_WithMaxRetriesExceeded_ShouldThrowGitHubApiException()
    {
        // Arrange
        _httpMessageHandler.SetupResponse(
            HttpMethod.Get,
            "https://api.github.com/repos/owner/repo",
            "Internal Server Error",
            HttpStatusCode.InternalServerError);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GitHubApiException>(
            () => _apiClient.GetAsync<dynamic>("repos/owner/repo"));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Contains("failed after", exception.Message);
    }

    [Fact]
    public async Task GetAsync_WithCancellation_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _apiClient.GetAsync<dynamic>("repos/owner/repo", cancellationTokenSource.Token));
    }

    [Fact]
    public async Task GetAsync_WithRateLimitHeaders_ShouldUpdateCachedRateLimit()
    {
        // Arrange
        var resetTime = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var headers = new Dictionary<string, string>
        {
            ["X-RateLimit-Limit"] = "5000",
            ["X-RateLimit-Remaining"] = "4500",
            ["X-RateLimit-Reset"] = resetTime.ToString()
        };

        _httpMessageHandler.SetupResponse(
            HttpMethod.Get,
            "https://api.github.com/repos/owner/repo",
            JsonSerializer.Serialize(new { id = 123, name = "repo" }),
            HttpStatusCode.OK,
            headers);

        // Act
        await _apiClient.GetAsync<dynamic>("repos/owner/repo");
        var rateLimit = await _apiClient.GetRateLimitAsync();

        // Assert
        Assert.Equal(5000, rateLimit.Limit);
        Assert.Equal(4500, rateLimit.Remaining);
        Assert.Equal(500, rateLimit.Used);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, false)]
    public async Task GetAsync_WithDifferentStatusCodes_ShouldRetryOnlyRetryableCodes(HttpStatusCode statusCode, bool shouldRetry)
    {
        // Arrange
        if (shouldRetry)
        {
            _httpMessageHandler.SetupSequentialResponses(
                HttpMethod.Get,
                "https://api.github.com/repos/owner/repo",
                new[]
                {
                    ("Error", statusCode, null),
                    (JsonSerializer.Serialize(new { id = 123, name = "repo" }), HttpStatusCode.OK, null)
                });

            // Act
            var result = await _apiClient.GetAsync<dynamic>("repos/owner/repo");

            // Assert
            Assert.NotNull(result);
        }
        else
        {
            _httpMessageHandler.SetupResponse(
                HttpMethod.Get,
                "https://api.github.com/repos/owner/repo",
                "Error",
                statusCode);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<GitHubApiException>(
                () => _apiClient.GetAsync<dynamic>("repos/owner/repo"));

            Assert.Equal(statusCode, exception.StatusCode);
        }
    }

    [Fact]
    public async Task PostAsync_WithNullBody_ShouldSendRequestWithoutContent()
    {
        // Arrange
        _httpMessageHandler.SetupResponse(
            HttpMethod.Post,
            "https://api.github.com/repos/owner/repo/issues/1/labels",
            JsonSerializer.Serialize(new[] { new { name = "bug" } }),
            HttpStatusCode.OK);

        // Act
        var result = await _apiClient.PostAsync<dynamic>("repos/owner/repo/issues/1/labels");

        // Assert
        Assert.NotNull(result);
        var lastRequest = _httpMessageHandler.GetLastRequest();
        Assert.Null(lastRequest!.Content);
    }

    [Fact]
    public void Dispose_ShouldCleanupResources()
    {
        // Act & Assert (should not throw)
        _apiClient.Dispose();
    }

    public void Dispose()
    {
        _apiClient?.Dispose();
        _httpClient?.Dispose();
        _httpMessageHandler?.Dispose();
    }
}