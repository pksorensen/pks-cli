using PKS.Infrastructure.Services.Oidc;

namespace PKS.Infrastructure.Services.Agentics;

/// <summary>
/// Signing in to an agentics server with the device grant, and saving the result.
///
/// Shared rather than owned by <c>pks agentics init</c> because the runner signs in
/// too: a fresh box that has never run <c>init</c> should not have to, and two copies
/// of "discover the issuer, run the grant, write the credential" would drift the first
/// time either grew a rule.
/// </summary>
public static class AgenticsSignIn
{
    /// <summary>The scope every CLI login asks for. <c>offline_access</c> is what makes
    /// the credential refreshable, which is the difference between signing in once and
    /// signing in every day.</summary>
    public const string Scope = "openid offline_access";

    public const string DefaultRealm = "agentics";
    public const string DefaultClientId = "agentics-cli";

    /// <summary>
    /// Where the realm might live, best guess first.
    ///
    /// An explicit URL is the only answer; otherwise we probe, because there is no one
    /// convention. Ours is <c>login.agentics.dk</c> — <c>keycloak.</c> never resolved,
    /// which surfaced as a TLS handshake error rather than a 404 and read like a broken
    /// server instead of a wrong hostname. It stays in the list for installs that do use
    /// it, and the bare host is last for a server that fronts its own identity provider.
    /// </summary>
    public static string[] IssuerCandidates(string? explicitUrl, string serverHost, string realm)
    {
        static bool IsUrl(string value)
            => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        string Realm(string host) => $"{host.TrimEnd('/')}/realms/{realm}";

        if (!string.IsNullOrWhiteSpace(explicitUrl)) return [Realm(explicitUrl)];
        if (IsUrl(serverHost)) return [Realm(serverHost)];

        return
        [
            Realm($"https://login.{serverHost}"),
            Realm($"https://keycloak.{serverHost}"),
            Realm($"https://{serverHost}"),
        ];
    }

    /// <summary>
    /// Asks each candidate where its endpoints are, rather than assuming Keycloak's
    /// layout. Discovery doubles as host resolution: every Keycloak serves it, so the
    /// first candidate that answers is the identity provider and the others are
    /// subdomains that do not exist.
    /// </summary>
    public static async Task<OidcEndpoints> DiscoverAsync(
        IOidcDiscovery discovery,
        IReadOnlyList<string> candidates,
        CancellationToken ct = default)
    {
        foreach (var candidate in candidates)
        {
            var endpoints = await discovery.EndpointsAsync(candidate, ct);
            if (endpoints is not null) return endpoints;
        }

        // Nothing answered. Guess the paths on the first candidate so the error names
        // the host we would have used, not the last one we tried.
        return KeycloakConvention(candidates[0]);
    }

    /// <summary>
    /// Runs the device grant against whichever candidate answers, and persists the
    /// credential when it succeeds. Returns the login result either way — the caller
    /// decides how loudly to fail.
    /// </summary>
    /// <param name="server">The agentics host, e.g. <c>agentics.dk</c>; not a URL.</param>
    /// <param name="keycloakUrl">An explicit identity provider, when the operator named one.</param>
    /// <param name="onPrompt">Called once with the URL and code to put in front of the human.</param>
    public static async Task<OidcLoginResult> SignInAsync(
        IOidcDiscovery discovery,
        IDeviceCodeLogin deviceLogin,
        IAgenticsAuthConfigurationService authConfig,
        string server,
        string? keycloakUrl,
        string realm,
        string clientId,
        Action<DeviceCodePrompt> onPrompt,
        CancellationToken ct = default)
    {
        var endpoints = await DiscoverAsync(discovery, IssuerCandidates(keycloakUrl, server, realm), ct);

        var result = await deviceLogin.LoginAsync(new DeviceLoginRequest(
            endpoints,
            clientId,
            Scope,
            // No `resource`: this credential is the CLI's general-purpose login, not a
            // token bound to one API.
            null,
            onPrompt), ct);

        if (!result.Ok) return result;

        await authConfig.SaveAsync(new AgenticsAuthCredentials
        {
            Server = server,
            // Recorded so refresh does not have to re-derive it — and it is the
            // discovered issuer, not a guessed host. Without this, refresh falls back to
            // the login.<server> convention, which holds for agentics.dk and nowhere
            // else; a self-hosted instance that passed --keycloak would silently lose
            // its refresh path.
            Issuer = endpoints.Issuer,
            Realm = realm,
            ClientId = clientId,
            AccessToken = result.AccessToken!,
            RefreshToken = result.RefreshToken,
            IdToken = result.IdToken,
            ExpiresAt = result.ExpiresAtUnix,
        });

        return result;
    }

    /// <summary>
    /// What Keycloak's paths are, for an issuer that serves no discovery document. Our
    /// own dev realm has answered <c>/.well-known/openid-configuration</c> all along, so
    /// this is the fallback for someone else's install.
    /// </summary>
    private static OidcEndpoints KeycloakConvention(string issuer) => new(
        issuer,
        $"{issuer}/protocol/openid-connect/token",
        $"{issuer}/protocol/openid-connect/auth/device",
        $"{issuer}/protocol/openid-connect/auth");
}
