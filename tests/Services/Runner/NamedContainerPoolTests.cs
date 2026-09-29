using FluentAssertions;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Tests for NamedContainerPool covering registration, lookup, locking,
/// removal of named containers, and the repository scoping that keeps two
/// repositories sharing one runner label from sharing one container.
/// </summary>
public class NamedContainerPoolTests
{
    private const string Owner = "testowner";
    private const string Repo = "testrepo";

    private readonly NamedContainerPool _pool = new();

    [Fact]
    public void TryGet_WhenEmpty_ReturnsNull()
    {
        var result = _pool.TryGet(Owner, Repo, "nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public void Register_ThenTryGet_ReturnsEntry()
    {
        var entry = CreateEntry("test-container");
        _pool.Register(entry);

        var result = _pool.TryGet(Owner, Repo, "test-container");
        result.Should().NotBeNull();
        result!.ContainerId.Should().Be("container-123");
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        _pool.Register(CreateEntry("My-Container"));

        _pool.TryGet(Owner, Repo, "my-container").Should().NotBeNull();
        _pool.TryGet(Owner, Repo, "MY-CONTAINER").Should().NotBeNull();
    }

    /// <summary>
    /// The 2026-08-30 incident: pks-agent-pulse and agentic-live-www both name
    /// `agentics-live-www-devcontainer` in runs-on. Pulse got to an empty pool first, and every
    /// agentic-live-www job afterwards ran inside pulse's devcontainer — no dotnet, no docker,
    /// and a `docker: command not found` in a step that had always worked. A container belongs
    /// to the repository that built it, whatever the label says.
    /// </summary>
    [Fact]
    public void TryGet_DoesNotLendOneRepositorysContainerToAnother()
    {
        _pool.Register(CreateEntry("agentics-live-www-devcontainer", repository: "pks-agent-pulse"));

        _pool.TryGet(Owner, "agentic-live-www", "agentics-live-www-devcontainer").Should().BeNull();
        _pool.TryGet(Owner, "pks-agent-pulse", "agentics-live-www-devcontainer").Should().NotBeNull();
    }

    [Fact]
    public void Register_SameNameFromTwoRepositories_KeepsBoth()
    {
        _pool.Register(CreateEntry("shared-label", repository: "repo-a", containerId: "container-a"));
        _pool.Register(CreateEntry("shared-label", repository: "repo-b", containerId: "container-b"));

        _pool.TryGet(Owner, "repo-a", "shared-label")!.ContainerId.Should().Be("container-a");
        _pool.TryGet(Owner, "repo-b", "shared-label")!.ContainerId.Should().Be("container-b");
        _pool.GetAll().Should().HaveCount(2);
    }

    [Fact]
    public void Remove_OnlyRemovesTheRepositorysOwnEntry()
    {
        _pool.Register(CreateEntry("shared-label", repository: "repo-a"));
        _pool.Register(CreateEntry("shared-label", repository: "repo-b"));

        _pool.Remove(Owner, "repo-a", "shared-label");

        _pool.TryGet(Owner, "repo-a", "shared-label").Should().BeNull();
        _pool.TryGet(Owner, "repo-b", "shared-label").Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_WhenFree_ReturnsImmediately()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var handle = await _pool.AcquireAsync(Owner, Repo, "test", cts.Token);
        // Should not throw or timeout
    }

    [Fact]
    public async Task AcquireAsync_WhenInUse_BlocksUntilReleased()
    {
        _pool.Register(CreateEntry("test"));

        // Acquire first lock
        var lock1 = await _pool.AcquireAsync(Owner, Repo, "test");

        // Entry should be marked in use
        _pool.TryGet(Owner, Repo, "test")!.InUse.Should().BeTrue();

        // Start acquiring second lock (should block)
        var lock2Task = _pool.AcquireAsync(Owner, Repo, "test");

        // Give it a moment - should not complete
        await Task.Delay(50);
        lock2Task.IsCompleted.Should().BeFalse();

        // Release first lock
        lock1.Dispose();

        // Second lock should now complete
        using var lock2 = await lock2Task;
    }

    /// <summary>
    /// Two repositories that happen to share a runner label have separate containers, so they
    /// must not serialize against each other's lock either.
    /// </summary>
    [Fact]
    public async Task AcquireAsync_LocksAreScopedPerRepository()
    {
        using var held = await _pool.AcquireAsync(Owner, "repo-a", "shared-label");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var other = await _pool.AcquireAsync(Owner, "repo-b", "shared-label", cts.Token);
        // Should not block on repo-a's lock
    }

    [Fact]
    public async Task AcquireAsync_WhenCancelled_ThrowsOperationCanceled()
    {
        // Acquire lock to block
        var lock1 = await _pool.AcquireAsync(Owner, Repo, "test");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await _pool.AcquireAsync(Owner, Repo, "test", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        lock1.Dispose();
    }

    [Fact]
    public async Task Dispose_MarksEntryAsNotInUse()
    {
        _pool.Register(CreateEntry("test"));

        var handle = await _pool.AcquireAsync(Owner, Repo, "test");
        _pool.TryGet(Owner, Repo, "test")!.InUse.Should().BeTrue();

        handle.Dispose();
        _pool.TryGet(Owner, Repo, "test")!.InUse.Should().BeFalse();
    }

    [Fact]
    public void Remove_RemovesEntry()
    {
        _pool.Register(CreateEntry("test"));
        _pool.TryGet(Owner, Repo, "test").Should().NotBeNull();

        _pool.Remove(Owner, Repo, "test");
        _pool.TryGet(Owner, Repo, "test").Should().BeNull();
    }

    [Fact]
    public void GetAll_ReturnsAllEntries()
    {
        _pool.Register(CreateEntry("container-a"));
        _pool.Register(CreateEntry("container-b"));

        var all = _pool.GetAll();
        all.Should().HaveCount(2);
    }

    private static NamedContainerEntry CreateEntry(
        string name,
        string repository = Repo,
        string containerId = "container-123") => new()
        {
            Name = name,
            ContainerId = containerId,
            ClonePath = "/tmp/test-clone",
            Owner = Owner,
            Repository = repository,
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow
        };
}
