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

    /// <summary>OAuth client_id the token was issued to.</summary>
    public string ClientId { get; set; } = "pks-cli";

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True when AccessToken is expired (with a 30-second skew).</summary>
    public bool IsExpired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30 >= ExpiresAt;
}
