using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Security;

/// <summary>What may be told about a stored secret without revealing it: that it exists, when it was
/// written, and a fingerprint that lets two machines agree they hold the same value.</summary>
public sealed record SecretDescriptor(string Key, DateTime SetAt, string Fingerprint);

/// <summary>
/// The write-only surface over stored credentials. Everything here is safe to reach from a command,
/// an MCP tool, or anything else whose output an agent or a terminal transcript will see: it can
/// prove a secret is present and prove two secrets are equal, but it cannot produce one.
/// </summary>
public interface ISecretStore
{
    Task SetAsync(string key, string value);
    Task<bool> HasAsync(string key);
    Task<bool> DeleteAsync(string key);
    Task<SecretDescriptor?> DescribeAsync(string key);
    Task<IReadOnlyList<SecretDescriptor>> ListAsync();
}

/// <summary>
/// The plaintext side. Only services that <em>use</em> a credential — the ones that put it in an
/// Authorization header or hand it to a provider SDK — may take this dependency.
///
/// <c>SecretResolverGateTests</c> fails the build if anything under <c>src/Commands/</c> or
/// <c>src/Infrastructure/Services/MCP/</c> so much as names it. That gate is the actual guarantee;
/// the type system cannot express "reachable but not printable" inside a single assembly.
/// </summary>
public interface ISecretResolver
{
    Task<string?> RevealAsync(string key);
}

/// <summary>
/// AES-GCM encrypted credential storage at <c>~/.pks-cli/secrets.json</c>, keyed by a 32-byte KEK
/// sidecar at <c>~/.pks-cli/.secrets-kek</c>. Same construction as <see cref="SshKeyStore"/> and
/// <c>CertStore</c>: per-value <c>nonce(12) || tag(16) || ciphertext</c>, everything 0600.
///
/// The encryption is not a defence against a same-UID attacker — it can read the KEK. What it buys
/// is that a stray <c>cat ~/.pks-cli/settings.json</c>, a backup, a synced dotfile, or a config dump
/// through an MCP tool no longer yields a usable token, which is the failure that keeps happening.
/// </summary>
public sealed class SecretStore : ISecretStore, ISecretResolver
{
    private const int Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _storePath;
    private readonly string _kekPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SecretStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli"))
    {
    }

    /// <summary>Roots the store at an explicit pks config directory. Used by tests and by
    /// <c>pks secrets seed-home</c>, which writes into another HOME's store.</summary>
    public SecretStore(string configDirectory)
    {
        _storePath = Path.Combine(configDirectory, "secrets.json");
        _kekPath = Path.Combine(configDirectory, ".secrets-kek");
    }

