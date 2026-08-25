using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Runner;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Expo;

/// <summary>Who an Expo access token authenticates as, as reported by Expo's <c>meActor</c> query.</summary>
public sealed record ExpoActor(string Id, string Type, string Name)
{
    /// <summary>True for a robot user — the account kind that should be used in CI.</summary>
    public bool IsRobot => string.Equals(Type, "Robot", StringComparison.OrdinalIgnoreCase);
}

public interface IExpoCredentialService
{
    /// <summary>
    /// Checks a token against Expo without storing it. Returns null when Expo rejects it, so
    /// <c>pks expo init</c> fails at the prompt rather than six weeks later in a release job.
    /// </summary>
    Task<ExpoActor?> ValidateTokenAsync(string token, CancellationToken ct = default);

    /// <summary>Whether a token is stored on this host.</summary>
    Task<bool> HasTokenAsync();

    /// <summary>
    /// Resolves the stored token against Expo and reports the account. Lives here rather than in
    /// the command because it needs the plaintext, which <c>src/Commands/</c> may not touch.
    /// </summary>
    Task<ExpoActor?> DescribeStoredActorAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether jobs for <paramref name="owner"/>/<paramref name="repo"/> may spend the stored token.
    /// Requires an enabled runner registration carrying <c>--expo</c>.
    /// </summary>
    Task<bool> IsRepoAllowedAsync(string owner, string repo);

    /// <summary>
    /// The stored token in plaintext, for the one caller that must hand it across the credential
    /// socket. Everything else uses <see cref="HasTokenAsync"/>.
    /// </summary>
    Task<string?> RevealTokenAsync();
}

/// <summary>
/// The Expo access token on this host: stored encrypted, validated against Expo, and released only
/// to jobs whose repository was registered with <c>--expo</c>.
///
/// The token is a robot user's, not a person's, and it never leaves the box except through
/// <c>GET /expo/token</c> on the runner's credential socket. That is the whole point of the type:
/// before it existed, CI authenticated to Expo by running <c>npx expo login -u … -p …</c> with an
/// account password held in GitHub secrets.
/// </summary>
public sealed class ExpoCredentialService : IExpoCredentialService
{
    /// <summary>
    /// Storage key. Ends in <c>token</c>, so <see cref="SecretKeys"/> classifies it as credential
    /// material and it can only ever live in the encrypted store — never in settings.json.
    /// </summary>
    public const string TokenKey = "expo.access.token";

    private const string GraphQlEndpoint = "https://api.expo.dev/graphql";

    // __typename discriminates User from Robot; both carry an id, and the display name differs by
    // kind, which is why the query asks for both inline fragments.
    private const string MeActorQuery =
        "query { meActor { __typename id ... on User { username } ... on Robot { firstName } } }";

    private readonly ISecretResolver _secrets;
    private readonly IRunnerConfigurationService _runners;
    private readonly IHttpClientFactory? _httpClientFactory;

    public ExpoCredentialService(
        ISecretResolver secrets,
        IRunnerConfigurationService runners,
        IHttpClientFactory? httpClientFactory = null)
    {
        // Required, not defaulted: a service that constructs its own SecretStore silently reads a
        // different store than the rest of the process under test, which is how a live key leaked once.
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _runners = runners ?? throw new ArgumentNullException(nameof(runners));
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ExpoActor?> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        using var http = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query = MeActorQuery }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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

            var body = await response.Content.ReadAsStringAsync(ct);
            return ParseActor(body);
        }
    }

    internal static ExpoActor? ParseActor(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            // GraphQL answers 200 with an errors array for an invalid token; treat that as a rejection.
            if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                return null;

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("meActor", out var actor) ||
                actor.ValueKind != JsonValueKind.Object)
                return null;

            var id = actor.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var type = actor.TryGetProperty("__typename", out var tEl) ? tEl.GetString() ?? "" : "";
            var name =
                actor.TryGetProperty("username", out var uEl) && uEl.ValueKind == JsonValueKind.String
                    ? uEl.GetString()!
                    : actor.TryGetProperty("firstName", out var fEl) && fEl.ValueKind == JsonValueKind.String
                        ? fEl.GetString()!
                        : "";

            return string.IsNullOrEmpty(id) ? null : new ExpoActor(id, type, name);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<bool> HasTokenAsync()
        => !string.IsNullOrEmpty(await _secrets.RevealAsync(TokenKey));

    public async Task<ExpoActor?> DescribeStoredActorAsync(CancellationToken ct = default)
    {
        var token = await _secrets.RevealAsync(TokenKey);
        return string.IsNullOrEmpty(token) ? null : await ValidateTokenAsync(token, ct);
    }

    public async Task<bool> IsRepoAllowedAsync(string owner, string repo)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
            return false;

        var registrations = await _runners.ListRegistrationsAsync();
        return registrations.Any(r =>
            r.Enabled &&
            r.ExpoEnabled &&
            string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Repository, repo, StringComparison.OrdinalIgnoreCase));
    }

    public Task<string?> RevealTokenAsync() => _secrets.RevealAsync(TokenKey);

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory?.CreateClient("expo") ?? new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}
