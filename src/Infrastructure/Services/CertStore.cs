using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services;

/// <summary>Where the signing key material for a <see cref="CertRecord"/> comes from. Self-signed and
/// imported PFX hold a local PKCS#12; the cloud providers are seams for later (they store endpoint /
/// key-vault references in <see cref="CertRecord.ProviderConfig"/> instead of a local key).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CertProvider
{
    SelfSigned,
    ImportedPfx,
    AzureTrustedSigning,
    AppleDeveloperId,
}

/// <summary>A pks-held code-signing certificate. The private key never leaves the store as plaintext —
/// it is materialized to a short-lived 0600 temp PKCS#12 only for the duration of one signing run.</summary>
public sealed class CertRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];
    public string? Label { get; set; }
    public CertProvider Provider { get; set; } = CertProvider.SelfSigned;
    public string Subject { get; set; } = "";
    /// <summary>SHA-1 hex thumbprint — identical to the value Windows tooling reports, so it stays
    /// interoperable across signtool / osslsigncode / the cert store.</summary>
    public string Thumbprint { get; set; } = "";
    public DateTime NotBefore { get; set; }
    public DateTime NotAfter { get; set; }
    /// <summary>Public certificate (PEM). Enough to export a <c>.cer</c> and display details without
    /// touching the encrypted key blob.</summary>
    public string PublicCertPem { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Provider-specific config (key-vault refs, endpoints, …). Null for self-signed/imported.
    /// Keeps future providers additive — no schema migration.</summary>
    public Dictionary<string, string>? ProviderConfig { get; set; }
}

/// <summary>A decrypted PKCS#12 written to a 0600 temp file with a one-shot password; deleted on dispose.</summary>
public sealed class MaterializedPfx : IDisposable
{
    public string Path { get; }
    /// <summary>The ephemeral password the PKCS#12 was (re)encrypted with — pass to signtool/osslsigncode.</summary>
    public string Password { get; }
    public MaterializedPfx(string path, string password) { Path = path; Password = password; }
    public void Dispose()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch { /* best effort */ }
    }
}

/// <summary>
/// Holds code-signing certificates for pks under <c>~/.pks-cli/certs/</c>. Created once (typically on
/// the host running <c>pks github runner start</c>) and reused across CI runs so the public trust
/// cert is stable — consumers trust it once instead of on every release.
///
/// PKCS#12 blobs are AES-GCM encrypted at rest with a KEK held outside the certs directory (same
/// model and caveats as <see cref="SshKeyStore"/>: obfuscation-grade while pks shares the agent's OS
/// user; real isolation arrives when pks runs as its own user and the 0700 dir + KEK are unreadable).
/// </summary>
public interface ICertStore
{
    Task<CertRecord> CreateSelfSignedAsync(string subject, string? label, TimeSpan validity, CancellationToken ct = default);
    /// <summary>Import an existing PKCS#12 (PFX). Seam for `pks cert import` — provider = ImportedPfx.</summary>
    Task<CertRecord> ImportPfxAsync(byte[] pfx, string? password, string? label, CancellationToken ct = default);
    Task<List<CertRecord>> ListAsync();
    Task<CertRecord?> FindAsync(string idOrLabel);
    Task RemoveAsync(string id);
    Task<bool> AnyAsync();
    /// <summary>Write the public cert (DER <c>.cer</c>) to <paramref name="destPath"/>. No private key.</summary>
    Task<string> ExportPublicCerAsync(string id, string destPath, CancellationToken ct = default);
    /// <summary>Decrypt the PKCS#12 to a private 0600 temp file. Dispose the handle to shred it.</summary>
    Task<MaterializedPfx> MaterializePfxAsync(string id, CancellationToken ct = default);
}

