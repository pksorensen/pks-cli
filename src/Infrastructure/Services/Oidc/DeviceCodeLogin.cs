using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Oidc;

public sealed class DeviceCodeLogin(HttpClient http) : IDeviceCodeLogin
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<OidcLoginResult> LoginAsync(DeviceLoginRequest request, CancellationToken ct = default)
    {
        var endpoints = request.Endpoints;
        if (!endpoints.SupportsDeviceGrant)
            return OidcLoginResult.Failed("This identity provider does not offer the device grant.");

        // PKCE, even though the device grant has no redirect to intercept.
        // Keycloak's public clients require S256 and enforce it here too — the
        // device request comes back "Missing parameter: code_challenge_method"
        // without it — and RFC 8628 §5.2 wants the extra binding regardless.
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        DeviceCodeDto? device;
        try
        {
            var fields = new List<KeyValuePair<string, string>>
            {
                new("client_id", request.ClientId),
                new("scope", request.Scope),
                new("code_challenge", challenge),
                new("code_challenge_method", "S256"),
            };
            if (!string.IsNullOrEmpty(request.Resource)) fields.Add(new("resource", request.Resource));

            using var resp = await http.PostAsync(endpoints.DeviceAuthorizationEndpoint,
                new FormUrlEncodedContent(fields), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return OidcLoginResult.Failed($"{(int)resp.StatusCode} from the device endpoint: {Trim(body)}");

            device = JsonSerializer.Deserialize<DeviceCodeDto>(body, Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return OidcLoginResult.Failed(ex.Message);
        }

        if (device?.DeviceCode is null || device.UserCode is null)
            return OidcLoginResult.Failed("The device endpoint answered without a code.");

        request.OnUserCode(new DeviceCodePrompt(
            device.UserCode, device.VerificationUri ?? "", device.VerificationUriComplete));

        // RFC 8628 §3.5. `interval` is the server's floor, not a suggestion;
        // polling faster earns slow_down and, on some providers, a ban.
        var interval = TimeSpan.FromSeconds(Math.Max(5, device.Interval));
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, device.ExpiresIn));

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);

            var fields = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                new("device_code", device.DeviceCode),
                new("client_id", request.ClientId),
                new("code_verifier", verifier),
            };
            if (!string.IsNullOrEmpty(request.Resource)) fields.Add(new("resource", request.Resource));

            string body;
            bool ok;
            try
            {
                using var resp = await http.PostAsync(endpoints.TokenEndpoint, new FormUrlEncodedContent(fields), ct);
                ok = resp.IsSuccessStatusCode;
                body = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                return OidcLoginResult.Failed(ex.Message);
            }

            if (ok)
            {
                var token = JsonSerializer.Deserialize<TokenDto>(body, Json);
                if (token is null || string.IsNullOrEmpty(token.AccessToken))
                    return OidcLoginResult.Failed("The token endpoint answered without an access token.");

                return new OidcLoginResult(
                    token.AccessToken,
                    token.RefreshToken,
                    token.IdToken,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() + token.ExpiresIn,
                    null);
            }

            string? error;
            try { error = JsonSerializer.Deserialize<ErrorDto>(body, Json)?.Error; }
            catch (JsonException) { return OidcLoginResult.Failed(Trim(body)); }

            switch (error)
            {
                case "authorization_pending": continue;
                case "slow_down": interval += TimeSpan.FromSeconds(5); continue;
                case null: return OidcLoginResult.Failed(Trim(body));
                default: return OidcLoginResult.Failed(error);
            }
        }

        return OidcLoginResult.Failed("expired_token");
    }

    public async Task<OidcLoginResult> RefreshAsync(
        OidcEndpoints endpoints,
        string clientId,
        string refreshToken,
        string? resource,
        CancellationToken ct = default)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("client_id", clientId),
            new("refresh_token", refreshToken),
        };
        if (!string.IsNullOrEmpty(resource)) fields.Add(new("resource", resource));

        try
        {
            using var resp = await http.PostAsync(endpoints.TokenEndpoint, new FormUrlEncodedContent(fields), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return OidcLoginResult.Failed(Trim(body));

            var token = JsonSerializer.Deserialize<TokenDto>(body, Json);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                return OidcLoginResult.Failed("The token endpoint answered without an access token.");

            return new OidcLoginResult(
                token.AccessToken,
                // Keycloak rotates refresh tokens; a provider that does not
                // omits the field, and dropping the old one there would log the
                // user out on the next run.
                token.RefreshToken ?? refreshToken,
                token.IdToken,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + token.ExpiresIn,
                null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return OidcLoginResult.Failed(ex.Message);
        }
    }

    private static string Trim(string body) => body.Length > 300 ? body[..300] : body;

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class DeviceCodeDto
    {
        [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
        [JsonPropertyName("user_code")] public string? UserCode { get; set; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
        [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
    }

    private sealed class TokenDto
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class ErrorDto
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }
}
