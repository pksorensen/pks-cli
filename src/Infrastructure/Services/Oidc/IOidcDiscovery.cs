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
public sealed record ProtectedResourceMetadata(
    string Resource,
    IReadOnlyList<string> AuthorizationServers,
    IReadOnlyList<string> ScopesSupported);

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
