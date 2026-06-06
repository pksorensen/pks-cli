using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services;

/// <summary>A pks-held SSH private key. The private material never leaves the store as plaintext —
/// it is materialized to a short-lived 0600 temp file only for the duration of one ssh invocation.</summary>
public sealed class SshKeyRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string? Label { get; set; }
    public string PublicKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Holds SSH private keys for pks under <c>~/.pks-cli/ssh-keys/</c>. The point of the store is that
/// the only ergonomic way to <em>use</em> a key is via <c>pks ssh connect</c>, which gates each use
/// through the action guard — so an agent cannot silently SSH out.
///
/// Private keys are AES-GCM encrypted at rest with a KEK held outside the key directory. NB: while
/// pks runs as the <em>same</em> OS user as the agent this is obfuscation-grade — a deliberate agent
/// could read the KEK and decrypt. The real isolation boundary is pks running as its own user (then
/// the 0700 key dir and KEK are unreadable to the agent); this store is already shaped for that day.
/// </summary>
public interface ISshKeyStore
{
    /// <summary>Import an existing OpenSSH/PEM private key (paste). Derives + stores the public key.</summary>
    Task<SshKeyRecord> ImportAsync(string privateKeyPem, string? label, CancellationToken ct = default);
    Task<List<SshKeyRecord>> ListAsync();
    Task<SshKeyRecord?> FindAsync(string idOrLabel);
    Task RemoveAsync(string id);

    /// <summary>Decrypt the key to a private 0600 temp file. Dispose the handle to shred it.</summary>
    Task<MaterializedKey> MaterializeAsync(string id, CancellationToken ct = default);
}

/// <summary>A decrypted private key written to a 0600 temp file; deleted on dispose.</summary>
public sealed class MaterializedKey : IDisposable
{
    public string Path { get; }
    public MaterializedKey(string path) => Path = path;
    public void Dispose()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch { /* best effort */ }
    }
}

public sealed class SshKeyStore : ISshKeyStore
{
    private readonly string _dir;
    private readonly string _indexPath;
    private readonly string _kekPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SshKeyStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "ssh-keys");
        _indexPath = Path.Combine(_dir, "index.json");
        // KEK lives one level up, outside the key dir, so a future dedicated-user move can lock them independently.
        _kekPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", ".ssh-keys-kek");
    }

    public async Task<SshKeyRecord> ImportAsync(string privateKeyPem, string? label, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem))
            throw new ArgumentException("Private key is empty.", nameof(privateKeyPem));

        // Normalize line endings and ensure a trailing newline (ssh-keygen is picky).
        var normalized = privateKeyPem.Replace("\r\n", "\n").TrimEnd() + "\n";

        var (publicKey, fingerprint) = await DerivePublicKeyAsync(normalized, ct);

        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_dir);
            SecurityFiles.RestrictDir(_dir);

            var record = new SshKeyRecord { Label = label, PublicKey = publicKey, Fingerprint = fingerprint };

            var blobPath = Path.Combine(_dir, record.Id + ".key");
            await File.WriteAllBytesAsync(blobPath, Encrypt(Encoding.UTF8.GetBytes(normalized)), ct);
            SecurityFiles.Restrict(blobPath);

            var index = await LoadIndexAsync();
            index.RemoveAll(r => r.Id == record.Id);
            index.Add(record);
            await SaveIndexAsync(index);

            return record;
        }
        finally { _lock.Release(); }
    }

    public async Task<List<SshKeyRecord>> ListAsync()
    {
        await _lock.WaitAsync();
        try { return await LoadIndexAsync(); }
        finally { _lock.Release(); }
    }

    public async Task<SshKeyRecord?> FindAsync(string idOrLabel)
    {
        var all = await ListAsync();
        return all.FirstOrDefault(r => string.Equals(r.Id, idOrLabel, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(r => r.Label != null && string.Equals(r.Label, idOrLabel, StringComparison.OrdinalIgnoreCase));
    }

    public async Task RemoveAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var index = await LoadIndexAsync();
            index.RemoveAll(r => r.Id == id);
            await SaveIndexAsync(index);
            var blobPath = Path.Combine(_dir, id + ".key");
            if (File.Exists(blobPath)) File.Delete(blobPath);
        }
        finally { _lock.Release(); }
    }

    public async Task<MaterializedKey> MaterializeAsync(string id, CancellationToken ct = default)
    {
        var blobPath = Path.Combine(_dir, id + ".key");
        if (!File.Exists(blobPath))
            throw new FileNotFoundException($"No pks-held SSH key with id '{id}'.");

        var plaintext = Decrypt(await File.ReadAllBytesAsync(blobPath, ct));

        var tempPath = Path.Combine(Path.GetTempPath(), $"pks-ssh-{id}-{Guid.NewGuid():n}");
        // Create with restrictive perms BEFORE writing the secret.
        using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            SecurityFiles.Restrict(tempPath);
            await fs.WriteAsync(plaintext, ct);
        }
        SecurityFiles.Restrict(tempPath);
        return new MaterializedKey(tempPath);
    }

    // --- index persistence ---

    private async Task<List<SshKeyRecord>> LoadIndexAsync()
    {
        if (!File.Exists(_indexPath)) return new List<SshKeyRecord>();
        try
        {
            var json = await File.ReadAllTextAsync(_indexPath);
            return JsonSerializer.Deserialize<List<SshKeyRecord>>(json, JsonOptions) ?? new List<SshKeyRecord>();
        }
        catch (JsonException) { return new List<SshKeyRecord>(); }
    }

    private async Task SaveIndexAsync(List<SshKeyRecord> index)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(_indexPath, JsonSerializer.Serialize(index, JsonOptions));
        SecurityFiles.Restrict(_indexPath);
    }

    // --- public-key derivation via ssh-keygen ---

    private static async Task<(string PublicKey, string Fingerprint)> DerivePublicKeyAsync(string privateKeyPem, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"pks-keyderive-{Guid.NewGuid():n}");
        try
        {
            await File.WriteAllTextAsync(tmp, privateKeyPem, ct);
            SecurityFiles.Restrict(tmp);

            var pub = await RunSshKeygenAsync(new[] { "-y", "-f", tmp }, ct);
            if (pub.ExitCode != 0)
                throw new InvalidOperationException(
                    "Could not parse the private key. Paste an unencrypted OpenSSH/PEM private key. " +
                    (string.IsNullOrWhiteSpace(pub.Stderr) ? "" : pub.Stderr.Trim()));

            var publicKey = pub.Stdout.Trim();

            // Fingerprint is best-effort cosmetic.
            var fpTmp = tmp + ".pub";
            string fingerprint = "";
            try
            {
                await File.WriteAllTextAsync(fpTmp, publicKey + "\n", ct);
                var fp = await RunSshKeygenAsync(new[] { "-lf", fpTmp }, ct);
                if (fp.ExitCode == 0) fingerprint = fp.Stdout.Trim();
            }
            finally { if (File.Exists(fpTmp)) File.Delete(fpTmp); }

            return (publicKey, fingerprint);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunSshKeygenAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ssh-keygen")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("ssh-keygen not found on PATH.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, stdout, stderr);
    }

    // --- encryption at rest (AES-GCM, KEK outside the key dir) ---

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
        // layout: nonce(12) || tag(16) || ciphertext
        var blob = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, 12);
        Buffer.BlockCopy(tag, 0, blob, 12, 16);
        Buffer.BlockCopy(cipher, 0, blob, 28, cipher.Length);
        return blob;
    }

    private byte[] Decrypt(byte[] blob)
    {
        if (blob.Length < 28) throw new CryptographicException("Corrupt key blob.");
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
