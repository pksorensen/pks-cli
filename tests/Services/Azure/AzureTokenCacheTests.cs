using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Azure;
using Xunit;

namespace PKS.CLI.Tests.Services.Azure;

/// <summary>
/// The (key, scope) token cache that replaced <c>FoundryTokenCredential</c>'s Foundry-only L1/L2.
/// Every test gets its own directory so the on-disk L2 never leaks between tests or into
/// <c>~/.pks-cli</c>, and every token literal is obviously fake.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public sealed class AzureTokenCacheTests : IDisposable
{
    private const string Scope = "https://management.azure.com/.default";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pks-cli-tests", "token-cache", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private AzureTokenCache NewCache() => new(_dir);

    private static Func<CancellationToken, Task<(string, DateTimeOffset)>> Refresh(string token, Counter counter, TimeSpan? lifetime = null)
        => _ =>
        {
            counter.Count++;
            return Task.FromResult((token, DateTimeOffset.UtcNow + (lifetime ?? TimeSpan.FromHours(1))));
        };

    private sealed class Counter { public int Count; }

    [Fact]
    public async Task DifferentKeys_DoNotShareTokens()
    {
        var cache = NewCache();
        var a = new Counter();
        var b = new Counter();

        var tokenA = await cache.GetOrRefreshAsync("tenant-a", Scope, Refresh("fake-token-a", a), CancellationToken.None);
        var tokenB = await cache.GetOrRefreshAsync("tenant-b", Scope, Refresh("fake-token-b", b), CancellationToken.None);

        tokenA.Should().Be("fake-token-a");
        tokenB.Should().Be("fake-token-b");
        a.Count.Should().Be(1);
        b.Count.Should().Be(1);
    }

    [Fact]
    public async Task SameKeyAndScope_SharesToken()
    {
        var cache = NewCache();
        var counter = new Counter();

        var first = await cache.GetOrRefreshAsync("tenant-a", Scope, Refresh("fake-token-1", counter), CancellationToken.None);
        var second = await cache.GetOrRefreshAsync("tenant-a", Scope, Refresh("fake-token-2", counter), CancellationToken.None);

        first.Should().Be("fake-token-1");
        second.Should().Be("fake-token-1", "the second call must be served from the cache");
        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task SameKey_DifferentScope_RefreshesSeparately()
    {
        var cache = NewCache();
        var counter = new Counter();

        var mgmt = await cache.GetOrRefreshAsync("tenant-a", Scope, Refresh("fake-mgmt-token", counter), CancellationToken.None);
        var storage = await cache.GetOrRefreshAsync("tenant-a", "https://storage.azure.com/.default", Refresh("fake-storage-token", counter), CancellationToken.None);

        mgmt.Should().Be("fake-mgmt-token");
        storage.Should().Be("fake-storage-token");
        counter.Count.Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentCalls_RefreshOnce()
    {
        var cache = NewCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;

        Func<CancellationToken, Task<(string, DateTimeOffset)>> refresh = async _ =>
        {
            Interlocked.Increment(ref count);
            entered.TrySetResult();
            await release.Task;
            return ("fake-shared-token", DateTimeOffset.UtcNow.AddHours(1));
        };

        var first = cache.GetOrRefreshAsync("tenant-a", Scope, refresh, CancellationToken.None);
        await entered.Task; // the first refresh is provably in flight
        var second = cache.GetOrRefreshAsync("tenant-a", Scope, refresh, CancellationToken.None);
        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse("the second caller must wait for the in-flight refresh");

        release.SetResult();
        var tokens = await Task.WhenAll(first, second);

        tokens.Should().AllBe("fake-shared-token");
        count.Should().Be(1);
    }

    [Fact]
    public async Task ExpiredToken_IsRefreshed()
    {
        var cache = NewCache();
        var counter = new Counter();

        // Inside the refresh skew: stored, but never served as fresh.
        await cache.GetOrRefreshAsync("tenant-a", Scope, Refresh("fake-token-old", counter, TimeSpan.FromMinutes(1)), CancellationToken.None);
        var second = await cache.GetOrRefreshAsync("tenant-a", Scope, Refresh("fake-token-new", counter), CancellationToken.None);

        second.Should().Be("fake-token-new");
        counter.Count.Should().Be(2);
    }

    [Fact]
    public async Task DiskCache_IsReadBySecondInstance()
    {
        var writer = NewCache();
        var reader = NewCache();
        var writerCount = new Counter();
        var readerCount = new Counter();

        await writer.GetOrRefreshAsync("tenant-a", Scope, Refresh("fake-token-disk", writerCount), CancellationToken.None);
        var fromDisk = await reader.GetOrRefreshAsync("tenant-a", Scope, Refresh("fake-token-should-not-be-minted", readerCount), CancellationToken.None);

        fromDisk.Should().Be("fake-token-disk");
        readerCount.Count.Should().Be(0);
        File.Exists(Path.Combine(_dir, "azure-token-cache.json")).Should().BeTrue();
    }

    [Fact]
    public async Task DiskCache_HoldsOneEntryPerKeyAndScope()
    {
        var cache = NewCache();

        await cache.GetOrRefreshAsync("tenant-a", Scope, Refresh("fake-token-a", new Counter()), CancellationToken.None);
        await cache.GetOrRefreshAsync("tenant-b", Scope, Refresh("fake-token-b", new Counter()), CancellationToken.None);

        var json = await File.ReadAllTextAsync(Path.Combine(_dir, "azure-token-cache.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty($"tenant-a|{Scope}", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty($"tenant-b|{Scope}", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CorruptDiskCache_IsTreatedAsMiss()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "azure-token-cache.json"), "{ not json");
        var cache = NewCache();
        var counter = new Counter();

        var token = await cache.GetOrRefreshAsync("tenant-a", Scope, Refresh("fake-token-fresh", counter), CancellationToken.None);

        token.Should().Be("fake-token-fresh");
        counter.Count.Should().Be(1);
    }

    [Fact]
    public async Task RefreshFailure_PropagatesUnchanged()
    {
        var cache = NewCache();

        var act = () => cache.GetOrRefreshAsync("tenant-a", Scope,
            _ => throw new InvalidOperationException("refresh boom"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("refresh boom");
    }

    [Fact]
    public async Task DiskFiles_AreOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        var cache = NewCache();

        await cache.GetOrRefreshAsync("tenant-a", Scope, Refresh("fake-token", new Counter()), CancellationToken.None);

        File.GetUnixFileMode(Path.Combine(_dir, "azure-token-cache.json"))
            .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
