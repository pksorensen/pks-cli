namespace PKS.Infrastructure.Services.Oidc;

/// RFC 8252 native-app login: authorization code + PKCE (S256) with a loopback
/// redirect, against any issuer.
///
/// Why this exists next to <see cref="IDeviceCodeLogin"/>, which is otherwise
/// the nicer flow for a CLI: **Keycloak's CIMD support only covers the
/// authorization endpoint.** Presenting a client-id-URL at
/// `/protocol/openid-connect/auth/device` answers `invalid_client` /
/// `client_not_found` — the client policy that resolves the metadata document
/// never runs there (verified against Keycloak 26.6 with `KC_FEATURES=cimd`,
/// 2026-08-04). So a receiver whose issuer knows us only through our CIMD
/// document can be logged into through the browser and no other way.
///
/// The consequence for headless machines is real and not worked around here: a
/// loopback redirect only lands if the browser runs on the same host as the CLI
/// (or the editor forwards the port). Servers and CI keep using
/// `PKS_BRAIN_TOKEN` or a pre-registered client_id with the device grant.
public interface ILoopbackAuthCodeLogin
{
    Task<OidcLoginResult> LoginAsync(LoopbackLoginRequest request, CancellationToken ct = default);
}

/// <param name="Endpoints">Discovered, never guessed.</param>
/// <param name="ClientId">Usually a CIMD URL — that is the case this flow is for.</param>
/// <param name="Scope">Space-separated. `offline_access` is what makes the daily daemon possible.</param>
/// <param name="Resource">RFC 8707 resource indicator: the API the token is for.</param>
/// <param name="RedirectPorts">
/// The loopback ports to try, in order. **Not** an ephemeral port: Keycloak's
/// CIMD executor compares `redirect_uri` byte-for-byte against the
/// `redirect_uris` in the metadata document, so the client and the document have
/// to agree on the exact URL — a random port fails with "does not exactly match".
/// A short list rather than one port, because something else may hold it.
/// </param>
/// <param name="RedirectPath">Path component of the redirect. Must match the document too.</param>
/// <param name="OnAuthorizeUrl">
/// Shows the URL to the human. The service tries to open a browser as well, but
/// printing is what makes it work when it cannot.
/// </param>
public sealed record LoopbackLoginRequest(
    OidcEndpoints Endpoints,
    string ClientId,
    string Scope,
    string? Resource,
    IReadOnlyList<int> RedirectPorts,
    string RedirectPath,
    Action<string> OnAuthorizeUrl)
{
    /// What agentics.dk publishes in its CIMD document. A receiver that publishes
    /// different ones is not discoverable from here — the document is fetched by
    /// the *authorization server*, not by us — so these are the convention every
    /// deployment of ours follows.
    public static readonly IReadOnlyList<int> DefaultPorts = [51789, 51790, 51791];

    public const string DefaultPath = "/callback";
}
