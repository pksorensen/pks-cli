using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Interface for Microsoft Graph authentication services using device code flow
/// </summary>
public interface IMsGraphAuthenticationService
{
    /// <summary>
    /// Initiates device code flow authentication with Microsoft Entra ID
    /// </summary>
    Task<MsGraphDeviceCodeResponse> InitiateDeviceCodeFlowAsync(string[]? scopes = null, CancellationToken ct = default);

    /// <summary>
    /// Polls for authentication completion using device code
    /// </summary>
    Task<MsGraphDeviceAuthStatus> PollForAuthenticationAsync(string deviceCode, CancellationToken ct = default);

    /// <summary>
    /// Performs complete device code flow authentication with progress reporting
    /// </summary>
    Task<MsGraphDeviceAuthStatus> AuthenticateAsync(string[]? scopes = null, IProgress<MsGraphAuthProgress>? progressCallback = null, CancellationToken ct = default);

    /// <summary>
    /// Validates an existing access token against Microsoft Graph /me endpoint
    /// </summary>
    Task<bool> ValidateTokenAsync(string accessToken, CancellationToken ct = default);

    /// <summary>
    /// Stores authentication token securely
    /// </summary>
    Task<bool> StoreTokenAsync(MsGraphStoredToken token);

    /// <summary>
    /// Retrieves stored authentication token
    /// </summary>
    Task<MsGraphStoredToken?> GetStoredTokenAsync();

    /// <summary>
    /// Clears stored authentication token
    /// </summary>
    Task<bool> ClearStoredTokenAsync();

    /// <summary>
    /// Gets current authentication status
    /// </summary>
    Task<bool> IsAuthenticatedAsync();

    /// <summary>
    /// Refreshes the access token using the stored refresh token
    /// </summary>
    Task<MsGraphStoredToken?> RefreshTokenAsync();

    /// <summary>
    /// Gets a valid access token, auto-refreshing if expired. Returns null if unavailable.
    /// </summary>
    Task<string?> GetValidAccessTokenAsync();

    /// <summary>
    /// Persists client_id and tenant_id configuration
    /// </summary>
    Task StoreConfigAsync(string clientId, string tenantId);

    /// <summary>
    /// Loads persisted client_id and tenant_id configuration
    /// </summary>
    Task<MsGraphAuthConfig?> LoadConfigAsync();
}

/// <summary>
/// Implementation of Microsoft Graph device code flow authentication via Entra ID
/// </summary>
public class MsGraphAuthenticationService : IMsGraphAuthenticationService
{
    private const string TokenStorageKey = "msgraph.auth.token";
    private const string ConfigStorageKey = "msgraph.auth.config";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<MsGraphAuthenticationService> _logger;
    private MsGraphAuthConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;

