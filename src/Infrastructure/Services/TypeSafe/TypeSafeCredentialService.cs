using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.TypeSafe;

/// <summary>One entry from TypeSafe's <c>GET /v1/models</c>.</summary>
public sealed record TypeSafeModel(string Name, string? Description, string? ReleaseDate);

/// <summary>
/// What the evaluation endpoint answered, verbatim. The status is TypeSafe's own except for one
/// translation done in <see cref="TypeSafeCredentialService.EvaluateAsync"/>: an upstream 401 means
/// the <em>host's</em> key is bad, and a job that sees 401 would conclude its own job token was
/// refused. It is reported as 502 with a body that names the real cause.
/// </summary>
public sealed record TypeSafeEvaluation(int StatusCode, string Body);

public interface ITypeSafeCredentialService
{
    /// <summary>
    /// Checks a key against TypeSafe without storing it, by listing the models the key may call.
    /// Null when TypeSafe rejects it or cannot be reached, so <c>pks typesafe init</c> fails at
    /// the prompt rather than in the middle of an assembly-line job.
    /// </summary>
    Task<IReadOnlyList<TypeSafeModel>?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default);

    /// <summary>Whether a key is stored on this host.</summary>
    Task<bool> HasApiKeyAsync();

    /// <summary>
    /// The stored key in plaintext, for the one caller that must hand it across the credential
    /// socket (<c>GET /typesafe/token</c>). Everything else goes through <see cref="EvaluateAsync"/>.
    /// </summary>
    Task<string?> RevealApiKeyAsync();

    /// <summary>Repositories (owner/name) whose jobs may spend the key. Case-insensitive.</summary>
    Task<IReadOnlyList<string>> GetAllowedRepositoriesAsync();

    Task SetAllowedRepositoriesAsync(IEnumerable<string> repositories);

    /// <summary>Whether jobs for <paramref name="owner"/>/<paramref name="repo"/> may spend the key.</summary>
    Task<bool> IsRepoAllowedAsync(string owner, string repo);

    /// <summary>The model sent when a request names none. Defaults to <c>jev-latest</c>.</summary>
    Task<string> GetDefaultModelAsync();

    Task SetDefaultModelAsync(string model);

    /// <summary>
    /// Forwards a System One request to TypeSafe with the stored key and returns the answer.
    /// <paramref name="requestJson"/> is the request body as the caller wrote it; a missing
    /// <c>model</c> is filled in from <see cref="GetDefaultModelAsync"/>. The key never appears
    /// in the result.
    /// </summary>
    Task<TypeSafeEvaluation> EvaluateAsync(string requestJson, CancellationToken ct = default);
}

/// <summary>
/// The TypeSafe (Jev) API key on this host: stored encrypted, validated against TypeSafe, and spent
/// only on behalf of jobs whose repository was allowed with <c>pks typesafe allow owner/repo</c>.
///
/// The preferred way for a job to use it is <c>POST /typesafe/systemone</c> on the runner's
/// credential socket, which proxies the call: the key stays on the host and the container only
/// ever sees answers. <c>GET /typesafe/token</c> exists for a job that must run an SDK, and is the
/// same trade Expo makes — the raw credential crosses the socket.
/// </summary>
public sealed class TypeSafeCredentialService : ITypeSafeCredentialService
{
    /// <summary>
    /// Storage key. Ends in <c>api_key</c>, so <see cref="SecretKeys"/> classifies it as credential
    /// material and it can only ever live in the encrypted store — never in settings.json.
    /// </summary>
    public const string ApiKeyKey = "typesafe.api_key";

    /// <summary>Non-secret: comma-separated owner/repo list in settings.json.</summary>
    public const string AllowedRepositoriesKey = "typesafe.allowed_repos";

    /// <summary>Non-secret: the model used when a request carries none.</summary>
    public const string DefaultModelKey = "typesafe.default_model";

    public const string DefaultModel = "jev-latest";

    /// <summary>API root. <c>TYPESAFE_BASE_URL</c> overrides it, same name the SDKs honour.</summary>
    public const string DefaultBaseUrl = "https://api.typesafe.ai";

    private readonly ISecretResolver _secrets;
    private readonly ISecretStore _secretStore;
    private readonly IConfigurationService _configuration;
    private readonly IHttpClientFactory? _httpClientFactory;

