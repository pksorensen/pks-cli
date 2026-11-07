using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PKS.Infrastructure.Services.Oidc;

namespace PKS.Infrastructure.Services.Agent.Setup;

/// <summary>
/// Talks ASP to a platform: discovery, sign-in, profiles, credentials.
///
/// <para>
/// This type knows nothing about Codex or about files. It turns "a URL the reader typed" into
/// documents; <see cref="Codex.CodexProfileApplier"/> turns documents into file edits. The seam is
/// the whole point of the protocol, and keeping it here is what makes a second tool binding a new
/// file rather than a rewrite.
/// </para>
/// </summary>
public interface IAgentSetupClient
{
    /// <summary>Finds the service document from a bare origin, or null with a reason.</summary>
    Task<AgentSetupDiscovery> DiscoverAsync(string url, CancellationToken ct = default);

    /// <summary>Signs in if needed and returns the profile list.</summary>
    Task<AgentSetupProfilesResult> ProfilesAsync(
        AgentSetupDiscovery discovery, string tool, bool forceLogin, Action<string> onAuthorizeUrl, CancellationToken ct = default);

    /// <summary>Mints or rotates one profile's credential. Null when the profile carries none.</summary>
    Task<AspCredential?> CredentialAsync(string credentialUrl, string accessToken, CancellationToken ct = default);
}

/// <param name="Document">Null when the platform does not speak ASP.</param>
public sealed record AgentSetupDiscovery(
    string Origin,
    AspServiceDocument? Document,
    string? ServiceUrl,
    string? Error)
{
    public bool Ok => Error is null && Document is not null;

    public static AgentSetupDiscovery Failed(string origin, string error) => new(origin, null, null, error);
}

public sealed record AgentSetupProfilesResult(
    AspProfileList? List,
    string? AccessToken,
    string? Issuer,
    string? ClientId,
    string? Error)
{
    public bool Ok => Error is null && List is not null;

    public static AgentSetupProfilesResult Failed(string error) => new(null, null, null, null, error);
}

