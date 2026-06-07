using PKS.Infrastructure.Services;

namespace PKS.Infrastructure.Services.Signing;

/// <summary>What to sign and how.</summary>
public sealed record SignRequest(string InputPath, string OutputPath, string? TimestampUrl);

public sealed record SignResult(bool Success, string Message);

/// <summary>
/// Signs an artifact using the key material described by a <see cref="CertRecord"/>. One provider per
/// <see cref="CertProvider"/> — self-signed/imported go through <see cref="OsslSignProvider"/>; cloud
/// providers (Azure Trusted Signing, Apple Developer ID) are future additions, registered the same way.
/// </summary>
public interface ISignProvider
{
    bool CanHandle(CertProvider provider);
    Task<SignResult> SignAsync(CertRecord cert, ICertStore store, SignRequest request, CancellationToken ct = default);
}
