using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Interface for GitHub authentication services using device code flow
/// </summary>
public interface IGitHubAuthenticationService
{
    /// <summary>
    /// Initiates device code flow authentication
    /// </summary>
    /// <param name="scopes">Required OAuth scopes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Device code response with user instructions</returns>
    Task<GitHubDeviceCodeResponse> InitiateDeviceCodeFlowAsync(string[]? scopes = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls for authentication completion using device code
    /// </summary>
    /// <param name="deviceCode">Device code from initiation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Authentication status with token if successful</returns>
    Task<GitHubDeviceAuthStatus> PollForAuthenticationAsync(string deviceCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs complete device code flow authentication with progress reporting
    /// </summary>
    /// <param name="scopes">Required OAuth scopes</param>
    /// <param name="progressCallback">Progress callback for UI updates</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Final authentication status</returns>
    Task<GitHubDeviceAuthStatus> AuthenticateAsync(string[]? scopes = null, IProgress<GitHubAuthProgress>? progressCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates an existing access token
    /// </summary>
    /// <param name="accessToken">Token to validate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Token validation result</returns>
    Task<GitHubTokenValidation> ValidateTokenAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores authentication token securely
    /// </summary>
    /// <param name="token">Token to store</param>
    /// <param name="associatedUser">Optional user identifier</param>
    /// <returns>True if stored successfully</returns>
    Task<bool> StoreTokenAsync(GitHubStoredToken token, string? associatedUser = null);

    /// <summary>
    /// Retrieves stored authentication token
    /// </summary>
    /// <param name="associatedUser">Optional user identifier</param>
    /// <returns>Stored token or null if not found</returns>
    Task<GitHubStoredToken?> GetStoredTokenAsync(string? associatedUser = null);

    /// <summary>
    /// Clears stored authentication token
    /// </summary>
    /// <param name="associatedUser">Optional user identifier</param>
    /// <returns>True if cleared successfully</returns>
    Task<bool> ClearStoredTokenAsync(string? associatedUser = null);

    /// <summary>
    /// Gets current authentication status
    /// </summary>
    /// <param name="associatedUser">Optional user identifier</param>
    /// <returns>Current authentication status</returns>
    Task<bool> IsAuthenticatedAsync(string? associatedUser = null);
}

/// <summary>
/// Implementation of GitHub device code flow authentication
/// </summary>
public class GitHubAuthenticationService : IGitHubAuthenticationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly GitHubAuthConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;

    public GitHubAuthenticationService(
        HttpClient httpClient,
        IConfigurationService configurationService,
        GitHubAuthConfig? config = null)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _config = config ?? new GitHubAuthConfig();

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        ConfigureHttpClient();
    }

    public async Task<GitHubDeviceCodeResponse> InitiateDeviceCodeFlowAsync(string[]? scopes = null, CancellationToken cancellationToken = default)
    {
        var requestScopes = scopes ?? _config.DefaultScopes;
        var request = new GitHubDeviceCodeRequest
        {
            ClientId = _config.ClientId,
            Scopes = requestScopes
        };

        // GitHub Apps (client_id starting with "Ov") don't use OAuth scopes —
        // permissions come from the App's configuration. Only include scope for classic OAuth apps.
        var requestBody = new Dictionary<string, object>
        {
            ["client_id"] = request.ClientId
        };

        var scopeString = string.Join(" ", requestScopes);
        if (!string.IsNullOrEmpty(scopeString) && !_config.ClientId.StartsWith("Ov"))
        {
            requestBody["scope"] = scopeString;
        }

        var content = new FormUrlEncodedContent(requestBody.Select(kvp =>
            new KeyValuePair<string, string>(kvp.Key, kvp.Value.ToString()!)));

        try
        {
            // GitHub OAuth endpoints require Accept: application/json (not the API accept header)
            var request2 = new HttpRequestMessage(HttpMethod.Post, _config.DeviceCodeUrl)
            {
                Content = content
            };
            request2.Headers.Accept.Clear();
            request2.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request2, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var deviceCodeResponse = JsonSerializer.Deserialize<GitHubDeviceCodeResponse>(responseJson, _jsonOptions);

            if (deviceCodeResponse == null)
            {
                throw new InvalidOperationException("Failed to parse device code response");
            }

            return deviceCodeResponse;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Failed to initiate device code flow: {ex.Message}", ex);
        }
    }

    public async Task<GitHubDeviceAuthStatus> PollForAuthenticationAsync(string deviceCode, CancellationToken cancellationToken = default)
    {
        var pollRequest = new GitHubTokenPollRequest
        {
            ClientId = _config.ClientId,
            DeviceCode = deviceCode
        };

        var requestBody = new Dictionary<string, string>
        {
            ["client_id"] = pollRequest.ClientId,
            ["device_code"] = pollRequest.DeviceCode,
            ["grant_type"] = pollRequest.GrantType
        };

        var content = new FormUrlEncodedContent(requestBody);

        try
        {
            // GitHub OAuth endpoints require Accept: application/json
            var tokenRequest = new HttpRequestMessage(HttpMethod.Post, _config.TokenUrl)
            {
                Content = content
            };
            tokenRequest.Headers.Accept.Clear();
            tokenRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(tokenRequest, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var tokenResponse = JsonSerializer.Deserialize<GitHubTokenResponse>(responseJson, _jsonOptions);
                if (tokenResponse != null && !string.IsNullOrEmpty(tokenResponse.AccessToken))
                {
                    return new GitHubDeviceAuthStatus
                    {
                        IsAuthenticated = true,
                        AccessToken = tokenResponse.AccessToken,
                        RefreshToken = tokenResponse.RefreshToken,
                        Scopes = tokenResponse.Scopes,
                        ExpiresAt = tokenResponse.ExpiresIn.HasValue
                            ? DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn.Value)
                            : null,
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }

            // Parse error response
            var errorResponse = ParseErrorResponse(responseJson);
            return new GitHubDeviceAuthStatus
            {
                IsAuthenticated = false,
                Error = errorResponse.error,
                ErrorDescription = errorResponse.errorDescription,
                CheckedAt = DateTime.UtcNow
            };
        }
        catch (HttpRequestException ex)
        {
            return new GitHubDeviceAuthStatus
            {
                IsAuthenticated = false,
                Error = "http_error",
                ErrorDescription = ex.Message,
                CheckedAt = DateTime.UtcNow
            };
        }
    }

    public async Task<GitHubDeviceAuthStatus> AuthenticateAsync(
        string[]? scopes = null,
        IProgress<GitHubAuthProgress>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var progress = new GitHubAuthProgress
        {
            CurrentStep = GitHubAuthStep.Initializing,
            StatusMessage = "Initializing authentication..."
        };
        progressCallback?.Report(progress);

        try
        {
            // Step 1: Request device code
            progress.CurrentStep = GitHubAuthStep.RequestingDeviceCode;
            progress.StatusMessage = "Requesting device code from GitHub...";
            progressCallback?.Report(progress);

            var deviceCodeResponse = await InitiateDeviceCodeFlowAsync(scopes, cancellationToken);

            // Step 2: Display user instructions
            progress.CurrentStep = GitHubAuthStep.WaitingForUserAuthorization;
            progress.UserCode = deviceCodeResponse.UserCode;
            progress.VerificationUrl = deviceCodeResponse.VerificationUriComplete;
            progress.TimeRemaining = TimeSpan.FromSeconds(deviceCodeResponse.ExpiresIn);
            progress.StatusMessage = $"Visit {deviceCodeResponse.VerificationUri} and enter code: {deviceCodeResponse.UserCode}";
            progressCallback?.Report(progress);

            // Step 3: Poll for authentication
            progress.CurrentStep = GitHubAuthStep.PollingForToken;
            progressCallback?.Report(progress);

            var pollingDelay = TimeSpan.FromSeconds(Math.Max(deviceCodeResponse.Interval, _config.PollingIntervalSeconds));
            var maxAttempts = Math.Min(_config.MaxPollingAttempts, deviceCodeResponse.ExpiresIn / deviceCodeResponse.Interval);
            var startTime = DateTime.UtcNow;
            var expiresAt = startTime.AddSeconds(deviceCodeResponse.ExpiresIn);

            for (int attempt = 0; attempt < maxAttempts && DateTime.UtcNow < expiresAt; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var authStatus = await PollForAuthenticationAsync(deviceCodeResponse.DeviceCode, cancellationToken);

                if (authStatus.IsAuthenticated)
                {
                    // Step 4: Validate token
                    progress.CurrentStep = GitHubAuthStep.ValidatingToken;
                    progress.StatusMessage = "Validating authentication token...";
                    progressCallback?.Report(progress);

                    var validation = await ValidateTokenAsync(authStatus.AccessToken!, cancellationToken);
                    if (validation.IsValid)
                    {
                        // Store token
                        var storedToken = new GitHubStoredToken
                        {
                            AccessToken = authStatus.AccessToken!,
                            RefreshToken = authStatus.RefreshToken,
                            Scopes = authStatus.Scopes,
                            CreatedAt = DateTime.UtcNow,
                            ExpiresAt = authStatus.ExpiresAt,
                            IsValid = true,
                            LastValidated = DateTime.UtcNow
                        };

                        await StoreTokenAsync(storedToken);

                        progress.CurrentStep = GitHubAuthStep.Complete;
                        progress.IsComplete = true;
                        progress.StatusMessage = "Authentication completed successfully!";
                        progressCallback?.Report(progress);

                        return authStatus;
                    }
                }

                if (authStatus.Error == "authorization_pending")
                {
                    // Continue polling
                    progress.TimeRemaining = expiresAt - DateTime.UtcNow;
                    progress.StatusMessage = $"Waiting for authorization... Time remaining: {progress.TimeRemaining?.ToString(@"mm\:ss")}";
                    progressCallback?.Report(progress);

                    await Task.Delay(pollingDelay, cancellationToken);
                    continue;
                }

                if (authStatus.Error == "slow_down")
                {
                    // Increase polling interval
                    pollingDelay = pollingDelay.Add(TimeSpan.FromSeconds(5));
                    await Task.Delay(pollingDelay, cancellationToken);
                    continue;
                }

                if (authStatus.Error == "expired_token" || authStatus.Error == "access_denied")
                {
                    progress.CurrentStep = GitHubAuthStep.Error;
                    progress.HasError = true;
                    progress.ErrorMessage = authStatus.ErrorDescription ?? authStatus.Error;
                    progressCallback?.Report(progress);

                    return authStatus;
                }
            }

            // Timeout
            var timeoutStatus = new GitHubDeviceAuthStatus
            {
                IsAuthenticated = false,
                Error = "timeout",
                ErrorDescription = "Authentication timed out",
                CheckedAt = DateTime.UtcNow
            };

            progress.CurrentStep = GitHubAuthStep.Error;
            progress.HasError = true;
            progress.ErrorMessage = "Authentication timed out";
            progressCallback?.Report(progress);

            return timeoutStatus;
        }
        catch (Exception ex)
        {
            progress.CurrentStep = GitHubAuthStep.Error;
            progress.HasError = true;
            progress.ErrorMessage = ex.Message;
            progressCallback?.Report(progress);

            return new GitHubDeviceAuthStatus
            {
                IsAuthenticated = false,
                Error = "exception",
                ErrorDescription = ex.Message,
                CheckedAt = DateTime.UtcNow
            };
        }
    }

    public async Task<GitHubTokenValidation> ValidateTokenAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var originalAuth = _httpClient.DefaultRequestHeaders.Authorization;

        try
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", accessToken);

            var response = await _httpClient.GetAsync($"{_config.ApiBaseUrl}/user", cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var scopes = response.Headers.Contains("X-OAuth-Scopes")
                    ? response.Headers.GetValues("X-OAuth-Scopes").FirstOrDefault()?.Split(',').Select(s => s.Trim()).ToArray() ?? Array.Empty<string>()
                    : Array.Empty<string>();

                return new GitHubTokenValidation
                {
                    IsValid = true,
                    Scopes = scopes,
                    ValidatedAt = DateTime.UtcNow
                };
            }

            return new GitHubTokenValidation
            {
                IsValid = false,
                ErrorMessage = $"Token validation failed: {response.StatusCode}",
                ValidatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new GitHubTokenValidation
            {
                IsValid = false,
                ErrorMessage = ex.Message,
                ValidatedAt = DateTime.UtcNow
            };
        }
        finally
        {
            _httpClient.DefaultRequestHeaders.Authorization = originalAuth;
        }
    }

    public async Task<bool> StoreTokenAsync(GitHubStoredToken token, string? associatedUser = null)
    {
        try
        {
            var key = GetTokenStorageKey(associatedUser);
            var tokenJson = JsonSerializer.Serialize(token, _jsonOptions);
            await _configurationService.SetAsync(key, tokenJson, global: true, encrypt: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<GitHubStoredToken?> GetStoredTokenAsync(string? associatedUser = null)
    {
        try
        {
            var key = GetTokenStorageKey(associatedUser);
            var tokenJson = await _configurationService.GetAsync(key);

            if (string.IsNullOrEmpty(tokenJson))
            {
                return null;
            }

            return JsonSerializer.Deserialize<GitHubStoredToken>(tokenJson, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> ClearStoredTokenAsync(string? associatedUser = null)
    {
        try
        {
            var key = GetTokenStorageKey(associatedUser);
            await _configurationService.DeleteAsync(key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsAuthenticatedAsync(string? associatedUser = null)
    {
        var storedToken = await GetStoredTokenAsync(associatedUser);
        if (storedToken == null || !storedToken.IsValid)
        {
            return false;
        }

        // Check if token is expired
        if (storedToken.ExpiresAt.HasValue && storedToken.ExpiresAt.Value <= DateTime.UtcNow)
        {
            return false;
        }

        // Validate token if it hasn't been validated recently
        if (storedToken.LastValidated < DateTime.UtcNow.AddHours(-1))
        {
            var validation = await ValidateTokenAsync(storedToken.AccessToken);
            if (!validation.IsValid)
            {
                await ClearStoredTokenAsync(associatedUser);
                return false;
            }

            // Update validation timestamp
            storedToken.LastValidated = DateTime.UtcNow;
            await StoreTokenAsync(storedToken, associatedUser);
        }

        return true;
    }

    private void ConfigureHttpClient()
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", _config.UserAgent);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
    }

    private static string GetTokenStorageKey(string? associatedUser = null)
    {
        return string.IsNullOrEmpty(associatedUser)
            ? "github.auth.token"
            : $"github.auth.token.{associatedUser}";
    }

    private static (string error, string? errorDescription) ParseErrorResponse(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;

            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString() ?? "unknown_error"
                : "unknown_error";

            var errorDescription = root.TryGetProperty("error_description", out var descElement)
                ? descElement.GetString()
                : null;

            return (error, errorDescription);
        }
        catch
        {
            return ("parse_error", "Failed to parse error response");
        }
    }
}