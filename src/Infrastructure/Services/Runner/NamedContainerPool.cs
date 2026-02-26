using System.Collections.Concurrent;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Thread-safe pool that tracks named containers for reuse across jobs
/// </summary>
public interface INamedContainerPool
{
    /// <summary>Try to get an existing named container. Returns null if not found.</summary>
    NamedContainerEntry? TryGet(string name);

    /// <summary>Register a new named container after it's created.</summary>
    void Register(NamedContainerEntry entry);

    /// <summary>
    /// Acquire exclusive access to a named container. Returns a disposable that releases the lock.
    /// If another job holds the lock, this blocks until released.
    /// </summary>
    Task<IDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Remove a named container from the pool.</summary>
    void Remove(string name);

    /// <summary>Get all tracked named containers (for status display).</summary>
    IReadOnlyList<NamedContainerEntry> GetAll();
}

public class NamedContainerPool : INamedContainerPool
{
    private readonly ConcurrentDictionary<string, NamedContainerEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public NamedContainerEntry? TryGet(string name)
    {
        return _entries.TryGetValue(name, out var entry) ? entry : null;
    }

    public void Register(NamedContainerEntry entry)
    {
        _entries[entry.Name] = entry;
    }

    public async Task<IDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        // Mark as in use
        if (_entries.TryGetValue(name, out var entry))
        {
            entry.InUse = true;
        }

        return new ContainerLock(this, name, semaphore);
    }

    public void Remove(string name)
    {
        _entries.TryRemove(name, out _);
    }

    public IReadOnlyList<NamedContainerEntry> GetAll()
    {
        return _entries.Values.ToList().AsReadOnly();
    }

    private class ContainerLock : IDisposable
    {
        private readonly NamedContainerPool _pool;
        private readonly string _name;
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public ContainerLock(NamedContainerPool pool, string name, SemaphoreSlim semaphore)
        {
            _pool = pool;
            _name = name;
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_pool._entries.TryGetValue(_name, out var entry))
            {
                entry.InUse = false;
                entry.LastUsedAt = DateTime.UtcNow;
            }

            _semaphore.Release();
        }
    }
}
