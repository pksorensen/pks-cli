using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Runner;

namespace PKS.Infrastructure.Services.Agentics;

/// <summary>
/// Default implementation of <see cref="IAgenticsAuthService"/>.
///
/// Priority chain:
///   1. Explicit token passed in (--token flag)
///   2. GitHub Actions OIDC token (when running inside GitHub Actions, the
///      caller-supplied audience is bound into the issued JWT)
///   3. Stored Keycloak access token from `pks agentics init` (Phase 3 —
///      currently returns null until the auth-config service ships)
///   4. Stored runner registration token (back-compat)
/// </summary>
public class AgenticsAuthService(
    IAgenticsRunnerConfigurationService runnerConfig,
    IAgenticsAuthConfigurationService authConfig) : IAgenticsAuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<string?> GetTokenAsync(string audience, string? explicitToken, string owner, string project)
    {
        // 1. Explicit override
        if (!string.IsNullOrEmpty(explicitToken)) return explicitToken;

        // 2. GitHub Actions OIDC
        var oidc = await TryFetchGitHubOidcTokenAsync(audience);
        if (oidc != null) return oidc;

        // 3. Stored Keycloak token from `pks agentics init`. Refresh in place
        //    when access token has expired but refresh token is still valid.
        var creds = await authConfig.LoadAsync();
        if (creds != null && !string.IsNullOrEmpty(creds.AccessToken))
        {
            if (creds.IsExpired && !string.IsNullOrEmpty(creds.RefreshToken))
            {
                var refreshed = await TryRefreshAsync(creds);
                if (refreshed != null)
                {
                    await authConfig.SaveAsync(refreshed);
                    return refreshed.AccessToken;
                }
                // Refresh failed; fall through to runner-token back-compat.
            }
            else if (!creds.IsExpired)
            {
                return creds.AccessToken;
            }
        }

        // 4. Stored runner registration (back-compat)
        var registrations = await runnerConfig.LoadAsync();
        var registration = registrations.Registrations
            .Where(r => string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.RegisteredAt)
            .FirstOrDefault();

        return registration?.Token;
    }

    private static async Task<AgenticsAuthCredentials?> TryRefreshAsync(AgenticsAuthCredentials creds)
    {
        try
        {
            var keycloakBase = $"https://keycloak.{creds.Server.TrimEnd('/')}/realms/{creds.Realm}";
            using var http = new HttpClient();
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("client_id", creds.ClientId),
                new KeyValuePair<string, string>("refresh_token", creds.RefreshToken!),
            });
            using var resp = await http.PostAsync($"{keycloakBase}/protocol/openid-connect/token", form);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync();
            var tok = JsonSerializer.Deserialize<RefreshResponse>(body, JsonOptions);
            if (tok == null || string.IsNullOrEmpty(tok.AccessToken)) return null;
            return new AgenticsAuthCredentials
            {
                Server = creds.Server,
                Realm = creds.Realm,
                ClientId = creds.ClientId,
                AccessToken = tok.AccessToken,
                RefreshToken = tok.RefreshToken ?? creds.RefreshToken,
                IdToken = tok.IdToken ?? creds.IdToken,
                ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + tok.ExpiresIn,
            };
        }
        catch
        {
            return null;
        }
    }

    private class RefreshResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    /// <summary>
    /// When running in GitHub Actions with `permissions: id-token: write`,
    /// fetches an OIDC token whose `aud` claim equals <paramref name="audience"/>.
    /// Returns null when not in Actions, when the env vars are missing, or on
    /// any error.
    /// </summary>
    private static async Task<string?> TryFetchGitHubOidcTokenAsync(string audience)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true",
                StringComparison.OrdinalIgnoreCase))
            return null;

        var requestUrl = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL");
        var requestToken = Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN");
        if (string.IsNullOrEmpty(requestUrl) || string.IsNullOrEmpty(requestToken))
            return null;

        try
        {
            using var http = new HttpClient();
            var url = $"{requestUrl}&audience={Uri.EscapeDataString(audience)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", requestToken);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadFromJsonAsync<OidcTokenResponse>(JsonOptions);
            return string.IsNullOrEmpty(body?.Value) ? null : body.Value;
        }
        catch
        {
            return null;
        }
    }

    private class OidcTokenResponse
    {
        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }
}
