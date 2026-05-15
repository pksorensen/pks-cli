namespace PKS.Infrastructure.Services.GitProxy;

/// <summary>
/// Pluggable provider of the Bearer token to inject when forwarding requests
/// to a specific upstream. Implementations span:
///  - Static — the operator already has a long-lived JWT (marketplace flow).
///  - Refresh — the operator has refresh-token credentials and exchanges them
///    on each request (ADO flow with MSAL refresh against login.microsoftonline).
///  - OAuth client_credentials — auto-mint a service token on first miss and
///    cache it until near-expiry (future marketplace flow).
///
/// Each upstream registers its own source so the proxy doesn't have to know
/// how the credential was obtained.
/// </summary>
public interface IGitProxyTokenSource
{
    /// <summary>
    /// Returns a Bearer token for the upstream this source is bound to, or
    /// null when no token is currently obtainable. The proxy responds 502 in
    /// the null case so the caller sees a clear "no creds" failure rather
    /// than a misleading 401 from the upstream.
    /// </summary>
    Task<string?> GetTokenAsync(CancellationToken ct = default);
}
