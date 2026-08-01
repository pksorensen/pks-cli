namespace PKS.Infrastructure.Services.Models;

/// <summary>Moonshot API credentials persisted for process-local tool launches.</summary>
public sealed class MoonshotStoredCredentials
{
    public string ApiKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
