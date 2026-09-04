using System.Collections.Concurrent;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Thread-safe pool that tracks named containers for reuse across jobs.
///
/// The container name comes from the runner label a workflow puts in <c>runs-on</c>, and several
/// repositories reuse the same label — so the name alone is not a key. A pool keyed on the name
/// hands whichever repository built the container first to everyone else, and the borrower runs
/// its build inside a workspace and toolchain belonging to another repository. Every lookup is
/// therefore scoped by owner/repository as well.
/// </summary>
public interface INamedContainerPool
{
    /// <summary>Try to get an existing named container for a repository. Returns null if not found.</summary>
    NamedContainerEntry? TryGet(string owner, string repository, string name);

    /// <summary>Register a new named container after it's created.</summary>
    void Register(NamedContainerEntry entry);

    /// <summary>
    /// Acquire exclusive access to a named container. Returns a disposable that releases the lock.
    /// If another job holds the lock, this blocks until released.
    /// </summary>
    Task<IDisposable> AcquireAsync(string owner, string repository, string name, CancellationToken cancellationToken = default);

    /// <summary>Remove a named container from the pool.</summary>
    void Remove(string owner, string repository, string name);

    /// <summary>Get all tracked named containers (for status display).</summary>
    IReadOnlyList<NamedContainerEntry> GetAll();
}

public class NamedContainerPool : INamedContainerPool
{
    private readonly ConcurrentDictionary<string, NamedContainerEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The pool key: the runner label scoped to the repository that owns the container.
    ///
    /// The incident this prevents (2026-08-30): pks-agent-pulse and agentic-live-www both say
    /// <c>runs-on: [self-hosted, devcontainer-runner, agentics-live-www-devcontainer]</c>. After a
    /// daemon restart the pool was empty, a pulse job got there first and built pulse's
    /// devcontainer under that name, and every agentic-live-www job afterwards ran inside it —
    /// where there is no dotnet and no docker. The failure surfaced as
    /// <c>docker: command not found</c> in a step that has always worked.
    /// </summary>
    internal static string KeyFor(string owner, string repository, string name) =>
        $"{owner}/{repository}::{name}";

    public NamedContainerEntry? TryGet(string owner, string repository, string name)
    {
        return _entries.TryGetValue(KeyFor(owner, repository, name), out var entry) ? entry : null;
    }

    public void Register(NamedContainerEntry entry)
    {
        _entries[KeyFor(entry.Owner, entry.Repository, entry.Name)] = entry;
    }

    public async Task<IDisposable> AcquireAsync(string owner, string repository, string name, CancellationToken cancellationToken = default)
    {
        var key = KeyFor(owner, repository, name);
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        // Mark as in use
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.InUse = true;
        }

        return new ContainerLock(this, key, semaphore);
    }

    public void Remove(string owner, string repository, string name)
    {
        _entries.TryRemove(KeyFor(owner, repository, name), out _);
    }

    public IReadOnlyList<NamedContainerEntry> GetAll()
    {
        return _entries.Values.ToList().AsReadOnly();
    }

    private class ContainerLock : IDisposable
    {
        private readonly NamedContainerPool _pool;
        private readonly string _key;
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public ContainerLock(NamedContainerPool pool, string key, SemaphoreSlim semaphore)
        {
            _pool = pool;
            _key = key;
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_pool._entries.TryGetValue(_key, out var entry))
            {
                entry.InUse = false;
                entry.LastUsedAt = DateTime.UtcNow;
            }

            _semaphore.Release();
        }
    }
}
