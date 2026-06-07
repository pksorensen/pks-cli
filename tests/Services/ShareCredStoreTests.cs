using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Agents;
using Xunit;

namespace PKS.CLI.Tests.Services;

/// <summary>Tests for the Agent Share credential store: encrypted refresh-token round-trip,
/// lookup by host / sole-login, listing, and removal.</summary>
public class ShareCredStoreTests : TestBase
{
    private readonly string _dir;

    public ShareCredStoreTests()
    {
        _dir = Path.Combine(CreateTempDirectory(), "share");
    }

    private ShareCredStore CreateStore() => new(_dir);

    private static ShareCred Cred(string host) => new()
    {
        Host = host,
        Issuer = "https://login.agentics.dk/realms/agentics",
        ClientId = "agentics-share-desktop",
        Sub = "user-123",
        DisplayName = "Poul",
    };

    [Fact]
    [Trait("Category", "Core")]
    public async Task Save_Then_Get_Decrypts_RefreshToken()
    {
        var store = CreateStore();
        await store.SaveAsync(Cred("https://share.agentics.dk"), "refresh-abc");

        var got = await store.GetAsync("https://share.agentics.dk");
        got.Should().NotBeNull();
        got!.Sub.Should().Be("user-123");
        got.RefreshTokenEnc.Should().NotBeNullOrEmpty();
        got.RefreshTokenEnc.Should().NotContain("refresh-abc"); // encrypted at rest

        (await store.DecryptRefreshAsync(got)).Should().Be("refresh-abc");
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task Get_NoArg_Returns_Sole_Login()
    {
        var store = CreateStore();
        await store.SaveAsync(Cred("https://share.agentics.dk"), "r1");

        var got = await store.GetAsync();
        got.Should().NotBeNull();
        got!.Host.Should().Be("https://share.agentics.dk");
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task Get_NoArg_Null_When_Ambiguous()
    {
        var store = CreateStore();
        await store.SaveAsync(Cred("https://share.agentics.dk"), "r1");
        await store.SaveAsync(Cred("https://share.example.com"), "r2");

        (await store.GetAsync()).Should().BeNull();
        (await store.ListAsync()).Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task Remove_Deletes_The_Login()
    {
        var store = CreateStore();
        await store.SaveAsync(Cred("https://share.agentics.dk"), "r1");
        await store.RemoveAsync("https://share.agentics.dk");

        (await store.GetAsync("https://share.agentics.dk")).Should().BeNull();
        (await store.ListAsync()).Should().BeEmpty();
    }
}
