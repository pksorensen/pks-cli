using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Models;

/// <summary>NVIDIA NIM (build.nvidia.com) API credentials.</summary>
public sealed class NvidiaStoredCredentials
{
    public SecretValue ApiKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum NvidiaKeyVerdict
{
    /// <summary>NVIDIA served the probe — the key works.</summary>
    Valid,

    /// <summary>NVIDIA answered 401/403 — the key is wrong, expired or revoked.</summary>
    Rejected,

    /// <summary>
    /// Something else went wrong: the probe model was retired, a gateway failed, the network is out.
    /// Distinct from <see cref="Rejected"/> on purpose — telling someone their key is bad when the
    /// real problem is a 503 sends them off to rotate a credential that was never broken.
    /// </summary>
    Inconclusive,
}

public sealed record NvidiaValidationResult(NvidiaKeyVerdict Verdict, int? StatusCode, string? Detail)
{
    public bool IsValid => Verdict == NvidiaKeyVerdict.Valid;
}