    public async Task SetAsync(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Secret key is required.", nameof(key));
        ArgumentNullException.ThrowIfNull(value);

        await MutateAsync(file =>
        {
            file.Secrets[key] = new StoredSecret
            {
                Blob = Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(value))),
                SetAt = DateTime.UtcNow,
                Fingerprint = Fingerprint(value)
            };
            return true;
        });
    }

    public async Task<bool> HasAsync(string key)
    {
        var file = await LoadAsync();
        return file.Secrets.ContainsKey(key);
    }

    public async Task<bool> DeleteAsync(string key)
    {
        var removed = false;
        await MutateAsync(file =>
        {
            removed = file.Secrets.Remove(key);
            return removed;
        });
        return removed;
    }

    public async Task<SecretDescriptor?> DescribeAsync(string key)
    {
        var file = await LoadAsync();
        return file.Secrets.TryGetValue(key, out var stored)
            ? new SecretDescriptor(key, stored.SetAt, stored.Fingerprint ?? "")
            : null;
    }

    public async Task<IReadOnlyList<SecretDescriptor>> ListAsync()
    {
        var file = await LoadAsync();
        return file.Secrets
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new SecretDescriptor(kvp.Key, kvp.Value.SetAt, kvp.Value.Fingerprint ?? ""))
            .ToList();
    }

    public async Task<string?> RevealAsync(string key)
    {
        var file = await LoadAsync();
        if (!file.Secrets.TryGetValue(key, out var stored) || string.IsNullOrEmpty(stored.Blob)) return null;

        try
        {
            return Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(stored.Blob)));
        }
        catch
        {
            // A blob written under a KEK that has since been replaced (restored backup, copied HOME)
            // is unrecoverable. Report it as absent so callers fall back to re-authenticating.
            return null;
        }
    }

    // ---- persistence -------------------------------------------------------

    private sealed class StoredSecret
    {
        [JsonPropertyName("blob")] public string Blob { get; set; } = "";
        [JsonPropertyName("setAt")] public DateTime SetAt { get; set; }
        [JsonPropertyName("fp")] public string? Fingerprint { get; set; }
    }

    private sealed class SecretFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = SecretStore.Version;
        [JsonPropertyName("secrets")] public Dictionary<string, StoredSecret> Secrets { get; set; } = new();
    }

    private async Task<SecretFile> LoadAsync()
    {
        try
        {
            if (!File.Exists(_storePath)) return new SecretFile();
            var json = await File.ReadAllTextAsync(_storePath);
            return JsonSerializer.Deserialize<SecretFile>(json, JsonOptions) ?? new SecretFile();
        }
        catch
        {
            return new SecretFile();
        }
    }

    /// <summary>Read-modify-write under the same cross-process lock discipline as settings.json —
    /// a runner and an interactive pks can be writing at the same time.</summary>
    private async Task MutateAsync(Func<SecretFile, bool> mutate)
    {
        await _gate.WaitAsync();
        try
        {
            SecurityFiles.EnsureDirectory(_storePath);
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) SecurityFiles.RestrictDir(dir);

            await using var fileLock = await AcquireLockAsync();

            var file = await LoadAsync();
            if (!mutate(file)) return;

            var json = JsonSerializer.Serialize(file, JsonOptions);
            var tmp = $"{_storePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(tmp, json);
                SecurityFiles.Restrict(tmp);
                File.Move(tmp, _storePath, overwrite: true);
                SecurityFiles.Restrict(_storePath);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FileStream> AcquireLockAsync()
    {
        var lockPath = $"{_storePath}.lock";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                SecurityFiles.Restrict(lockPath);
                return stream;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
        }
    }

    // ---- crypto ------------------------------------------------------------

    private byte[] LoadOrCreateKek()
    {
        if (File.Exists(_kekPath))
        {
            var existing = File.ReadAllBytes(_kekPath);
            if (existing.Length == 32) return existing;
        }

        SecurityFiles.EnsureDirectory(_kekPath);
        var fresh = RandomNumberGenerator.GetBytes(32);
        try
        {
            // CreateNew, not WriteAllBytes: two pks processes racing on first run must not each
            // install a KEK, or the loser's already-written blobs become undecryptable.
            using var stream = new FileStream(_kekPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(fresh);
            stream.Flush();
        }
        catch (IOException) when (File.Exists(_kekPath))
        {
            var winner = File.ReadAllBytes(_kekPath);
            if (winner.Length == 32) return winner;
            throw;
        }

        SecurityFiles.Restrict(_kekPath);
        return fresh;
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        var kek = LoadOrCreateKek();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(kek, TagSize))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag);
        }

        var blob = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, blob, NonceSize + TagSize, cipher.Length);
        return blob;
    }

    private byte[] Decrypt(byte[] blob)
    {
        if (blob.Length < NonceSize + TagSize) throw new CryptographicException("Secret blob is truncated.");

        var kek = LoadOrCreateKek();
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipher = blob.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[cipher.Length];

        using var aes = new AesGcm(kek, TagSize);
        aes.Decrypt(nonce, cipher, tag, plaintext);
        return plaintext;
    }

    /// <summary>
    /// Keyed fingerprint over the plaintext, so <c>DescribeAsync</c> can answer "is this the same
    /// credential as over there?" without becoming a confirmation oracle: a bare hash would let a
    /// caller test guessed values offline. HMAC under the local KEK makes the digest meaningless
    /// off this machine.
    /// </summary>
    private string Fingerprint(string value)
    {
        using var hmac = new HMACSHA256(LoadOrCreateKek());
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest)[..12].ToLowerInvariant();
    }
}
