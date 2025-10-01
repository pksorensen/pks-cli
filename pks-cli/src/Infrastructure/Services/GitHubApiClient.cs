using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Interface for GitHub API client with comprehensive functionality
/// </summary>
public interface IGitHubApiClient
{
    /// <summary>
    /// Sends a GET request to the GitHub API
    /// </summary>
    Task<T?> GetAsync<T>(string endpoint, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Sends a POST request to the GitHub API
    /// </summary>
    Task<T?> PostAsync<T>(string endpoint, object? body = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Sends a PUT request to the GitHub API
    /// </summary>
    Task<T?> PutAsync<T>(string endpoint, object? body = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Sends a PATCH request to the GitHub API
    /// </summary>
    Task<T?> PatchAsync<T>(string endpoint, object? body = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Sends a DELETE request to the GitHub API
    /// </summary>
    Task<bool> DeleteAsync(string endpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets current rate limit information
    /// </summary>
    Task<GitHubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the authentication token for API requests
    /// </summary>
    void SetAuthenticationToken(string accessToken);

    /// <summary>
    /// Clears the authentication token
    /// </summary>
    void ClearAuthenticationToken();

    /// <summary>
    /// Gets current authentication status
    /// </summary>
    bool IsAuthenticated { get; }
}

/// <summary>
/// Comprehensive GitHub API client with retry logic, rate limiting, and error handling
/// </summary>
public class GitHubApiClient : IGitHubApiClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly GitHubAuthConfig _config;
    private readonly GitHubRetryPolicy _retryPolicy;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _rateLimitSemaphore;
    private string? _accessToken;
    private GitHubRateLimit? _cachedRateLimit;
    private DateTime _rateLimitCacheExpiry;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);

    public GitHubApiClient(
        HttpClient httpClient,
        GitHubAuthConfig? config = null,
        GitHubRetryPolicy? retryPolicy = null)
    {
        _httpClient = httpClient;
        _config = config ?? new GitHubAuthConfig();
        _retryPolicy = retryPolicy ?? new GitHubRetryPolicy();
        _rateLimitSemaphore = new SemaphoreSlim(1, 1);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        ConfigureHttpClient();
    }

    public async Task<T?> GetAsync<T>(string endpoint, CancellationToken cancellationToken = default) where T : class
    {
        return await ExecuteWithRetryAsync<T>(
            () => _httpClient.GetAsync(endpoint, cancellationToken),
            cancellationToken);
    }

    public async Task<T?> PostAsync<T>(string endpoint, object? body = null, CancellationToken cancellationToken = default) where T : class
    {
        var content = CreateJsonContent(body);
        return await ExecuteWithRetryAsync<T>(
            () => _httpClient.PostAsync(endpoint, content, cancellationToken),
            cancellationToken);
    }

    public async Task<T?> PutAsync<T>(string endpoint, object? body = null, CancellationToken cancellationToken = default) where T : class
    {
        var content = CreateJsonContent(body);
        return await ExecuteWithRetryAsync<T>(
            () => _httpClient.PutAsync(endpoint, content, cancellationToken),
            cancellationToken);
    }

    public async Task<T?> PatchAsync<T>(string endpoint, object? body = null, CancellationToken cancellationToken = default) where T : class
    {
        var content = CreateJsonContent(body);
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), endpoint) { Content = content };
        return await ExecuteWithRetryAsync<T>(
            () => _httpClient.SendAsync(request, cancellationToken),
            cancellationToken);
    }

    public async Task<bool> DeleteAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            await ExecuteWithRetryAsync<object>(
                () => _httpClient.DeleteAsync(endpoint, cancellationToken),
                cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<GitHubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default)
    {
        // Return cached rate limit if still valid
        if (_cachedRateLimit != null && DateTime.UtcNow < _rateLimitCacheExpiry)
        {
            return _cachedRateLimit;
        }

        try
        {
            var response = await _httpClient.GetAsync("rate_limit", cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            using var document = JsonDocument.Parse(content);
            var coreLimit = document.RootElement.GetProperty("resources").GetProperty("core");

            var rateLimit = new GitHubRateLimit
            {
                Limit = coreLimit.GetProperty("limit").GetInt32(),
                Remaining = coreLimit.GetProperty("remaining").GetInt32(),
                Used = coreLimit.GetProperty("used").GetInt32(),
                ResetTime = DateTimeOffset.FromUnixTimeSeconds(coreLimit.GetProperty("reset").GetInt64()).DateTime,
                Resource = "core"
            };

            _cachedRateLimit = rateLimit;
            _rateLimitCacheExpiry = DateTime.UtcNow.AddMinutes(1);

            return rateLimit;
        }
        catch
        {
            // Return default rate limit if API call fails
            return new GitHubRateLimit
            {
                Limit = 5000,
                Remaining = 5000,
                Used = 0,
                ResetTime = DateTime.UtcNow.AddHours(1),
                Resource = "core"
            };
        }
    }

    public void SetAuthenticationToken(string accessToken)
    {
        _accessToken = accessToken;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", accessToken);
    }

    public void ClearAuthenticationToken()
    {
        _accessToken = null;
        _httpClient.DefaultRequestHeaders.Authorization = null;
    }

    private async Task<T?> ExecuteWithRetryAsync<T>(
        Func<Task<HttpResponseMessage>> requestFunc,
        CancellationToken cancellationToken) where T : class
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt <= _retryPolicy.MaxRetries; attempt++)
        {
            try
            {
                // Check rate limiting before request
                if (_retryPolicy.HandleRateLimiting)
                {
                    await HandleRateLimitingAsync(cancellationToken);
                }

                var response = await requestFunc();

                // Update rate limit cache from response headers
                UpdateRateLimitFromHeaders(response);

                // Handle specific HTTP status codes
                if (response.IsSuccessStatusCode)
                {
                    if (typeof(T) == typeof(object))
                    {
                        return default(T); // For DELETE operations
                    }

                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (string.IsNullOrEmpty(content))
                    {
                        return default(T);
                    }

                    return JsonSerializer.Deserialize<T>(content, _jsonOptions);
                }

                // Handle rate limiting
                if (response.StatusCode == HttpStatusCode.Forbidden &&
                    response.Headers.Contains("X-RateLimit-Remaining") &&
                    response.Headers.GetValues("X-RateLimit-Remaining").First() == "0")
                {
                    if (attempt < _retryPolicy.MaxRetries)
                    {
                        var resetTime = GetRateLimitResetTime(response);
                        var waitTime = resetTime - DateTime.UtcNow;
                        if (waitTime > TimeSpan.Zero && waitTime < _retryPolicy.MaxDelay)
                        {
                            await Task.Delay(waitTime, cancellationToken);
                            continue;
                        }
                    }
                }

                // Handle other retryable status codes
                if (IsRetryableStatusCode(response.StatusCode) && attempt < _retryPolicy.MaxRetries)
                {
                    var delay = CalculateRetryDelay(attempt);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                // Handle non-retryable errors
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var error = ParseApiError(errorContent);
                throw new GitHubApiException($"GitHub API error: {error.Message}", response.StatusCode, error);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (attempt < _retryPolicy.MaxRetries)
            {
                lastException = ex;
                var delay = CalculateRetryDelay(attempt);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex) when (!(ex is GitHubApiException))
            {
                lastException = ex;
                break;
            }
        }

        throw new GitHubApiException(
            $"GitHub API request failed after {_retryPolicy.MaxRetries + 1} attempts",
            HttpStatusCode.InternalServerError,
            lastException);
    }

    private async Task HandleRateLimitingAsync(CancellationToken cancellationToken)
    {
        await _rateLimitSemaphore.WaitAsync(cancellationToken);
        try
        {
            var rateLimit = await GetRateLimitAsync(cancellationToken);

            if (rateLimit.Remaining <= 1)
            {
                var waitTime = rateLimit.ResetTime - DateTime.UtcNow;
                if (waitTime > TimeSpan.Zero && waitTime < _retryPolicy.MaxDelay)
                {
                    await Task.Delay(waitTime, cancellationToken);
                }
            }
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }

    private void UpdateRateLimitFromHeaders(HttpResponseMessage response)
    {
        try
        {
            if (response.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues) &&
                response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) &&
                response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
            {
                _cachedRateLimit = new GitHubRateLimit
                {
                    Limit = int.Parse(limitValues.First()),
                    Remaining = int.Parse(remainingValues.First()),
                    Used = int.Parse(limitValues.First()) - int.Parse(remainingValues.First()),
                    ResetTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(resetValues.First())).DateTime,
                    Resource = "core"
                };
                _rateLimitCacheExpiry = DateTime.UtcNow.AddMinutes(1);
            }
        }
        catch
        {
            // Ignore header parsing errors
        }
    }

    private static DateTime GetRateLimitResetTime(HttpResponseMessage response)
    {
        try
        {
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues))
            {
                var resetUnixTime = long.Parse(resetValues.First());
                return DateTimeOffset.FromUnixTimeSeconds(resetUnixTime).DateTime;
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return DateTime.UtcNow.AddMinutes(1);
    }

    private static bool IsRetryableStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.InternalServerError ||
               statusCode == HttpStatusCode.BadGateway ||
               statusCode == HttpStatusCode.ServiceUnavailable ||
               statusCode == HttpStatusCode.GatewayTimeout ||
               statusCode == HttpStatusCode.RequestTimeout;
    }

