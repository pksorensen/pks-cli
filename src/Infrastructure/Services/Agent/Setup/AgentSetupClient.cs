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
    /// string, exactly as a link relation or an RDF vocabulary would. The identifier lives in the
    /// specification's namespace rather than any platform's, so interpolating the host into the
    /// key matches nothing anywhere.
    /// </para>
    /// </summary>
    public const string RelUri = "https://agentsetupprotocol.com/rel/agent-setup/v1";

    /// <summary>
    /// The key agentics.dk published before the specification had a home of its own.
    ///
    /// <para>
    /// Tried after <see cref="RelUri"/>, because the rel URI is compared byte for byte and a
    /// platform that has not redeployed still advertises the old string. Reading both costs one
    /// dictionary lookup; dropping it strands every platform on the previous release.
    /// </para>
    /// </summary>
    public const string RelUriLegacy = "https://agentics.dk/rel/agent-setup/v1";

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

    /// <summary>
    /// The path an RFC 8615 well-known address puts the service document at, and the first one
    /// tried when a platform publishes no index.
    ///
    /// <para>
    /// It matters that this is guessable: the fallback chain has to end somewhere a client can
    /// try on a host it knows nothing about, and the pre-move <see cref="DefaultPath"/> is not
    /// that — it is one platform's API layout, which a second implementer has no reason to copy.
    /// A platform may serve either or both; this platform serves both, and a new one needs only
    /// the well-known path.
    /// </para>
    /// </summary>
    public const string WellKnownPath = "/.well-known/agent-setup";

    public async Task<AgentSetupDiscovery> DiscoverAsync(string url, CancellationToken ct = default)
    {
        var origin = NormalizeOrigin(url);
        if (origin is null) return AgentSetupDiscovery.Failed(url, $"'{url}' is not a URL I can use.");

        // The index is authoritative when there is one. Without it, the two documented paths in
        // order: the well-known address any client can guess, then the pre-move path, which a
        // platform standing still on an older layout may be the only thing serving.
        var fromIndex = await FromIndexAsync(origin, ct);
        var candidates = fromIndex is not null
            ? new[] { fromIndex }
            : new[] { origin + WellKnownPath, origin + DefaultPath };

        AspServiceDocument? doc = null;
        string? problem = null;
        var serviceUrl = candidates[0];

        foreach (var candidate in candidates)
        {
            (doc, problem) = await GetJsonDetailedAsync<AspServiceDocument>(candidate, ct);
            serviceUrl = candidate;
            if (doc is not null) break;
        }

        if (doc is null)
            return AgentSetupDiscovery.Failed(origin,
                $"{origin} does not publish an agent-setup service document "
                + $"(looked at {string.Join(" and ", candidates)})."
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
            // Current key first, then the pre-agentsetupprotocol.com one. A platform
            // mid-rollout serves both and either answer is the same document.
            if (!services.TryGetProperty(RelUri, out var entry)
                && !services.TryGetProperty(RelUriLegacy, out entry)) return null;

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

        var auth = await AuthenticateAsync(disco.Origin, profilesUrl, doc, forceLogin, onAuthorizeUrl, ct);
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
        string origin, string profilesUrl, AspServiceDocument doc, bool forceLogin,
        Action<string> onAuthorizeUrl, CancellationToken ct)
    {
        var (metadataUrl, metadataError) = ResourceMetadataUrl(origin, profilesUrl, doc);
        if (metadataError is not null) return (null, null, null, metadataError);

        var prm = await discovery.ProtectedResourceAtAsync(metadataUrl, ct);
        if (prm is null)
            return (null, null, null,
                $"{metadataUrl} is not an RFC 9728 metadata document, so I cannot tell where to sign in.");

        // Never trust a resource's own idea of which issuer owns it without checking that the
        // resource it names is the one the token is for: a document that redirected us to an
        // attacker's issuer would be handed a token for this API. Compared against the profiles
        // URL, because that is where the token actually goes.
        if (!SameOrigin(prm.Resource, profilesUrl))
            return (null, null, null,
                $"{metadataUrl} published metadata for a different resource ({prm.Resource}). Refusing to sign in.");

        var issuer = prm.AuthorizationServers.FirstOrDefault();
        if (string.IsNullOrEmpty(issuer))
            return (null, null, null, $"{metadataUrl} named no authorization server.");

        // The client id comes from the platform. This single line is what lets one binary serve
        // both a Keycloak that resolves a CIMD URL and an Entra tenant that cannot.
        var clientId = prm.ClientId;
        if (string.IsNullOrEmpty(clientId))
            return (null, null, null,
                $"{issuer} did not tell me which client_id to present. A platform must publish " +
                "`client_id` in its protected-resource metadata; there is no registration I can fall back on.");

        var audience = prm.Audience ?? prm.Resource;

        // RFC 8707 §2: a resource indicator is an absolute URI. A platform that publishes a bare
        // name in the house `audience` field is describing a client or an API name, not a resource,
        // and sending it anyway is not a cosmetic error: Keycloak answers `invalid_target` and
        // redirects straight back to the loopback, so the reader's browser never shows a login page
        // and the failure arrives as a dead callback rather than a message. Measured 14/11-2026
        // against a platform publishing `"audience": "pks-cli"`.
        //
        // Omitted rather than corrected, because the fallback is already right: `prm.Resource` is a
        // URI by RFC 9728, and an authorization server that binds the resource it was asked about
        // needs no parameter to do it. The value still keys the credential cache, so a cached token
        // keeps matching whatever the platform calls its audience.
        var resourceIndicator = Uri.IsWellFormedUriString(audience, UriKind.Absolute) ? audience : null;

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
            endpoints, clientId, scope, resourceIndicator, ports, path, onAuthorizeUrl, host), ct);

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

    /// <summary>
    /// Where this platform's RFC 9728 metadata lives, and null-error when it can be reached.
    /// </summary>
    /// <remarks>
    /// The service document's own <c>authorization</c> field, because the spec says a client
    /// reads the metadata document <b>named by</b> it, and only the platform knows which path
    /// its API is protected at.
    ///
    /// <para>
    /// This used to derive the well-known path from wherever the service document was served,
    /// which is the same answer only while the document and the protected resource share a path.
    /// The moment a platform moved its document to <c>/.well-known/agent-setup</c> and left its
    /// API on <c>/api/v1/setup</c>, every connect asked for
    /// <c>/.well-known/oauth-protected-resource/.well-known/agent-setup</c> and told the reader
    /// that a correctly configured platform published no metadata. Measured against a live
    /// platform 14/11-2026, where the document named the right URL all along.
    /// </para>
    ///
    /// <para>
    /// A cross-origin <c>authorization</c> is refused rather than fetched. RFC 9728 puts a
    /// resource's metadata on the resource's own host, so a document naming another origin is
    /// either broken or pointing sign-in at an issuer that does not own this API — and a reader
    /// who completed that flow would be typing their password into whatever that issuer served.
    /// </para>
    /// </remarks>
    private static (string Url, string? Error) ResourceMetadataUrl(
        string origin, string profilesUrl, AspServiceDocument doc)
    {
        if (!string.IsNullOrWhiteSpace(doc.Authorization))
        {
            if (!SameOrigin(doc.Authorization, origin))
                return ("", $"{origin} names its authorization metadata at {doc.Authorization}, " +
                            "which is a different origin. Refusing to sign in there.");

            return (doc.Authorization, null);
        }

        // No `authorization` field. Derive it from the API the document DID publish — the
        // profiles URL minus its last segment — and never from where the document was served.
        var resource = ParentOf(profilesUrl) ?? origin + DefaultPath;

        return Uri.TryCreate(resource, UriKind.Absolute, out var uri)
            ? ($"{uri.Scheme}://{uri.Authority}/.well-known/oauth-protected-resource{uri.AbsolutePath.TrimEnd('/')}", null)
            : ("", $"{origin} published neither authorization metadata nor a usable profiles URL.");
    }

    /// <summary>The profiles URL minus its last segment: the resource a token is minted for.</summary>
    private static string? ParentOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var path = uri.AbsolutePath.TrimEnd('/');
        var cut = path.LastIndexOf('/');

        return cut <= 0 ? null : $"{uri.Scheme}://{uri.Authority}{path[..cut]}";
    }

    private static bool SameOrigin(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua)
        && Uri.TryCreate(b, UriKind.Absolute, out var ub)
        && string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ua.Authority, ub.Authority, StringComparison.OrdinalIgnoreCase);
}
