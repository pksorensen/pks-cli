namespace PKS.Infrastructure.Services.Oidc;

/// Credentials for identity providers other than the one `pks agentics init`
/// signed into.
///
/// `~/.pks-cli/agentics-auth.json` holds exactly one login, which was right
/// while agentics.dk was the only thing to log in to. `pks brain push --server
/// https://brain.example.com` breaks that assumption: the user now has two
/// identities at two providers, and neither should overwrite the other. So this
/// store is keyed by issuer, and the single-login file stays the home of the
/// agentics.dk credential rather than being migrated — a working install should
/// not need a data migration to gain a feature it does not use.
///
/// Files land at `~/.pks-cli/auth/issuers/<sha256(issuer)[0..16]>.json`, mode
/// 0600. The hash is a filename, not a secret: issuer URLs contain slashes and
/// colons, and the file records the issuer in cleartext inside.
public interface IIssuerCredentialStore
{
    Task<IssuerCredentials?> LoadAsync(string issuer, CancellationToken ct = default);
    Task SaveAsync(IssuerCredentials credentials, CancellationToken ct = default);
    Task<IReadOnlyList<IssuerCredentials>> ListAsync(CancellationToken ct = default);
    Task<bool> DeleteAsync(string issuer, CancellationToken ct = default);
}

public sealed class IssuerCredentials
{
    /// The authorization server, exactly as the PRM named it. This is the key.
    public string Issuer { get; set; } = "";

    /// The API this credential was obtained for — the RFC 8707 resource
    /// indicator. Checked before the token is presented anywhere: an issuer can
    /// serve several resources, and a brain token is not an MCP token.
    public string Resource { get; set; } = "";

    public string ClientId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string? IdToken { get; set; }

    /// Unix epoch seconds.
    public long ExpiresAt { get; set; }

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    /// 30 s of skew, matching AgenticsAuthCredentials — a token that expires
    /// mid-flight costs a retry, and the retry is the expensive part.
    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30 >= ExpiresAt;
}
