using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Azure;

/// <summary>
/// Tokens from the Azure Instance Metadata Service — the managed identity of the VM (or container
/// host) pks is running on. No secret is stored anywhere: the platform hands a token to whatever can
/// reach 169.254.169.254 from inside the machine, which is exactly why `pks providers init` prefers
/// it over a refresh token on an Azure box.
/// </summary>
public interface IManagedIdentityClient
{
    /// <summary>True when an IMDS endpoint answers. Cheap and bounded (about a second): it is asked on
    /// every `pks providers init`, most of which run nowhere near Azure.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>An access token for <paramref name="scope"/> (a v2 scope such as
    /// <c>https://management.azure.com/.default</c>, or a bare resource). <paramref name="clientId"/>
    /// selects a user-assigned identity; null takes the system-assigned one. Null when IMDS refuses —
    /// typically no identity on the VM, or the identity id is wrong.</summary>
    Task<string?> GetTokenAsync(string scope, string? clientId = null, CancellationToken cancellationToken = default);
}

public sealed class ManagedIdentityClient : IManagedIdentityClient
{
    // Link-local, answered by the hypervisor. Not configurable on purpose: a "managed identity" at any
    // other address is someone else's token server.
    internal const string ImdsBase = "http://169.254.169.254";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1200);

    private readonly HttpClient _http;
    private readonly ILogger<ManagedIdentityClient> _logger;
    private bool? _available;

    public ManagedIdentityClient(HttpClient http, ILogger<ManagedIdentityClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_available is { } known) return known;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ImdsBase}/metadata/instance?api-version=2021-02-01");
            request.Headers.Add("Metadata", "true");
            using var response = await _http.SendAsync(request, cts.Token);
            _available = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _available = false;
        }
        return _available.Value;
    }

    public async Task<string?> GetTokenAsync(string scope, string? clientId = null, CancellationToken cancellationToken = default)
    {
        var resource = ResourceFromScope(scope);
        var url = $"{ImdsBase}/metadata/identity/oauth2/token?api-version=2018-02-01&resource={Uri.EscapeDataString(resource)}";
        if (!string.IsNullOrWhiteSpace(clientId))
            url += $"&client_id={Uri.EscapeDataString(clientId)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Metadata", "true");
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // The body is IMDS's own error ("Identity not found" etc.), never a token.
                _logger.LogWarning("Managed identity token for {Resource} refused: {Status} {Body}",
                    resource, (int)response.StatusCode, body.Length > 300 ? body[..300] : body);
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("access_token", out var token) ? token.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            _logger.LogWarning("Managed identity token for {Resource} failed: {Message}", resource, ex.Message);
            return null;
        }
    }

    /// <summary>IMDS speaks v1 (<c>resource=</c>), everything else in pks speaks v2 scopes. The first
    /// scope wins; <c>offline_access</c> and friends mean nothing to a managed identity.</summary>
    internal static string ResourceFromScope(string scope)
    {
        var first = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(s => s.Contains("://", StringComparison.Ordinal)) ?? scope.Trim();
        return first.EndsWith("/.default", StringComparison.Ordinal) ? first[..^"/.default".Length] : first;
    }
}
