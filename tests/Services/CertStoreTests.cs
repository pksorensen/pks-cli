using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Services;

/// <summary>Tests for the pks-held code-signing cert store: create/import, encryption at rest,
/// materialize round-trip, public .cer export, and removal.</summary>
public class CertStoreTests : TestBase
{
    private readonly string _dir;

    public CertStoreTests()
    {
        _dir = Path.Combine(CreateTempDirectory(), "certs");
    }

    private CertStore CreateStore() => new(_dir);

    [Fact]
    [Trait("Category", "Core")]
    public async Task CreateSelfSigned_Persists_AndReloads()
    {
        var store = CreateStore();
        var rec = await store.CreateSelfSignedAsync("CN=Agentic Live (Self-Signed)", "agentics", TimeSpan.FromDays(365));

        rec.Id.Should().NotBeNullOrEmpty();
        rec.Provider.Should().Be(CertProvider.SelfSigned);
        rec.Thumbprint.Should().HaveLength(40);

        var reloaded = await CreateStore().ListAsync();
        reloaded.Should().ContainSingle(r => r.Id == rec.Id && r.Label == "agentics");
        (await CreateStore().AnyAsync()).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task PrivateKey_IsNotStoredInPlaintext()
    {
        var store = CreateStore();
        var rec = await store.CreateSelfSignedAsync("CN=Test", null, TimeSpan.FromDays(30));

        var blob = await File.ReadAllBytesAsync(Path.Combine(_dir, rec.Id + ".pfx"));
        // An unencrypted PKCS#12 starts with the DER SEQUENCE tag 0x30; the encrypted blob begins
        // with a random 12-byte nonce, so it must not begin with a PFX header.
        blob.Should().NotBeEmpty();
        blob[0].Should().NotBe(0x30);
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task MaterializePfx_RoundTrips_AndShredsOnDispose()
    {
        var store = CreateStore();
        var rec = await store.CreateSelfSignedAsync("CN=Test", null, TimeSpan.FromDays(30));

        string tempPath;
        using (var pfx = await store.MaterializePfxAsync(rec.Id))
        {
            tempPath = pfx.Path;
            File.Exists(tempPath).Should().BeTrue();
            using var cert = X509CertificateLoader.LoadPkcs12(await File.ReadAllBytesAsync(pfx.Path), pfx.Password);
            cert.HasPrivateKey.Should().BeTrue();
            cert.Thumbprint.Should().Be(rec.Thumbprint);

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(tempPath);
                (mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)).Should().Be(UnixFileMode.None);
            }
        }
        File.Exists(tempPath).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task FindAsync_ByIdAndLabel()
    {
        var store = CreateStore();
        var rec = await store.CreateSelfSignedAsync("CN=Test", "release", TimeSpan.FromDays(30));

        (await store.FindAsync(rec.Id))!.Id.Should().Be(rec.Id);
        (await store.FindAsync("release"))!.Id.Should().Be(rec.Id);
        (await store.FindAsync("nope")).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task RemoveAsync_DeletesBlobAndIndexEntry()
    {
        var store = CreateStore();
        var rec = await store.CreateSelfSignedAsync("CN=Test", null, TimeSpan.FromDays(30));

        await store.RemoveAsync(rec.Id);

        File.Exists(Path.Combine(_dir, rec.Id + ".pfx")).Should().BeFalse();
        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task ExportPublicCer_WritesParseableDer_WithoutPrivateKey()
    {
        var store = CreateStore();
        var rec = await store.CreateSelfSignedAsync("CN=Test", null, TimeSpan.FromDays(30));
        var dest = Path.Combine(_dir, "out.cer");

        await store.ExportPublicCerAsync(rec.Id, dest);

        using var cert = X509CertificateLoader.LoadCertificateFromFile(dest);
        cert.HasPrivateKey.Should().BeFalse();
        cert.Thumbprint.Should().Be(rec.Thumbprint);
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task ImportPfx_StoresWithImportedProvider()
    {
        var gen = CertGenerator.CreateSelfSigned("CN=Imported", TimeSpan.FromDays(90), "outer");
        var store = CreateStore();

        var rec = await store.ImportPfxAsync(gen.Pkcs12, "outer", "imported");

        rec.Provider.Should().Be(CertProvider.ImportedPfx);
        rec.Thumbprint.Should().Be(gen.Thumbprint);

        using var pfx = await store.MaterializePfxAsync(rec.Id);
        using var cert = X509CertificateLoader.LoadPkcs12(await File.ReadAllBytesAsync(pfx.Path), pfx.Password);
        cert.HasPrivateKey.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task TamperedBlob_FailsToDecrypt()
    {
        var store = CreateStore();
        var rec = await store.CreateSelfSignedAsync("CN=Test", null, TimeSpan.FromDays(30));

        var blobPath = Path.Combine(_dir, rec.Id + ".pfx");
        var blob = await File.ReadAllBytesAsync(blobPath);
        blob[^1] ^= 0xff; // flip a ciphertext byte
        await File.WriteAllBytesAsync(blobPath, blob);

        var act = async () => await store.MaterializePfxAsync(rec.Id);
        await act.Should().ThrowAsync<CryptographicException>();
    }
}
