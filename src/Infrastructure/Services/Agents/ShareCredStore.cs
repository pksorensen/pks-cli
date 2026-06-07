using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Agents;

/// <summary>
/// A stored Agent Share login: the OIDC issuer/client it was obtained from, the
/// resolved user identity, and (encrypted) refresh token. The refresh token is
/// the only secret — it is AES-GCM encrypted at rest with a KEK held outside the
/// store dir (same model + caveats as <see cref="CertStore"/> / SshKeyStore).
/// </summary>
public sealed class ShareCred
{
    public string Host { get; set; } = "";        // e.g. https://share.agentics.dk
    public string Issuer { get; set; } = "";       // e.g. https://login.agentics.dk/realms/agentics
    public string ClientId { get; set; } = "";     // public desktop/CLI client
    public string Sub { get; set; } = "";          // resolved user id (token sub)
    public string DisplayName { get; set; } = "";
    public string RefreshTokenEnc { get; set; } = ""; // base64(AES-GCM blob)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public interface IShareCredStore
{
    Task SaveAsync(ShareCred cred, string refreshToken, CancellationToken ct = default);
    /// <summary>The stored login for a host (default: the sole login if only one).</summary>
    Task<ShareCred?> GetAsync(string? host = null, CancellationToken ct = default);
    Task<List<ShareCred>> ListAsync(CancellationToken ct = default);
    /// <summary>Decrypt the refresh token for a stored login.</summary>
    Task<string> DecryptRefreshAsync(ShareCred cred, CancellationToken ct = default);
    Task RemoveAsync(string host, CancellationToken ct = default);
}

/// <summary>
/// Holds Agent Share logins under <c>~/.pks-cli/share/</c> — one JSON file per host.
/// Created by <c>pks share init</c> (the OIDC dance) and consumed by
/// <c>pks agent register</c> to mint agent inboxes on the user's behalf.
/// </summary>
public sealed class ShareCredStore : IShareCredStore
{
    private readonly string _dir;
    private readonly string _kekPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ShareCredStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "share");
        var parent = Path.GetDirectoryName(_dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli");
        _kekPath = Path.Combine(parent, ".share-kek");
    }

    private static string HostKey(string host)
    {
        var h = host.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        var safe = new string(h.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return safe.Length == 0 ? "default" : safe;
    }

    private string PathFor(string host) => Path.Combine(_dir, HostKey(host) + ".json");

    public async Task SaveAsync(ShareCred cred, string refreshToken, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_dir);
            cred.RefreshTokenEnc = Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(refreshToken)));
            var path = PathFor(cred.Host);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(cred, JsonOptions), ct);
            SecurityFiles.Restrict(path);
        }
        finally { _lock.Release(); }
    }

    public async Task<List<ShareCred>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_dir)) return new();
        var list = new List<ShareCred>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var c = JsonSerializer.Deserialize<ShareCred>(await File.ReadAllTextAsync(f, ct));
                if (c != null) list.Add(c);
            }
            catch { /* skip corrupt */ }
        }
        return list;
    }

    public async Task<ShareCred?> GetAsync(string? host = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(host))
        {
            var path = PathFor(host);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ShareCred>(await File.ReadAllTextAsync(path, ct));
        }
        var all = await ListAsync(ct);
        return all.Count == 1 ? all[0] : null;
    }

    public Task<string> DecryptRefreshAsync(ShareCred cred, CancellationToken ct = default)
    {
        var plain = Decrypt(Convert.FromBase64String(cred.RefreshTokenEnc));
        return Task.FromResult(Encoding.UTF8.GetString(plain));
    }

    public async Task RemoveAsync(string host, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { var p = PathFor(host); if (File.Exists(p)) File.Delete(p); }
        finally { _lock.Release(); }
    }

    // ── crypto (identical scheme to CertStore) ───────────────────────────────
    private byte[] LoadOrCreateKek()
    {
        if (File.Exists(_kekPath))
        {
            var existing = File.ReadAllBytes(_kekPath);
            if (existing.Length == 32) return existing;
        }
        var kek = RandomNumberGenerator.GetBytes(32);
        SecurityFiles.EnsureDirectory(_kekPath);
        File.WriteAllBytes(_kekPath, kek);
        SecurityFiles.Restrict(_kekPath);
        return kek;
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        var kek = LoadOrCreateKek();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(kek, 16))
            aes.Encrypt(nonce, plaintext, cipher, tag);
        var blob = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, 12);
        Buffer.BlockCopy(tag, 0, blob, 12, 16);
        Buffer.BlockCopy(cipher, 0, blob, 28, cipher.Length);
        return blob;
    }

    private byte[] Decrypt(byte[] blob)
    {
        if (blob.Length < 28) throw new CryptographicException("Corrupt share credential blob.");
        var kek = LoadOrCreateKek();
        var nonce = blob.AsSpan(0, 12);
        var tag = blob.AsSpan(12, 16);
        var cipher = blob.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(kek, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
