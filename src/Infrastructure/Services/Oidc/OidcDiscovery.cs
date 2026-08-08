using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Oidc;

public sealed class OidcDiscovery(HttpClient http) : IOidcDiscovery
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<ProtectedResourceMetadata?> ProtectedResourceAsync(string resourceUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(resourceUrl, UriKind.Absolute, out var uri)) return null;

        // RFC 9728 §3.1: the well-known segment is inserted after the host, and
        // the resource's path is appended to it. `/api/brain/v1` on example.com
        // becomes example.com/.well-known/oauth-protected-resource/api/brain/v1.
        var url = $"{uri.Scheme}://{uri.Authority}/.well-known/oauth-protected-resource{uri.AbsolutePath.TrimEnd('/')}";
        var doc = await GetJsonAsync<PrmDto>(url, ct);
        if (doc is null || string.IsNullOrEmpty(doc.Resource)) return null;

        return new ProtectedResourceMetadata(
            doc.Resource,
            doc.AuthorizationServers ?? [],
            doc.ScopesSupported ?? []);
    }

    public async Task<OidcEndpoints?> EndpointsAsync(string issuer, CancellationToken ct = default)
    {
        var b = issuer.TrimEnd('/');

        // OIDC discovery first because that is what our own realm serves and
        // what every Keycloak serves; RFC 8414 is the fallback for plain OAuth
        // servers that never implemented OpenID Connect.
        var doc = await GetJsonAsync<AsMetadataDto>($"{b}/.well-known/openid-configuration", ct)
               ?? await GetJsonAsync<AsMetadataDto>($"{b}/.well-known/oauth-authorization-server", ct);

        if (doc is null || string.IsNullOrEmpty(doc.TokenEndpoint)) return null;

        return new OidcEndpoints(
            string.IsNullOrEmpty(doc.Issuer) ? b : doc.Issuer,
            doc.TokenEndpoint,
            doc.DeviceAuthorizationEndpoint,
            doc.AuthorizationEndpoint);
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Discovery is an optimisation. A receiver that does not answer, or
            // answers with something unparseable, falls back to the documented
            // default paths rather than failing the command.
            return null;
        }
    }

    private sealed class PrmDto
    {
        [JsonPropertyName("resource")] public string? Resource { get; set; }
        [JsonPropertyName("authorization_servers")] public List<string>? AuthorizationServers { get; set; }
        [JsonPropertyName("scopes_supported")] public List<string>? ScopesSupported { get; set; }
    }

    private sealed class AsMetadataDto
    {
        [JsonPropertyName("issuer")] public string? Issuer { get; set; }
        [JsonPropertyName("token_endpoint")] public string? TokenEndpoint { get; set; }
        [JsonPropertyName("device_authorization_endpoint")] public string? DeviceAuthorizationEndpoint { get; set; }
        [JsonPropertyName("authorization_endpoint")] public string? AuthorizationEndpoint { get; set; }
    }
}