public sealed class CertStore : ICertStore
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

    public CertStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "certs");
        _indexPath = Path.Combine(_dir, "index.json");
        // KEK lives next to (not inside) the certs dir so a future dedicated-user move can lock them
        // independently, and so tests pointed at a temp dir get an isolated KEK.
        var parent = Path.GetDirectoryName(_dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli");
        _kekPath = Path.Combine(parent, ".certs-kek");
    }

    public async Task<CertRecord> CreateSelfSignedAsync(string subject, string? label, TimeSpan validity, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Subject is required.", nameof(subject));

        var pwd = NewEphemeralPassword();
        var gen = CertGenerator.CreateSelfSigned(subject, validity, pwd);

        var record = new CertRecord
        {
            Provider = CertProvider.SelfSigned,
            Subject = gen.Subject,
            Thumbprint = gen.Thumbprint,
            NotBefore = gen.NotBefore,
            NotAfter = gen.NotAfter,
            PublicCertPem = gen.PublicCertPem,
            Label = label,
        };

        await PersistAsync(record, gen.Pkcs12, pwd, ct);
        return record;
    }

    public async Task<CertRecord> ImportPfxAsync(byte[] pfx, string? password, string? label, CancellationToken ct = default)
    {
        if (pfx == null || pfx.Length == 0)
            throw new ArgumentException("PFX is empty.", nameof(pfx));

        using var cert = X509CertificateLoader.LoadPkcs12(pfx, password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        if (!cert.HasPrivateKey)
            throw new InvalidOperationException("PFX has no private key.");

        // Re-export under a fresh ephemeral password so the store never persists the caller's password.
        var pwd = NewEphemeralPassword();
        var normalized = cert.Export(X509ContentType.Pkcs12, pwd);
        var pem = new string(PemEncoding.Write("CERTIFICATE", cert.RawData));

        var record = new CertRecord
        {
            Provider = CertProvider.ImportedPfx,
            Subject = cert.Subject,
            Thumbprint = cert.Thumbprint,
            NotBefore = cert.NotBefore.ToUniversalTime(),
            NotAfter = cert.NotAfter.ToUniversalTime(),
            PublicCertPem = pem,
            Label = label,
        };

        await PersistAsync(record, normalized, pwd, ct);
        return record;
    }

    public async Task<List<CertRecord>> ListAsync()
    {
        await _lock.WaitAsync();
        try { return await LoadIndexAsync(); }
        finally { _lock.Release(); }
    }

    public async Task<CertRecord?> FindAsync(string idOrLabel)
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
            var blobPath = BlobPath(id);
            if (File.Exists(blobPath)) File.Delete(blobPath);
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> AnyAsync() => (await ListAsync()).Count > 0;

    public async Task<string> ExportPublicCerAsync(string id, string destPath, CancellationToken ct = default)
    {
        var record = await FindAsync(id) ?? throw new FileNotFoundException($"No pks-held cert with id '{id}'.");
        var pem = record.PublicCertPem;
        if (string.IsNullOrWhiteSpace(pem))
            throw new InvalidOperationException("Cert record has no public certificate.");

        using var cert = X509Certificate2.CreateFromPem(pem);
        var der = cert.Export(X509ContentType.Cert); // DER-encoded public cert, no private key
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(destPath, der, ct);
        return destPath;
    }

    public async Task<MaterializedPfx> MaterializePfxAsync(string id, CancellationToken ct = default)
    {
        var blobPath = BlobPath(id);
        if (!File.Exists(blobPath))
            throw new FileNotFoundException($"No pks-held cert with id '{id}'.");

        var stored = Decrypt(await File.ReadAllBytesAsync(blobPath, ct));
        // stored layout: pwdLen(2) || pwd(utf8) || pkcs12
        var pwdLen = (stored[0] << 8) | stored[1];
        var pwd = Encoding.UTF8.GetString(stored, 2, pwdLen);
        var pkcs12 = stored.AsSpan(2 + pwdLen).ToArray();

        var tempPath = Path.Combine(Path.GetTempPath(), $"pks-cert-{id}-{Guid.NewGuid():n}.pfx");
        using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            SecurityFiles.Restrict(tempPath);
            await fs.WriteAsync(pkcs12, ct);
        }
        SecurityFiles.Restrict(tempPath);
        return new MaterializedPfx(tempPath, pwd);
    }

    // --- persistence ---

    private string BlobPath(string id) => Path.Combine(_dir, id + ".pfx");

    private async Task PersistAsync(CertRecord record, byte[] pkcs12, string pkcs12Password, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_dir);
            SecurityFiles.RestrictDir(_dir);

            // Bundle the PKCS#12 password with the blob so materialize is self-describing; the whole
            // bundle is AES-GCM encrypted with the KEK, so the password is never at rest in cleartext.
            var pwdBytes = Encoding.UTF8.GetBytes(pkcs12Password);
            var bundle = new byte[2 + pwdBytes.Length + pkcs12.Length];
            bundle[0] = (byte)(pwdBytes.Length >> 8);
            bundle[1] = (byte)(pwdBytes.Length & 0xff);
            Buffer.BlockCopy(pwdBytes, 0, bundle, 2, pwdBytes.Length);
            Buffer.BlockCopy(pkcs12, 0, bundle, 2 + pwdBytes.Length, pkcs12.Length);

            var blobPath = BlobPath(record.Id);
            await File.WriteAllBytesAsync(blobPath, Encrypt(bundle), ct);
            SecurityFiles.Restrict(blobPath);

            var index = await LoadIndexAsync();
            index.RemoveAll(r => r.Id == record.Id);
            index.Add(record);
            await SaveIndexAsync(index);
        }
        finally { _lock.Release(); }
    }

    private async Task<List<CertRecord>> LoadIndexAsync()
    {
        if (!File.Exists(_indexPath)) return new List<CertRecord>();
        try
        {
            var json = await File.ReadAllTextAsync(_indexPath);
            return JsonSerializer.Deserialize<List<CertRecord>>(json, JsonOptions) ?? new List<CertRecord>();
        }
        catch (JsonException) { return new List<CertRecord>(); }
    }

    private async Task SaveIndexAsync(List<CertRecord> index)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(_indexPath, JsonSerializer.Serialize(index, JsonOptions));
        SecurityFiles.Restrict(_indexPath);
    }

    private static string NewEphemeralPassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    // --- encryption at rest (AES-GCM, KEK outside the certs dir) ---

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
        if (blob.Length < 28) throw new CryptographicException("Corrupt cert blob.");
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
