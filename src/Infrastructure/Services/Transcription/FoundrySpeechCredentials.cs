using System.Net.Http.Headers;
using System.Text.Json;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Transcription;

/// <summary>
/// Resolves the host and the authorization header for the Azure speech and OpenAI data
/// planes, and defines the environment contract a child process is handed them by.
///
/// THE CONTRACT. <c>pks</c> exports these onto any tool it shells out to, and reads them
/// itself when they are already set — which is what lets a job on a runner supply a
/// credential without a <c>pks foundry init</c> having ever run there:
///
///   PKS_FOUNDRY_ENDPOINT   https://{resource}.cognitiveservices.azure.com
///   PKS_FOUNDRY_TOKEN      Entra access token for the Cognitive Services scope
///   PKS_FOUNDRY_API_KEY    resource key, when the resource accepts keys
///   PKS_FOUNDRY_LOCALE     BCP-47 default locale
///
/// The <c>HEYPOUL_*</c> names are read as aliases so the dictation daemon keeps working
/// against the same handoff without being changed.
///
/// TWO HEADER NAMES, NOT ONE. Azure *Speech* takes <c>Ocp-Apim-Subscription-Key</c>; the
/// Azure *OpenAI* data plane takes <c>api-key</c> and answers the Speech header with a 401
/// reading "invalid subscription key or wrong API endpoint" — which sends you looking for a
/// bad key when the key is fine. A bearer token works on both, so only the key branch differs.
/// </summary>
public interface IFoundrySpeechCredentials
{
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    /// <summary>Host only, no scheme: <c>{resource}.cognitiveservices.azure.com</c>.</summary>
    Task<string> ResolveHostAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply auth to a request. <paramref name="dataPlane"/> picks which key header is used;
    /// a bearer token is identical on both.
    /// </summary>
    Task ApplyAsync(HttpRequestMessage request, FoundryDataPlane dataPlane, CancellationToken cancellationToken = default);

    /// <summary>
    /// The contract above, as variables to export onto a child process. Returns an empty map
    /// when nothing is configured rather than throwing — a caller with no credential should
    /// spawn the child and let it report that, not fail here.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> BuildChildEnvironmentAsync(CancellationToken cancellationToken = default);
}

public enum FoundryDataPlane
{
    /// <summary>speechtotext/… — key header is Ocp-Apim-Subscription-Key.</summary>
    Speech,

    /// <summary>openai/deployments/… — key header is api-key.</summary>
    OpenAi,
}

public sealed class FoundrySpeechCredentials : IFoundrySpeechCredentials
{
    private readonly IAzureFoundryAuthService _auth;
    private readonly AzureFoundryAuthConfig _config;

    public FoundrySpeechCredentials(IAzureFoundryAuthService auth, AzureFoundryAuthConfig config)
    {
        _auth = auth;
        _config = config;
    }

    private static string? Env(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var token = Env("PKS_FOUNDRY_TOKEN", "HEYPOUL_TOKEN");
        if (token is not null && !TokenExpired(token)) return true;
        if (Env("PKS_FOUNDRY_API_KEY", "HEYPOUL_API_KEY", "AZURE_SPEECH_KEY") is not null) return true;

        return await _auth.IsAuthenticatedAsync();
    }

    public async Task<string> ResolveHostAsync(CancellationToken cancellationToken = default)
    {
        var explicitHost = Env("PKS_FOUNDRY_ENDPOINT", "PKS_FOUNDRY_HOST", "HEYPOUL_ENDPOINT");
        if (explicitHost is not null) return StripScheme(explicitHost);

        var credentials = await _auth.GetStoredCredentialsAsync();
        var resource = credentials?.SelectedResourceName;
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new InvalidOperationException(
                "No Foundry resource configured. Run 'pks foundry init', or set PKS_FOUNDRY_ENDPOINT.");
        }

        // The resource's custom subdomain serves the Speech REST API, which avoids needing
        // the region — not always stored alongside the Foundry credential.
        return $"{resource}.cognitiveservices.azure.com";
    }

    private static string StripScheme(string host)
        => host.Replace("https://", "", StringComparison.OrdinalIgnoreCase)
               .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
               .TrimEnd('/');

    public async Task ApplyAsync(
        HttpRequestMessage request, FoundryDataPlane dataPlane, CancellationToken cancellationToken = default)
    {
        // Three paths, in order of directness.
        //
        // An exported token is the handoff contract and the development path both: this
        // Foundry resource is Entra-only on the data plane, so during development the token
        // *is* the credential. It expires in about an hour, which is fine for one run.
        var token = Env("PKS_FOUNDRY_TOKEN", "HEYPOUL_TOKEN");
        if (token is not null && !TokenExpired(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return;
        }

        var key = Env("PKS_FOUNDRY_API_KEY", "HEYPOUL_API_KEY", "AZURE_SPEECH_KEY");
        if (key is not null)
        {
            ApplyKey(request, dataPlane, key);
            return;
        }

        var acquired = await _auth.GetAccessTokenAsync(_config.CognitiveScope, cancellationToken);
        if (!string.IsNullOrEmpty(acquired))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acquired);
            return;
        }

        var stored = await _auth.GetStoredCredentialsAsync();
        var storedKey = stored?.ApiKey.Reveal();
        if (!string.IsNullOrEmpty(storedKey))
        {
            ApplyKey(request, dataPlane, storedKey);
            return;
        }

        throw new InvalidOperationException(
            "No Foundry speech credential. Run 'pks foundry init', or set PKS_FOUNDRY_TOKEN / PKS_FOUNDRY_API_KEY.");
    }

    private static void ApplyKey(HttpRequestMessage request, FoundryDataPlane dataPlane, string key)
    {
        var header = dataPlane == FoundryDataPlane.OpenAi ? "api-key" : "Ocp-Apim-Subscription-Key";
        request.Headers.TryAddWithoutValidation(header, key);
    }

    public async Task<IReadOnlyDictionary<string, string>> BuildChildEnvironmentAsync(
        CancellationToken cancellationToken = default)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            env["PKS_FOUNDRY_ENDPOINT"] = "https://" + await ResolveHostAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return env;
        }

        var token = Env("PKS_FOUNDRY_TOKEN", "HEYPOUL_TOKEN");
        if (token is null || TokenExpired(token))
        {
            token = await _auth.GetAccessTokenAsync(_config.CognitiveScope, cancellationToken);
        }
        if (!string.IsNullOrEmpty(token)) env["PKS_FOUNDRY_TOKEN"] = token;

        return env;
    }

    /// <summary>
    /// A token that is about to expire is worse than no token: the request fails a minute
    /// into a long job rather than at the start. One minute of headroom, and a token we
    /// cannot parse is treated as valid — it may be a format we do not know.
    /// </summary>
    private static bool TokenExpired(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!document.RootElement.TryGetProperty("exp", out var exp)) return false;
            if (exp.ValueKind != JsonValueKind.Number) return false;

            return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) <= DateTimeOffset.UtcNow.AddMinutes(1);
        }
        catch
        {
            return false;
        }
    }
}
