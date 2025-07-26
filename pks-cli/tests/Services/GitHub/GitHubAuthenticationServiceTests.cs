using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.GitHub;

/// <summary>
/// Comprehensive unit tests for GitHubAuthenticationService
/// </summary>
public class GitHubAuthenticationServiceTests : IDisposable
{
    private readonly Mock<IConfigurationService> _configurationService;
    private readonly MockHttpMessageHandler _httpMessageHandler;
    private readonly HttpClient _httpClient;
    private readonly GitHubAuthConfig _config;
    private readonly GitHubAuthenticationService _service;

    public GitHubAuthenticationServiceTests()
    {
        _configurationService = new Mock<IConfigurationService>();
        _httpMessageHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_httpMessageHandler);
        _config = new GitHubAuthConfig
        {
            ClientId = "test-client-id",
            DeviceCodeUrl = "https://github.com/login/device/code",
            TokenUrl = "https://github.com/login/oauth/access_token",
            ApiBaseUrl = "https://api.github.com",
            PollingIntervalSeconds = 1, // Fast polling for tests
            MaxPollingAttempts = 5
        };

        _service = new GitHubAuthenticationService(_httpClient, _configurationService.Object, _config);
    }

    [Fact]
    public async Task InitiateDeviceCodeFlowAsync_ShouldReturnValidResponse()
    {
        // Arrange
        var expectedResponse = new
        {
            device_code = "test-device-code",
            user_code = "ABCD-1234",
            verification_uri = "https://github.com/login/device",
            verification_uri_complete = "https://github.com/login/device?user_code=ABCD-1234",
            expires_in = 900,
            interval = 5
        };

        _httpMessageHandler.SetupResponse(
            HttpMethod.Post,
            _config.DeviceCodeUrl,
            JsonSerializer.Serialize(expectedResponse),
            HttpStatusCode.OK);

        // Act
        var result = await _service.InitiateDeviceCodeFlowAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test-device-code", result.DeviceCode);
        Assert.Equal("ABCD-1234", result.UserCode);
        Assert.Equal("https://github.com/login/device", result.VerificationUri);
        Assert.Equal(900, result.ExpiresIn);
        Assert.Equal(5, result.Interval);
    }

    [Fact]
    public async Task InitiateDeviceCodeFlowAsync_WithCustomScopes_ShouldIncludeScopesInRequest()
    {
        // Arrange
        var customScopes = new[] { "repo", "user:email" };
        var expectedResponse = new { device_code = "test", user_code = "TEST", verification_uri = "https://github.com/login/device", expires_in = 900, interval = 5 };

        _httpMessageHandler.SetupResponse(
            HttpMethod.Post,
            _config.DeviceCodeUrl,
            JsonSerializer.Serialize(expectedResponse),
            HttpStatusCode.OK);

        // Act
        await _service.InitiateDeviceCodeFlowAsync(customScopes);

        // Assert
        var sentRequest = _httpMessageHandler.GetLastRequest();
        var sentContent = await sentRequest!.Content!.ReadAsStringAsync();
        Assert.Contains("scope=repo%20user%3Aemail", sentContent);
    }

    [Fact]
    public async Task PollForAuthenticationAsync_WithAuthorizationPending_ShouldReturnPendingStatus()
    {
        // Arrange
        var errorResponse = new
        {
            error = "authorization_pending",
            error_description = "The user has not yet entered the user code"
        };

        _httpMessageHandler.SetupResponse(
            HttpMethod.Post,
            _config.TokenUrl,
            JsonSerializer.Serialize(errorResponse),
            HttpStatusCode.BadRequest);

        // Act
        var result = await _service.PollForAuthenticationAsync("test-device-code");

        // Assert
        Assert.False(result.IsAuthenticated);
        Assert.Equal("authorization_pending", result.Error);
        Assert.Equal("The user has not yet entered the user code", result.ErrorDescription);
    }

    [Fact]
    public async Task PollForAuthenticationAsync_WithValidToken_ShouldReturnAuthenticatedStatus()
    {
        // Arrange
        var tokenResponse = new
        {
            access_token = "ghp_test_token",
            token_type = "bearer",
            scope = "repo user:email",
            expires_in = 28800
        };

        _httpMessageHandler.SetupResponse(
            HttpMethod.Post,
            _config.TokenUrl,
            JsonSerializer.Serialize(tokenResponse),
            HttpStatusCode.OK);

        // Act
        var result = await _service.PollForAuthenticationAsync("test-device-code");

        // Assert
        Assert.True(result.IsAuthenticated);
        Assert.Equal("ghp_test_token", result.AccessToken);
        Assert.Contains("repo", result.Scopes);
        Assert.Contains("user:email", result.Scopes);
        Assert.True(result.ExpiresAt > DateTime.UtcNow.AddHours(7));
    }

    [Fact]
    public async Task AuthenticateAsync_ShouldCompleteFullFlow()
    {
        // Arrange
        var deviceCodeResponse = new
        {
            device_code = "test-device-code",
            user_code = "ABCD-1234",
            verification_uri = "https://github.com/login/device",
            verification_uri_complete = "https://github.com/login/device?user_code=ABCD-1234",
            expires_in = 900,
            interval = 1
        };

        var tokenResponse = new
        {
            access_token = "ghp_test_token",
            token_type = "bearer",
            scope = "repo user:email"
        };

        var userResponse = new
        {
            login = "testuser",
            name = "Test User"
        };

        // Setup device code request
        _httpMessageHandler.SetupResponse(
            HttpMethod.Post,
            _config.DeviceCodeUrl,
            JsonSerializer.Serialize(deviceCodeResponse),
            HttpStatusCode.OK);

        // Setup token polling (first pending, then success)
        _httpMessageHandler.SetupSequentialResponses(HttpMethod.Post, _config.TokenUrl, new[]
        {
            (JsonSerializer.Serialize(new { error = "authorization_pending" }), HttpStatusCode.BadRequest),
            (JsonSerializer.Serialize(tokenResponse), HttpStatusCode.OK)
        });

        // Setup token validation
        _httpMessageHandler.SetupResponse(
            HttpMethod.Get,
            $"{_config.ApiBaseUrl}/user",
            JsonSerializer.Serialize(userResponse),
            HttpStatusCode.OK);

        var progressReports = new List<GitHubAuthProgress>();
        var progress = new Progress<GitHubAuthProgress>(p => progressReports.Add(p));

        // Act
        var result = await _service.AuthenticateAsync(progressCallback: progress);

        // Assert
        Assert.True(result.IsAuthenticated);
        Assert.Equal("ghp_test_token", result.AccessToken);
        Assert.True(progressReports.Count > 0);
        Assert.Contains(progressReports, p => p.CurrentStep == GitHubAuthStep.Complete);
    }

    [Fact]
    public async Task ValidateTokenAsync_WithValidToken_ShouldReturnValidResult()
    {
        // Arrange
        var userResponse = new { login = "testuser", name = "Test User" };
        
        _httpMessageHandler.SetupResponse(
            HttpMethod.Get,
            $"{_config.ApiBaseUrl}/user",
            JsonSerializer.Serialize(userResponse),
            HttpStatusCode.OK,
            new Dictionary<string, string> { ["X-OAuth-Scopes"] = "repo, user:email" });

        // Act
        var result = await _service.ValidateTokenAsync("ghp_valid_token");

        // Assert
        Assert.True(result.IsValid);
        Assert.Contains("repo", result.Scopes);
        Assert.Contains("user:email", result.Scopes);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateTokenAsync_WithInvalidToken_ShouldReturnInvalidResult()
    {
        // Arrange
        _httpMessageHandler.SetupResponse(
            HttpMethod.Get,
            $"{_config.ApiBaseUrl}/user",
            "Unauthorized",
            HttpStatusCode.Unauthorized);

        // Act
        var result = await _service.ValidateTokenAsync("invalid_token");

        // Assert
        Assert.False(result.IsValid);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task StoreTokenAsync_ShouldStoreTokenWithEncryption()
    {
        // Arrange
        var token = new GitHubStoredToken
        {
            AccessToken = "ghp_test_token",
            Scopes = new[] { "repo", "user:email" },
            CreatedAt = DateTime.UtcNow,
            IsValid = true
        };

        _configurationService.Setup(x => x.SetAsync(
            "github.auth.token",
            It.IsAny<string>(),
            true, // global
            true)) // encrypt
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.StoreTokenAsync(token);

        // Assert
        Assert.True(result);
        _configurationService.Verify(x => x.SetAsync(
            "github.auth.token",
            It.IsAny<string>(),
            true,
            true), Times.Once);
    }

    [Fact]
    public async Task GetStoredTokenAsync_ShouldRetrieveStoredToken()
    {
        // Arrange
        var token = new GitHubStoredToken
        {
            AccessToken = "ghp_test_token",
            Scopes = new[] { "repo", "user:email" },
            CreatedAt = DateTime.UtcNow,
            IsValid = true
        };

        var tokenJson = JsonSerializer.Serialize(token, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        _configurationService.Setup(x => x.GetAsync("github.auth.token"))
            .ReturnsAsync(tokenJson);

        // Act
        var result = await _service.GetStoredTokenAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("ghp_test_token", result.AccessToken);
        Assert.Contains("repo", result.Scopes);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task IsAuthenticatedAsync_WithValidStoredToken_ShouldReturnTrue()
    {
        // Arrange
        var token = new GitHubStoredToken
        {
            AccessToken = "ghp_test_token",
            Scopes = new[] { "repo", "user:email" },
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(8),
            IsValid = true,
            LastValidated = DateTime.UtcNow
        };

        var tokenJson = JsonSerializer.Serialize(token, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        _configurationService.Setup(x => x.GetAsync("github.auth.token"))
            .ReturnsAsync(tokenJson);

        // Act
        var result = await _service.IsAuthenticatedAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task IsAuthenticatedAsync_WithExpiredToken_ShouldReturnFalse()
    {
        // Arrange
        var token = new GitHubStoredToken
        {
            AccessToken = "ghp_test_token",
            Scopes = new[] { "repo", "user:email" },
            CreatedAt = DateTime.UtcNow.AddHours(-10),
            ExpiresAt = DateTime.UtcNow.AddHours(-2), // Expired
            IsValid = true,
            LastValidated = DateTime.UtcNow.AddHours(-3)
        };

        var tokenJson = JsonSerializer.Serialize(token, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        _configurationService.Setup(x => x.GetAsync("github.auth.token"))
            .ReturnsAsync(tokenJson);

        // Act
        var result = await _service.IsAuthenticatedAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ClearStoredTokenAsync_ShouldRemoveStoredToken()
    {
        // Arrange
        _configurationService.Setup(x => x.DeleteAsync("github.auth.token"))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.ClearStoredTokenAsync();

        // Assert
        Assert.True(result);
        _configurationService.Verify(x => x.DeleteAsync("github.auth.token"), Times.Once);
    }

    [Theory]
    [InlineData("slow_down")]
    [InlineData("access_denied")]
    [InlineData("expired_token")]
    public async Task PollForAuthenticationAsync_WithDifferentErrors_ShouldReturnCorrectStatus(string errorType)
    {
        // Arrange
        var errorResponse = new
        {
            error = errorType,
            error_description = $"Test error: {errorType}"
        };

        _httpMessageHandler.SetupResponse(
            HttpMethod.Post,
            _config.TokenUrl,
            JsonSerializer.Serialize(errorResponse),
            HttpStatusCode.BadRequest);

        // Act
        var result = await _service.PollForAuthenticationAsync("test-device-code");

        // Assert
        Assert.False(result.IsAuthenticated);
        Assert.Equal(errorType, result.Error);
        Assert.Equal($"Test error: {errorType}", result.ErrorDescription);
    }

    [Fact]
    public async Task AuthenticateAsync_WithCancellation_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.AuthenticateAsync(cancellationToken: cancellationTokenSource.Token));
    }

    [Fact]
    public async Task InitiateDeviceCodeFlowAsync_WithNetworkError_ShouldThrowInvalidOperationException()
    {
        // Arrange
        _httpMessageHandler.SetupResponse(
            HttpMethod.Post,
            _config.DeviceCodeUrl,
            "Network error",
            HttpStatusCode.InternalServerError);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.InitiateDeviceCodeFlowAsync());
        
        Assert.Contains("Failed to initiate device code flow", exception.Message);
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        _httpMessageHandler?.Dispose();
    }
}

/// <summary>
/// Mock HTTP message handler for testing HTTP requests
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler, IDisposable
{
    private readonly Dictionary<string, (string response, HttpStatusCode statusCode, Dictionary<string, string>? headers)> _responses = new();
    private readonly Dictionary<string, Queue<(string response, HttpStatusCode statusCode, Dictionary<string, string>? headers)>> _sequentialResponses = new();
    private HttpRequestMessage? _lastRequest;

    public void SetupResponse(HttpMethod method, string url, string response, HttpStatusCode statusCode, Dictionary<string, string>? headers = null)
    {
        var key = $"{method}:{url}";
        _responses[key] = (response, statusCode, headers);
    }

    public void SetupSequentialResponses(HttpMethod method, string url, (string response, HttpStatusCode statusCode, Dictionary<string, string>? headers)[] responses)
    {
        var key = $"{method}:{url}";
        _sequentialResponses[key] = new Queue<(string, HttpStatusCode, Dictionary<string, string>?)>(responses);
    }

    public HttpRequestMessage? GetLastRequest() => _lastRequest;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _lastRequest = request;
        var key = $"{request.Method}:{request.RequestUri}";

        // Check for sequential responses first
        if (_sequentialResponses.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            var (response, statusCode, headers) = queue.Dequeue();
            return CreateResponse(response, statusCode, headers);
        }

        // Check for single responses
        if (_responses.TryGetValue(key, out var singleResponse))
        {
            return CreateResponse(singleResponse.response, singleResponse.statusCode, singleResponse.headers);
        }

        // Default response
        return CreateResponse("Not Found", HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage CreateResponse(string content, HttpStatusCode statusCode, Dictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content)
        };

        if (headers != null)
        {
            foreach (var header in headers)
            {
                response.Headers.Add(header.Key, header.Value);
            }
        }

        return response;
    }

    public void Dispose()
    {
        // Clean up resources if needed
    }
}