    private TimeSpan CalculateRetryDelay(int attempt)
    {
        var delay = TimeSpan.FromTicks((long)(_retryPolicy.BaseDelay.Ticks * Math.Pow(_retryPolicy.BackoffMultiplier, attempt)));
        return delay > _retryPolicy.MaxDelay ? _retryPolicy.MaxDelay : delay;
    }

    private static GitHubApiError ParseApiError(string errorContent)
    {
        try
        {
            return JsonSerializer.Deserialize<GitHubApiError>(errorContent, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }) ?? new GitHubApiError { Message = "Unknown API error" };
        }
        catch
        {
            return new GitHubApiError { Message = errorContent };
        }
    }

    private StringContent? CreateJsonContent(object? body)
    {
        if (body == null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(body, _jsonOptions);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private void ConfigureHttpClient()
    {
        _httpClient.BaseAddress = new Uri(_config.ApiBaseUrl);
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", _config.UserAgent);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
    }

    public void Dispose()
    {
        _rateLimitSemaphore?.Dispose();
    }
}

/// <summary>
/// Exception thrown by GitHub API operations
/// </summary>
public class GitHubApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public GitHubApiError? ApiError { get; }

    public GitHubApiException(string message, HttpStatusCode statusCode, GitHubApiError? apiError = null)
        : base(message)
    {
        StatusCode = statusCode;
        ApiError = apiError;
    }

    public GitHubApiException(string message, HttpStatusCode statusCode, Exception? innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}