using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Models;

/// <summary>Moonshot API credentials persisted for process-local tool launches.</summary>
public sealed class MoonshotStoredCredentials
{
    public SecretValue ApiKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
