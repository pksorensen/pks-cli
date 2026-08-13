using System.Text.RegularExpressions;

namespace PKS.Infrastructure.Services.Security;

/// <summary>
/// Decides whether a configuration key holds credential material.
///
/// This is the single classifier used by both the settings migration and by
/// <c>ConfigurationService.SetAsync</c>, so "is this a secret?" is answered the same way
/// whether a value is being written today or was written by an older build. Secret-ness is
/// therefore a property of the key, and a key that is classified as secret can only ever
/// live in the encrypted <see cref="SecretStore"/> — never in <c>settings.json</c>.
///
/// Over-classification is not free: a misclassified ordinary key becomes invisible to
/// <c>IConfigurationService.GetAsync</c> and its feature breaks silently. The word list is
/// therefore explicit rather than clever, and <c>SecretKeysTests</c> pins it against the
/// real key inventory on both sides.
/// </summary>
public static class SecretKeys
{
    /// <summary>
    /// The sentinel the old (broken) <c>encrypt: true</c> path wrote instead of the value.
    /// Any stored value equal to this is destroyed credential material, not a secret.
    /// </summary>
    public const string LostValueSentinel = "***encrypted***";

    // Separators used across the key inventory: dots (foundry.auth.credentials), colons
    // (google:api_key) and underscores (jira:api_token). All three delimit a word here.
    private const string Boundary = @"[_\-.:]";

    private static readonly Regex SecretKey = new(
        $"(^|{Boundary})("
        + "secret|secrets|client_secret"
        + "|token|tokens|api_token|auth_token|access_token|refresh_token|session_token"
        + "|password|passwd|pwd"
        + "|credential|credentials"
        + "|apikey|api_key|access_key|private_key|secret_key"
        + "|authorization"
        + $")({Boundary}|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when <paramref name="key"/> names credential material.</summary>
    public static bool IsSecret(string key) =>
        !string.IsNullOrWhiteSpace(key) && SecretKey.IsMatch(key);

    /// <summary>
    /// True when a stored value carries no recoverable secret — the sentinel written by the
    /// old <c>encrypt: true</c> path. Migration drops these instead of enshrining them.
    /// </summary>
    public static bool IsLostValue(string? value) =>
        string.Equals(value, LostValueSentinel, StringComparison.Ordinal);
}