    public MsGraphAuthenticationService(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<MsGraphAuthenticationService> logger,
        MsGraphAuthConfig? config = null)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _logger = logger;
        _config = config ?? new MsGraphAuthConfig();

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        ConfigureHttpClient();
    }

    public async Task<MsGraphDeviceCodeResponse> InitiateDeviceCodeFlowAsync(string[]? scopes = null, CancellationToken ct = default)
    {
        var requestScopes = scopes ?? _config.DefaultScopes;
        var scopeString = string.Join(" ", requestScopes);

        var requestBody = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", _config.ClientId),
            new KeyValuePair<string, string>("scope", scopeString)
        });

        try
        {
            var response = await _httpClient.PostAsync(_config.DeviceCodeUrl, requestBody, ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Device code request failed: {StatusCode} {Response}", response.StatusCode, responseJson);
                throw new HttpRequestException(
                    $"Device code request failed ({response.StatusCode}): {responseJson}");
            }
            var deviceCodeResponse = JsonSerializer.Deserialize<MsGraphDeviceCodeResponse>(responseJson, _jsonOptions);

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

    public async Task<MsGraphDeviceAuthStatus> PollForAuthenticationAsync(string deviceCode, CancellationToken ct = default)
    {
        var requestBody = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", _config.ClientId),
            new KeyValuePair<string, string>("device_code", deviceCode),
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
        });

        try
        {
            var response = await _httpClient.PostAsync(_config.TokenUrl, requestBody, ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                var tokenResponse = JsonSerializer.Deserialize<MsGraphTokenResponse>(responseJson, _jsonOptions);
                if (tokenResponse != null && !string.IsNullOrEmpty(tokenResponse.AccessToken))
                {
                    return new MsGraphDeviceAuthStatus
                    {
                        IsAuthenticated = true,
                        AccessToken = tokenResponse.AccessToken,
                        RefreshToken = tokenResponse.RefreshToken,
                        Scopes = tokenResponse.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>(),
                        ExpiresAt = tokenResponse.ExpiresIn.HasValue
                            ? DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn.Value)
                            : null,
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }

            // Parse error response
            var errorResponse = ParseErrorResponse(responseJson);
            return new MsGraphDeviceAuthStatus
            {
                IsAuthenticated = false,
                Error = errorResponse.error,
                ErrorDescription = errorResponse.errorDescription,
                CheckedAt = DateTime.UtcNow
            };
        }
        catch (HttpRequestException ex)
        {
            return new MsGraphDeviceAuthStatus
            {
                IsAuthenticated = false,
                Error = "http_error",
                ErrorDescription = ex.Message,
                CheckedAt = DateTime.UtcNow
            };
        }
    }

    public async Task<MsGraphDeviceAuthStatus> AuthenticateAsync(
        string[]? scopes = null,
        IProgress<MsGraphAuthProgress>? progressCallback = null,
        CancellationToken ct = default)
    {
        var progress = new MsGraphAuthProgress
        {
            CurrentStep = MsGraphAuthStep.Initializing,
            StatusMessage = "Initializing authentication..."
        };
        progressCallback?.Report(progress);

        try
        {
            // Ensure config is loaded (client_id / tenant_id)
            if (string.IsNullOrEmpty(_config.ClientId))
            {
                var loaded = await LoadConfigAsync();
                if (loaded != null)
                {
                    _config = loaded;
                }
                else
                {
                    throw new InvalidOperationException(
                        "Microsoft Graph is not configured. Run 'pks ms-graph register' first.");
                }
            }

            // Step 1: Request device code
            progress.CurrentStep = MsGraphAuthStep.RequestingDeviceCode;
            progress.StatusMessage = "Requesting device code from Microsoft Entra ID...";
            progressCallback?.Report(progress);

            var deviceCodeResponse = await InitiateDeviceCodeFlowAsync(scopes, ct);

            // Step 2: Display user instructions
            progress.CurrentStep = MsGraphAuthStep.WaitingForUserAuthorization;
            progress.UserCode = deviceCodeResponse.UserCode;
            progress.VerificationUrl = deviceCodeResponse.VerificationUriComplete;
            progress.TimeRemaining = TimeSpan.FromSeconds(deviceCodeResponse.ExpiresIn);
            progress.StatusMessage = $"Visit {deviceCodeResponse.VerificationUri} and enter code: {deviceCodeResponse.UserCode}";
            progressCallback?.Report(progress);

            // Step 3: Poll for authentication
            progress.CurrentStep = MsGraphAuthStep.PollingForToken;
            progressCallback?.Report(progress);

            var pollingDelay = TimeSpan.FromSeconds(Math.Max(deviceCodeResponse.Interval, _config.PollingIntervalSeconds));
            var maxAttempts = Math.Min(_config.MaxPollingAttempts, deviceCodeResponse.ExpiresIn / deviceCodeResponse.Interval);
            var startTime = DateTime.UtcNow;
            var expiresAt = startTime.AddSeconds(deviceCodeResponse.ExpiresIn);

            for (int attempt = 0; attempt < maxAttempts && DateTime.UtcNow < expiresAt; attempt++)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var authStatus = await PollForAuthenticationAsync(deviceCodeResponse.DeviceCode, ct);

                if (authStatus.IsAuthenticated)
                {
                    // Step 4: Validate token
                    progress.CurrentStep = MsGraphAuthStep.ValidatingToken;
                    progress.StatusMessage = "Validating authentication token...";
                    progressCallback?.Report(progress);

                    var isValid = await ValidateTokenAsync(authStatus.AccessToken!, ct);
                    if (isValid)
                    {
                        // Store token
                        var storedToken = new MsGraphStoredToken
                        {
                            AccessToken = authStatus.AccessToken!,
                            RefreshToken = authStatus.RefreshToken,
                            Scopes = authStatus.Scopes,
                            ClientId = _config.ClientId,
                            TenantId = _config.TenantId,
                            CreatedAt = DateTime.UtcNow,
                            ExpiresAt = authStatus.ExpiresAt,
                            IsValid = true,
                            LastValidated = DateTime.UtcNow
                        };

                        await StoreTokenAsync(storedToken);

                        progress.CurrentStep = MsGraphAuthStep.Complete;
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

                    await Task.Delay(pollingDelay, ct);
                    continue;
                }

                if (authStatus.Error == "slow_down")
                {
                    // Increase polling interval
                    pollingDelay = pollingDelay.Add(TimeSpan.FromSeconds(5));
                    await Task.Delay(pollingDelay, ct);
                    continue;
                }

                if (authStatus.Error == "expired_token" || authStatus.Error == "access_denied")
                {
                    progress.CurrentStep = MsGraphAuthStep.Error;
                    progress.HasError = true;
                    progress.ErrorMessage = authStatus.ErrorDescription ?? authStatus.Error;
                    progressCallback?.Report(progress);

                    return authStatus;
                }
            }

            // Timeout
            var timeoutStatus = new MsGraphDeviceAuthStatus
            {
                IsAuthenticated = false,
                Error = "timeout",
                ErrorDescription = "Authentication timed out",
                CheckedAt = DateTime.UtcNow
            };

            progress.CurrentStep = MsGraphAuthStep.Error;
            progress.HasError = true;
            progress.ErrorMessage = "Authentication timed out";
            progressCallback?.Report(progress);

            return timeoutStatus;
        }
        catch (Exception ex)
        {
            progress.CurrentStep = MsGraphAuthStep.Error;
            progress.HasError = true;
            progress.ErrorMessage = ex.Message;
            progressCallback?.Report(progress);

            return new MsGraphDeviceAuthStatus
            {
                IsAuthenticated = false,
                Error = "exception",
                ErrorDescription = ex.Message,
                CheckedAt = DateTime.UtcNow
            };
        }
    }

    public async Task<bool> ValidateTokenAsync(string accessToken, CancellationToken ct = default)
    {
        var originalAuth = _httpClient.DefaultRequestHeaders.Authorization;

        try
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await _httpClient.GetAsync($"{_config.GraphBaseUrl}/me", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token validation failed");
            return false;
        }
        finally
        {
            _httpClient.DefaultRequestHeaders.Authorization = originalAuth;
        }
    }

    public async Task<bool> StoreTokenAsync(MsGraphStoredToken token)
    {
        try
        {
            var tokenJson = JsonSerializer.Serialize(token, _jsonOptions);
            await _configurationService.SetAsync(TokenStorageKey, tokenJson, global: true, encrypt: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<MsGraphStoredToken?> GetStoredTokenAsync()
    {
        try
        {
            var tokenJson = await _configurationService.GetAsync(TokenStorageKey);

            if (string.IsNullOrEmpty(tokenJson))
            {
                return null;
            }

            return JsonSerializer.Deserialize<MsGraphStoredToken>(tokenJson, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> ClearStoredTokenAsync()
    {
        try
        {
            await _configurationService.DeleteAsync(TokenStorageKey);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var storedToken = await GetStoredTokenAsync();
        if (storedToken == null || !storedToken.IsValid)
        {
            return false;
        }

        // Check if token is expired
        if (storedToken.ExpiresAt.HasValue && storedToken.ExpiresAt.Value <= DateTime.UtcNow)
        {
            return false;
        }

        // Validate token if it hasn't been validated recently (>1hr)
        if (storedToken.LastValidated < DateTime.UtcNow.AddHours(-1))
        {
            var isValid = await ValidateTokenAsync(storedToken.AccessToken);
            if (!isValid)
            {
                await ClearStoredTokenAsync();
                return false;
            }

            // Update validation timestamp
            storedToken.LastValidated = DateTime.UtcNow;
            await StoreTokenAsync(storedToken);
        }

        return true;
    }

    public async Task<MsGraphStoredToken?> RefreshTokenAsync()
    {
        await EnsureConfigLoadedAsync();
        var storedToken = await GetStoredTokenAsync();
        if (storedToken == null || string.IsNullOrEmpty(storedToken.RefreshToken))
        {
            _logger.LogWarning("Cannot refresh token: {Reason}",
                storedToken == null ? "no stored token found" : "no refresh token available");
            return null;
        }

        try
        {
            var requestBody = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", _config.ClientId),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", storedToken.RefreshToken),
                new KeyValuePair<string, string>("scope", string.Join(" ", storedToken.Scopes))
            });

            var response = await _httpClient.PostAsync(_config.TokenUrl, requestBody);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Token refresh HTTP failed: {StatusCode} {Response}", response.StatusCode, content);
                return null;
            }

            var tokenResponse = JsonSerializer.Deserialize<MsGraphTokenResponse>(content, _jsonOptions);
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                _logger.LogError("Token refresh returned no access token. Response: {Response}", content);
                return null;
            }

            _logger.LogInformation("Refresh response: expires_in={ExpiresIn}, has_refresh_token={HasRefresh}",
                tokenResponse.ExpiresIn, tokenResponse.RefreshToken != null);

            const int defaultExpiresInSeconds = 3600; // 1 hour default for Entra ID

            var newToken = new MsGraphStoredToken
            {
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken ?? storedToken.RefreshToken,
                Scopes = tokenResponse.Scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? storedToken.Scopes,
                ClientId = storedToken.ClientId,
                TenantId = storedToken.TenantId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn ?? defaultExpiresInSeconds),
                IsValid = true,
                LastValidated = DateTime.UtcNow
            };

            _logger.LogInformation("Token refreshed successfully, new token expires at {ExpiresAt}", newToken.ExpiresAt);
            await StoreTokenAsync(newToken);
            return newToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token refresh failed with exception");
            return null;
        }
    }

    public async Task<string?> GetValidAccessTokenAsync()
    {
        await EnsureConfigLoadedAsync();
        var storedToken = await GetStoredTokenAsync();
        if (storedToken == null)
        {
            return null;
        }

        // If token is not expired, return it
        if (storedToken.ExpiresAt.HasValue && storedToken.ExpiresAt.Value > DateTime.UtcNow)
        {
            return storedToken.AccessToken;
        }

        // Try to refresh
        var refreshedToken = await RefreshTokenAsync();
        return refreshedToken?.AccessToken;
    }

    public async Task StoreConfigAsync(string clientId, string tenantId)
    {
        // Update in-memory config so subsequent calls use the correct values
        _config.ClientId = clientId;
        _config.TenantId = tenantId;

        var configJson = JsonSerializer.Serialize(new { clientId, tenantId }, _jsonOptions);
        await _configurationService.SetAsync(ConfigStorageKey, configJson, global: true, encrypt: false);
    }

    public async Task<MsGraphAuthConfig?> LoadConfigAsync()
    {
        try
        {
            var configJson = await _configurationService.GetAsync(ConfigStorageKey);
            if (string.IsNullOrEmpty(configJson))
            {
                return null;
            }

            using var document = JsonDocument.Parse(configJson);
            var root = document.RootElement;

            var clientId = root.TryGetProperty("client_id", out var cidElement)
                ? cidElement.GetString()
                : null;
            var tenantId = root.TryGetProperty("tenant_id", out var tidElement)
                ? tidElement.GetString()
                : null;

            if (string.IsNullOrEmpty(clientId))
            {
                return null;
            }

            return new MsGraphAuthConfig
            {
                ClientId = clientId,
                TenantId = tenantId ?? "common"
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task EnsureConfigLoadedAsync()
    {
        if (!string.IsNullOrEmpty(_config.ClientId)) return;
        var loaded = await LoadConfigAsync();
        if (loaded != null)
        {
            _config = loaded;
        }
    }

    private void ConfigureHttpClient()
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", _config.UserAgent);
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