    public TypeSafeCredentialService(
        ISecretResolver secrets,
        ISecretStore secretStore,
        IConfigurationService configuration,
        IHttpClientFactory? httpClientFactory = null)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClientFactory = httpClientFactory;
    }

    public static string BaseUrl
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TYPESAFE_BASE_URL");
            return string.IsNullOrWhiteSpace(env) ? DefaultBaseUrl : env.TrimEnd('/');
        }
    }

    public async Task<IReadOnlyList<TypeSafeModel>?> ValidateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        using var http = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception)
        {
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) return null;
            return ParseModels(await response.Content.ReadAsStringAsync(ct));
        }
    }

    internal static IReadOnlyList<TypeSafeModel>? ParseModels(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // The documented shape is { "models": [ { name, description, release_date } ] };
            // a bare array is accepted too, so a shape change does not turn a valid key into
            // "TypeSafe rejected this key".
            var list = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array
                    ? m
                    : root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
                        ? d
                        : default;
            if (list.ValueKind != JsonValueKind.Array) return Array.Empty<TypeSafeModel>();

            var result = new List<TypeSafeModel>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    result.Add(new TypeSafeModel(item.GetString() ?? "", null, null));
                    continue;
                }
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = item.TryGetProperty("name", out var n) ? n.GetString()
                    : item.TryGetProperty("id", out var id) ? id.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;
                var description = item.TryGetProperty("description", out var de) && de.ValueKind == JsonValueKind.String ? de.GetString() : null;
                var released = item.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String ? rd.GetString() : null;
                result.Add(new TypeSafeModel(name, description, released));
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<bool> HasApiKeyAsync() => _secretStore.HasAsync(ApiKeyKey);

    public Task<string?> RevealApiKeyAsync() => _secrets.RevealAsync(ApiKeyKey);

    public async Task<IReadOnlyList<string>> GetAllowedRepositoriesAsync()
    {
        var raw = await _configuration.GetAsync(AllowedRepositoriesKey);
        return ParseRepositoryList(raw);
    }

    internal static IReadOnlyList<string> ParseRepositoryList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw
            .Split(new[] { ',', ';', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Count(c => c == '/') == 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task SetAllowedRepositoriesAsync(IEnumerable<string> repositories)
    {
        var value = string.Join(",", repositories
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        return _configuration.SetAsync(AllowedRepositoriesKey, value, global: true);
    }

    public async Task<bool> IsRepoAllowedAsync(string owner, string repo)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)) return false;
        var wanted = $"{owner}/{repo}";
        var allowed = await GetAllowedRepositoriesAsync();
        return allowed.Any(r => string.Equals(r, wanted, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> GetDefaultModelAsync()
    {
        var stored = await _configuration.GetAsync(DefaultModelKey);
        return string.IsNullOrWhiteSpace(stored) ? DefaultModel : stored.Trim();
    }

    public Task SetDefaultModelAsync(string model) =>
        _configuration.SetAsync(DefaultModelKey, model.Trim(), global: true);

    public async Task<TypeSafeEvaluation> EvaluateAsync(string requestJson, CancellationToken ct = default)
    {
        var apiKey = await _secrets.RevealAsync(ApiKeyKey);
        if (string.IsNullOrEmpty(apiKey))
            return new TypeSafeEvaluation(404, Error("No TypeSafe API key stored — run 'pks typesafe init' on the runner host"));

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(requestJson);
        }
        catch (JsonException ex)
        {
            return new TypeSafeEvaluation(400, Error($"request body is not JSON: {ex.Message}"));
        }
        if (node is not JsonObject obj)
            return new TypeSafeEvaluation(400, Error("request body must be a JSON object with state and questions"));

        if (obj["model"] is null || string.IsNullOrWhiteSpace(obj["model"]?.GetValue<string>()))
            obj["model"] = await GetDefaultModelAsync();

        using var http = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/systemone")
        {
            Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            return new TypeSafeEvaluation(502, Error($"TypeSafe unreachable: {ex.Message}"));
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new TypeSafeEvaluation(502, Error("TypeSafe rejected the host's API key — the job token was fine; run 'pks typesafe init --force' on the runner host"));
            return new TypeSafeEvaluation((int)response.StatusCode, body);
        }
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message });

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory?.CreateClient("typesafe") ?? new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }
}
