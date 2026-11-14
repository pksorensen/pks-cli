namespace PKS.Infrastructure.Services.Oidc;

/// The two discovery documents that stand between "a URL the user typed" and
/// "an access token": RFC 9728 protected resource metadata, and RFC 8414 /
/// OpenID Connect authorization server metadata.
///
/// Neither is optional in the spec and both are optional here: a receiver that
/// serves neither still works with a `bkt_` upload token, and refusing to talk
/// to it would make the standards a barrier to entry rather than a convenience.
public interface IOidcDiscovery
{
    /// Reads `<resourceUrl>`'s PRM. Returns null when the resource does not
    /// publish one — which is normal for a receiver that only accepts tokens.
    Task<ProtectedResourceMetadata?> ProtectedResourceAsync(string resourceUrl, CancellationToken ct = default);

    /// Reads the PRM document at the URL that NAMED it, instead of deriving the
    /// well-known path from a resource URL.
    ///
    /// ASP's service document carries this URL in its `authorization` field, and the
    /// spec says a client reads the document *named by* it. Deriving gives the same
    /// answer only while the service document and the protected resource share a
    /// path, so a platform that serves its document from `/.well-known/agent-setup`
    /// and its API from `/api/v1/setup` cannot be reached by derivation at all.
    Task<ProtectedResourceMetadata?> ProtectedResourceAtAsync(string metadataUrl, CancellationToken ct = default);

    /// Reads `<issuer>/.well-known/openid-configuration`, falling back to
    /// `oauth-authorization-server`. Returns null when neither answers.
    Task<OidcEndpoints?> EndpointsAsync(string issuer, CancellationToken ct = default);
}

/// <param name="Resource">
/// The audience the authorization server should bind, and the value a client
/// must compare against before presenting a token anywhere. Trusting this blind
/// would be a redirect primitive, so callers check it is same-origin with the
/// resource they asked about.
/// </param>
/// <param name="ClientId">
/// House extension, and the field that lets one binary serve two issuers.
///
/// RFC 9728 has no field for it and RFC 7591 dynamic registration is implemented
/// by none of the providers we reach, so the resource tells us which client id to
/// present. Keycloak takes a CIMD URL and resolves it by fetching it, so no
/// registration exists anywhere; Entra does not implement CIMD and hands out a
/// pre-registered GUID instead. A client that holds its own constant works
/// against exactly one of the two.
///
/// Null when the resource does not publish one, in which case the caller falls
/// back to whatever it was built with.
/// </param>
/// <param name="Audience">
/// House extension: the RFC 8707 resource indicator to request, when it is not
/// the resource URL itself.
/// </param>
public sealed record ProtectedResourceMetadata(
    string Resource,
    IReadOnlyList<string> AuthorizationServers,
    IReadOnlyList<string> ScopesSupported,
    string? ClientId = null,
    string? Audience = null);

public sealed record OidcEndpoints(
    string Issuer,
    string TokenEndpoint,
    string? DeviceAuthorizationEndpoint,
    string? AuthorizationEndpoint)
{
    /// A provider without the device endpoint cannot log in a headless CLI at
    /// all, and saying so early beats a 400 from a URL we invented.
    public bool SupportsDeviceGrant => !string.IsNullOrEmpty(DeviceAuthorizationEndpoint);
}
