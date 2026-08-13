namespace PKS.Infrastructure.Services.Security;

/// <summary>
/// Copies one stored credential from this user's store into another HOME's store.
///
/// It exists because isolated HOMEs are a real pattern here — the Aspire AppHost gives the ALP runner
/// its own <c>~/.pks-cli</c> precisely so the operator's other tokens do not leak into it, and then
/// needs exactly one of them (the Foundry session) to be present. Before the quarantine that was a
/// `File.ReadAllText` on settings.json; now the plaintext never leaves this service.
///
/// Deliberately one key at a time: the whole point of an isolated HOME is that "copy the credential
/// store" is the wrong operation.
/// </summary>
public interface ISecretSeedingService
{
    /// <summary>Copies <paramref name="key"/> into <paramref name="homeDirectory"/>'s pks store.
    /// False means nothing was stored under that key here — not an error, just nothing to seed.</summary>
    Task<bool> SeedIntoHomeAsync(string key, string homeDirectory);
}

public sealed class SecretSeedingService : ISecretSeedingService
{
    private readonly ISecretResolver _secrets;

    public SecretSeedingService(ISecretResolver? secrets = null) => _secrets = secrets ?? new SecretStore();

    public async Task<bool> SeedIntoHomeAsync(string key, string homeDirectory)
    {
        var value = await _secrets.RevealAsync(key);
        if (string.IsNullOrEmpty(value)) return false;

        var targetConfigDir = Path.Combine(homeDirectory, ".pks-cli");
        Directory.CreateDirectory(targetConfigDir);
        SecurityFiles.RestrictDir(targetConfigDir);

        // A fresh store rooted at the target HOME: its own KEK, its own file, both 0600. The value
        // is re-encrypted there rather than the source files being copied, so the target never
        // inherits credentials it was not asked for.
        await new SecretStore(targetConfigDir).SetAsync(key, value);
        return true;
    }
}
