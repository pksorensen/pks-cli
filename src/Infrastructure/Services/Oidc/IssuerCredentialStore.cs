using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Oidc;

public sealed class IssuerCredentialStore : IIssuerCredentialStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public IssuerCredentialStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli", "auth", "issuers"))
    {
    }

    public IssuerCredentialStore(string root) => _root = root;

    public async Task<IssuerCredentials?> LoadAsync(string issuer, CancellationToken ct = default)
    {
        var path = PathFor(issuer);
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<IssuerCredentials>(await File.ReadAllTextAsync(path, ct), Json);
        }
        catch (JsonException)
        {
            // A corrupt credential file is a re-login, not a crash.
            return null;
        }
        finally { _lock.Release(); }
    }

    public async Task SaveAsync(IssuerCredentials credentials, CancellationToken ct = default)
    {
        var path = PathFor(credentials.Issuer);
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_root);
            credentials.SavedAt = DateTime.UtcNow;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(credentials, Json), ct);

            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* best effort — same as the single-login file */ }
            }
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<IssuerCredentials>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(_root)) return [];
            var all = new List<IssuerCredentials>();
            foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
            {
                try
                {
                    var c = JsonSerializer.Deserialize<IssuerCredentials>(await File.ReadAllTextAsync(file, ct), Json);
                    if (c is not null) all.Add(c);
                }
                catch (JsonException) { /* skip */ }
            }

            return all;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> DeleteAsync(string issuer, CancellationToken ct = default)
    {
        var path = PathFor(issuer);
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);

            return true;
        }
        finally { _lock.Release(); }
    }

    private string PathFor(string issuer)
    {
        var normalized = issuer.TrimEnd('/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();

        return Path.Combine(_root, $"{hash[..16]}.json");
    }
}
