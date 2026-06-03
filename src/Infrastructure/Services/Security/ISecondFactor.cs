namespace PKS.Infrastructure.Services.Security;

public sealed record SecondFactorResult(bool Verified, string? Reason)
{
    public static SecondFactorResult Ok() => new(true, null);
    public static SecondFactorResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Pluggable second factor. TOTP (<see cref="TotpSecondFactor"/>) is the first implementation;
/// an out-of-band phone-push provider can be added later by implementing this interface — the
/// <see cref="IActionGuard"/> resolves the enrolled one. Implementations own their own user
/// interaction (TOTP prompts for a code; a push provider would notify a device and poll).
/// </summary>
public interface ISecondFactor
{
    string ProviderKey { get; }

    /// <summary>Whether this factor has been set up (e.g. a TOTP seed exists).</summary>
    Task<bool> IsEnrolledAsync();

    /// <summary>Obtain and verify the factor for this action. Never throws; reports via the result.</summary>
    Task<SecondFactorResult> ChallengeAsync(ActionRequest request, CancellationToken ct = default);
}