public sealed class AgentSetupClient(
    HttpClient http,
    IOidcDiscovery discovery,
    IIssuerCredentialStore credentials,
    ILoopbackAuthCodeLogin loopbackLogin) : IAgentSetupClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The rel URI keying our entry in <c>/.well-known/agentic-platform</c>.
    ///
    /// <para>
    /// <b>An identifier, not a location.</b> It stays byte-identical on every host that implements
    /// the service — a self-hosted platform at example.com still keys its entry with this exact
    /// string, exactly as a link relation or an RDF vocabulary would. Interpolating the host into
    /// the key matches nothing anywhere but agentics.dk.
    /// </para>
    /// </summary>
    public const string RelUri = "https://agentics.dk/rel/agent-setup/v1";

    /// <summary>
    /// Where the service document lives when the platform publishes no index.
    ///
    /// <para>
    /// Discovery is an optimisation, never a requirement — the same rule <c>pks brain push</c>
    /// follows. A platform that serves the setup API but no index still works, which is what makes
    /// it safe to stand the API up first and add the index entry later.
    /// </para>
    /// </summary>
    public const string DefaultPath = "/api/v1/setup";

    public async Task<AgentSetupDiscovery> DiscoverAsync(string url, CancellationToken ct = default)
    {
        var origin = NormalizeOrigin(url);
        if (origin is null) return AgentSetupDiscovery.Failed(url, $"'{url}' is not a URL I can use.");

        // Index first, then the documented default path.
        var serviceUrl = await FromIndexAsync(origin, ct) ?? origin + DefaultPath;

        var (doc, problem) = await GetJsonDetailedAsync<AspServiceDocument>(serviceUrl, ct);
        if (doc is null)
            return AgentSetupDiscovery.Failed(origin,
                $"{origin} does not publish an agent-setup service document (looked at {serviceUrl})."
                + (problem is null ? "" : $" {problem}"));

        if (doc.V != 1)
            return AgentSetupDiscovery.Failed(origin,
                $"{origin} speaks agent-setup v{doc.V}; this pks only speaks v1.");

        return new AgentSetupDiscovery(origin, doc, serviceUrl, null);
    }

    /// <summary>Reads the platform index and pulls out our rel entry's href.</summary>
    private async Task<string?> FromIndexAsync(string origin, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync($"{origin}/.well-known/agentic-platform", ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!json.RootElement.TryGetProperty("services", out var services)) return null;
            if (!services.TryGetProperty(RelUri, out var entry)) return null;

            return entry.TryGetProperty("href", out var href) ? href.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public async Task<AgentSetupProfilesResult> ProfilesAsync(
        AgentSetupDiscovery disco, string tool, bool forceLogin, Action<string> onAuthorizeUrl, CancellationToken ct = default)
    {
        if (!disco.Ok) return AgentSetupProfilesResult.Failed(disco.Error ?? "Discovery failed.");
        var doc = disco.Document!;

        if (doc.Tools.Count > 0 && !doc.Tools.Contains(tool, StringComparer.OrdinalIgnoreCase))
            return AgentSetupProfilesResult.Failed(
                $"{disco.Origin} publishes setup for {string.Join(", ", doc.Tools)} — not {tool}.");

        var profilesUrl = doc.Profiles ?? disco.Origin + DefaultPath + "/profiles";
        var resourceUrl = disco.ServiceUrl ?? disco.Origin + DefaultPath;

        var auth = await AuthenticateAsync(resourceUrl, doc, forceLogin, onAuthorizeUrl, ct);
        if (auth.Error is not null) return AgentSetupProfilesResult.Failed(auth.Error);

        var list = await GetJsonAsync<AspProfileList>(profilesUrl, ct, auth.Token);
        if (list is null)
            return AgentSetupProfilesResult.Failed($"{profilesUrl} did not return a profile list.");

        if (string.IsNullOrWhiteSpace(list.Source?.Id))
            return AgentSetupProfilesResult.Failed(
                $"{disco.Origin} returned profiles with no source id. Every file this writes is " +
                "namespaced by it, so without one a second platform would overwrite the first.");

        return new AgentSetupProfilesResult(list, auth.Token, auth.Issuer, auth.ClientId, null);
    }

    /// <summary>
    /// A usable access token for the setup API: cached if valid, interactive otherwise.
    ///
    /// <para>
    /// The credential store is keyed by issuer, so connecting to a second platform never
    /// overwrites the first one's login. Both logins coexist and a re-run of either is silent.
    /// </para>
    /// </summary>
    private async Task<(string? Token, string? Issuer, string? ClientId, string? Error)> AuthenticateAsync(
        string resourceUrl, AspServiceDocument doc, bool forceLogin, Action<string> onAuthorizeUrl, CancellationToken ct)
    {
        var prm = await discovery.ProtectedResourceAsync(resourceUrl, ct);
        if (prm is null)
            return (null, null, null,
                $"{resourceUrl} publishes no RFC 9728 metadata, so I cannot tell where to sign in. " +
                "The platform needs /.well-known/oauth-protected-resource" +
                new Uri(resourceUrl).AbsolutePath + ".");

        // Never trust a resource's own idea of which issuer owns it without checking the resource
        // it names is the one we asked about: a document that redirected us to an attacker's
        // issuer would be handed a token for this API.
        if (!SameOrigin(prm.Resource, resourceUrl))
            return (null, null, null,
                $"{resourceUrl} published metadata for a different resource ({prm.Resource}). Refusing to sign in.");

        var issuer = prm.AuthorizationServers.FirstOrDefault();
        if (string.IsNullOrEmpty(issuer))
            return (null, null, null, $"{resourceUrl} named no authorization server.");

        // The client id comes from the platform. This single line is what lets one binary serve
        // both a Keycloak that resolves a CIMD URL and an Entra tenant that cannot.
        var clientId = prm.ClientId;
        if (string.IsNullOrEmpty(clientId))
            return (null, null, null,
                $"{issuer} did not tell me which client_id to present. A platform must publish " +
                "`client_id` in its protected-resource metadata; there is no registration I can fall back on.");

        var audience = prm.Audience ?? prm.Resource;

        if (!forceLogin)
        {
            var cached = await credentials.LoadAsync(issuer, ct);
            if (cached is not null && !cached.IsExpired && string.Equals(cached.Resource, audience, StringComparison.Ordinal))
                return (cached.AccessToken, issuer, clientId, null);
        }

        var endpoints = await discovery.EndpointsAsync(issuer, ct);
        if (endpoints is null)
            return (null, null, null, $"{issuer} published no OpenID configuration.");

        var scope = prm.ScopesSupported.Count > 0
            ? string.Join(' ', prm.ScopesSupported)
            : "openid profile email offline_access";

        var (host, ports, path) = ParseRedirects(doc.LoopbackRedirectUris);

        var result = await loopbackLogin.LoginAsync(new LoopbackLoginRequest(
            endpoints, clientId, scope, audience, ports, path, onAuthorizeUrl, host), ct);

        if (!result.Ok) return (null, null, null, result.Error ?? "Sign-in failed.");

        await credentials.SaveAsync(new IssuerCredentials
        {
            Issuer = issuer,
            Resource = audience,
            ClientId = clientId,
            AccessToken = result.AccessToken!,
            RefreshToken = result.RefreshToken,
            IdToken = result.IdToken,
            ExpiresAt = result.ExpiresAtUnix,
        }, ct);

        return (result.AccessToken, issuer, clientId, null);
    }

    /// <summary>
    /// Splits the platform's declared redirect URIs into the host, ports and path to use.
    ///
    /// <para>
    /// All three come from the platform because all three are compared by the authorization
    /// server, and the two servers we target compare them differently: Keycloak byte-for-byte
    /// against its CIMD document, Entra loosely on the port but exactly on the host. A hardcoded
    /// host works against one and fails against the other.
    /// </para>
    /// </summary>
    private static (string Host, IReadOnlyList<int> Ports, string Path) ParseRedirects(List<string>? uris)
    {
        var fallback = (LoopbackLoginRequest.DefaultHost, LoopbackLoginRequest.DefaultPorts, LoopbackLoginRequest.DefaultPath);
        if (uris is null || uris.Count == 0) return fallback;

        var parsed = uris
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null)
            .Where(u => u is not null)
            .Select(u => u!)
            .ToList();

        if (parsed.Count == 0) return fallback;

        // One host and one path; a platform mixing them is publishing something no single
        // listener can satisfy, so the first wins and the rest of its ports are still tried.
        var host = parsed[0].Host;
        var path = parsed[0].AbsolutePath.TrimEnd('/');
        var ports = parsed
            .Where(u => string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase))
            .Select(u => u.Port)
            .Distinct()
            .ToList();

        return (host, ports.Count > 0 ? ports : LoopbackLoginRequest.DefaultPorts,
            path.Length > 0 ? path : LoopbackLoginRequest.DefaultPath);
    }

    public async Task<AspCredential?> CredentialAsync(string credentialUrl, string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, credentialUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = JsonContent.Create(new { });

            using var resp = await http.SendAsync(req, ct);

            // A platform that publishes no credential for this profile is the normal case, not a
            // failure: agentics.dk's MCP servers authenticate the reader themselves over OAuth.
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
            if (!resp.IsSuccessStatusCode) return null;

            return JsonSerializer.Deserialize<AspCredential>(
                AspJsonNames.Normalize(await resp.Content.ReadAsStringAsync(ct)), Json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // ---- helpers ----

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct, string? bearer = null) where T : class
        => (await GetJsonDetailedAsync<T>(url, ct, bearer)).Value;

    /// <summary>
    /// A GET that says WHY it has no document, because the three reasons need three different
    /// actions from the reader.
    ///
    /// <para>
    /// The one worth separating is a 200 that is not JSON. Nearly every platform is a single-page
    /// app with a catch-all fallback, so an unconfigured route does not answer 404: it answers 200
    /// with <c>index.html</c>. Reported as "does not publish an agent-setup service document" that
    /// is actively misleading, because the platform may publish it perfectly well and the fallback
    /// is simply eating the request. It is also invisible from outside, which is the worst kind of
    /// discovery failure: the reader is told the feature is absent and has nothing to go on.
    /// Home's own dev server does exactly this until a proxy rule is added for /.well-known.
    /// </para>
    /// </summary>
    private async Task<(T? Value, string? Problem)> GetJsonDetailedAsync<T>(
        string url, CancellationToken ct, string? bearer = null) where T : class
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return (null, $"{url} answered {(int)resp.StatusCode}.");

            var body = await resp.Content.ReadAsStringAsync(ct);

            try
            {
                return (JsonSerializer.Deserialize<T>(AspJsonNames.Normalize(body), Json), null);
            }
            catch (JsonException)
            {
                var type = resp.Content.Headers.ContentType?.MediaType;
                var looksLikeAPage = type?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
                    || body.TrimStart().StartsWith('<');

                return (null, looksLikeAPage
                    ? $"{url} answered 200 with a web page, not JSON. A single-page-app fallback is " +
                      "almost certainly answering instead of the route, so the document is unreachable " +
                      "even if the platform serves it."
                    : $"{url} answered 200 but the body is not the document I expected.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, $"Could not reach {url}: {ex.Message}");
        }
    }

    /// <summary>
    /// Scheme and authority of whatever the reader typed. Accepts a bare host, because
    /// <c>pks codex connect agentics.dk</c> is what people will type.
    /// </summary>
    internal static string? NormalizeOrigin(string input)
    {
        var text = input.Trim().TrimEnd('/');
        if (text.Length == 0) return null;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("127.0.0.1", StringComparison.Ordinal) ? "http://" : "https://") + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;

        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static bool SameOrigin(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua)
        && Uri.TryCreate(b, UriKind.Absolute, out var ub)
        && string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ua.Authority, ub.Authority, StringComparison.OrdinalIgnoreCase);
}
