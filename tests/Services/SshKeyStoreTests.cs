using System.Diagnostics;
using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Services;

/// <summary>
/// Tests for the pks-held SSH key store: import (derives public key via ssh-keygen),
/// encryption at rest, materialize round-trip, and removal. Requires ssh-keygen on PATH.
/// </summary>
public class SshKeyStoreTests : TestBase
{
    private readonly string _dir;

    public SshKeyStoreTests()
    {
        _dir = Path.Combine(CreateTempDirectory(), "ssh-keys");
    }

    private SshKeyStore CreateStore() => new(_dir);

    private static string GenerateEd25519PrivateKey()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"pks-test-key-{Guid.NewGuid():n}");
        var psi = new ProcessStartInfo("ssh-keygen")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-t", "ed25519", "-N", "", "-f", tmp, "-q" }) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!) { p.WaitForExit(); }
        var pem = File.ReadAllText(tmp);
        File.Delete(tmp);
        File.Delete(tmp + ".pub");
        return pem;
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task ImportAsync_DerivesPublicKey_AndPersists()
    {
        var store = CreateStore();
        var pem = GenerateEd25519PrivateKey();

        var record = await store.ImportAsync(pem, "hetzner");

        record.Id.Should().NotBeNullOrEmpty();
        record.Label.Should().Be("hetzner");
        record.PublicKey.Should().StartWith("ssh-ed25519 ");

        var reloaded = await CreateStore().ListAsync();
        reloaded.Should().ContainSingle(r => r.Id == record.Id && r.Label == "hetzner");
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task PrivateKey_IsNotStoredInPlaintext()
    {
        var store = CreateStore();
        var pem = GenerateEd25519PrivateKey();
        var record = await store.ImportAsync(pem, null);

        var blob = await File.ReadAllBytesAsync(Path.Combine(_dir, record.Id + ".key"));
        var asText = System.Text.Encoding.UTF8.GetString(blob);
        asText.Should().NotContain("PRIVATE KEY", "the key must be encrypted at rest");
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task MaterializeAsync_RoundTripsToOriginalKey_AndShredsOnDispose()
    {
        var store = CreateStore();
        var pem = GenerateEd25519PrivateKey();
        var record = await store.ImportAsync(pem, null);

        string tempPath;
        using (var mat = await store.MaterializeAsync(record.Id))
        {
            tempPath = mat.Path;
            File.Exists(tempPath).Should().BeTrue();
            var roundTripped = await File.ReadAllTextAsync(tempPath);
            roundTripped.Trim().Should().Be(pem.Replace("\r\n", "\n").TrimEnd());
        }

        File.Exists(tempPath).Should().BeFalse("the materialized key must be deleted on dispose");
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task RemoveAsync_DeletesKeyAndBlob()
    {
        var store = CreateStore();
        var record = await store.ImportAsync(GenerateEd25519PrivateKey(), "gone");

        await store.RemoveAsync(record.Id);

        (await store.ListAsync()).Should().BeEmpty();
        File.Exists(Path.Combine(_dir, record.Id + ".key")).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task ImportAsync_RejectsGarbage()
    {
        var store = CreateStore();
        var act = async () => await store.ImportAsync("not a private key", null);
        await act.Should().ThrowAsync<Exception>();
    }
}
