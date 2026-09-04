namespace PKS.Infrastructure.Services.Agentics;

/// <summary>
/// Persists the user's Keycloak access/refresh tokens issued by `pks agentics init`.
/// Stored at ~/.pks-cli/agentics-auth.json with file permissions restricted to the owner.
/// </summary>
public interface IAgenticsAuthConfigurationService
{
    Task<AgenticsAuthCredentials?> LoadAsync();
    Task SaveAsync(AgenticsAuthCredentials credentials);
    Task ClearAsync();
}

public class AgenticsAuthCredentials
{
    /// <summary>Server host the credentials were issued for, e.g. "agentics.dk".</summary>
    public string Server { get; set; } = "agentics.dk";

    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string? IdToken { get; set; }

    /// <summary>Unix epoch seconds when AccessToken expires.</summary>
    public long ExpiresAt { get; set; }

    /// <summary>Realm the token was issued by, e.g. "agentics".</summary>
    public string Realm { get; set; } = "agentics";

    /// <summary>
    /// Keycloak realm base URL the token came from, e.g.
    /// "https://login.agentics.dk/realms/agentics". Absent on credentials
    /// written before this field existed — read it through
    /// <see cref="IssuerOrConvention"/>, never directly.
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// The issuer to talk to, falling back to the subdomain convention that
    /// holds for agentics.dk. Self-hosted and local instances store an explicit
    /// <see cref="Issuer"/> because the convention does not describe them.
    ///
    /// `login.`, not `keycloak.` — the latter has never resolved for
    /// agentics.dk and a credential that fell back to it lost its refresh path
    /// with a TLS error rather than an HTTP one.
    /// </summary>
    public string IssuerOrConvention()
        => string.IsNullOrEmpty(Issuer)
            ? $"https://login.{Server.TrimEnd('/')}/realms/{Realm}"
            : Issuer.TrimEnd('/');

    /// <summary>OAuth client_id the token was issued to.</summary>
    public string ClientId { get; set; } = "pks-cli";

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True when AccessToken is expired (with a 30-second skew).</summary>
    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30 >= ExpiresAt;
}
