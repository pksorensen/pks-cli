using FluentAssertions;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Tests for NamedContainerPool covering registration, lookup, locking,
/// and removal of named containers.
/// </summary>
public class NamedContainerPoolTests
{
    private readonly NamedContainerPool _pool = new();

    [Fact]
    public void TryGet_WhenEmpty_ReturnsNull()
    {
        var result = _pool.TryGet("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public void Register_ThenTryGet_ReturnsEntry()
    {
        var entry = CreateEntry("test-container");
        _pool.Register(entry);

        var result = _pool.TryGet("test-container");
        result.Should().NotBeNull();
        result!.ContainerId.Should().Be("container-123");
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        _pool.Register(CreateEntry("My-Container"));

        _pool.TryGet("my-container").Should().NotBeNull();
        _pool.TryGet("MY-CONTAINER").Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_WhenFree_ReturnsImmediately()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var handle = await _pool.AcquireAsync("test", cts.Token);
        // Should not throw or timeout
    }

    [Fact]
    public async Task AcquireAsync_WhenInUse_BlocksUntilReleased()
    {
        _pool.Register(CreateEntry("test"));

        // Acquire first lock
        var lock1 = await _pool.AcquireAsync("test");

        // Entry should be marked in use
        _pool.TryGet("test")!.InUse.Should().BeTrue();

        // Start acquiring second lock (should block)
        var lock2Task = _pool.AcquireAsync("test");

        // Give it a moment - should not complete
        await Task.Delay(50);
        lock2Task.IsCompleted.Should().BeFalse();

        // Release first lock
        lock1.Dispose();

        // Second lock should now complete
        using var lock2 = await lock2Task;
    }

    [Fact]
    public async Task AcquireAsync_WhenCancelled_ThrowsOperationCanceled()
    {
        // Acquire lock to block
        var lock1 = await _pool.AcquireAsync("test");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await _pool.AcquireAsync("test", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        lock1.Dispose();
    }

    [Fact]
    public async Task Dispose_MarksEntryAsNotInUse()
    {
        _pool.Register(CreateEntry("test"));

        var handle = await _pool.AcquireAsync("test");
        _pool.TryGet("test")!.InUse.Should().BeTrue();

        handle.Dispose();
        _pool.TryGet("test")!.InUse.Should().BeFalse();
    }

    [Fact]
    public void Remove_RemovesEntry()
    {
        _pool.Register(CreateEntry("test"));
        _pool.TryGet("test").Should().NotBeNull();

        _pool.Remove("test");
        _pool.TryGet("test").Should().BeNull();
    }

    [Fact]
    public void GetAll_ReturnsAllEntries()
    {
        _pool.Register(CreateEntry("container-a"));
        _pool.Register(CreateEntry("container-b"));

        var all = _pool.GetAll();
        all.Should().HaveCount(2);
    }

    private static NamedContainerEntry CreateEntry(string name) => new()
    {
        Name = name,
        ContainerId = "container-123",
        ClonePath = "/tmp/test-clone",
        Owner = "testowner",
        Repository = "testrepo",
        CreatedAt = DateTime.UtcNow,
        LastUsedAt = DateTime.UtcNow
    };
}
