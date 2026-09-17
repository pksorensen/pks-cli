using System.Collections.Concurrent;
using System.Text.Json;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Azure;

/// <summary>
/// Two-level access-token cache keyed on <c>(key, scope)</c> — the generalisation of the cache
/// that used to live inside <c>FoundryTokenCredential</c>. <c>key</c> is whatever identifies the
/// refresh token behind the access token: <c>"foundry"</c> for the Foundry credential, a tenant id
/// for the tenant store.
///
/// L1 is in-memory per instance; <see cref="Default"/> is the process-wide instance, so the
/// <c>new FoundryTokenCredential(auth)</c> call sites keep sharing one L1 exactly as they did when
/// the fields were static. L2 is one JSON object on disk (<c>~/.pks-cli/azure-token-cache.json</c>,
/// entries keyed <c>"{key}|{scope}"</c>) guarded by a cross-process file lock, so a fan-out of
/// short-lived <c>pks</c> processes redeems a refresh token once per <see cref="CacheLifetime"/>
/// instead of once per process. AAD rotates refresh tokens on redemption, so N concurrent
/// redemptions of the same token would leave N-1 processes with <c>invalid_grant</c> and a racing
/// write-back of the rotated token; collapsing them into one is the whole reason L2 exists.
///
/// The refresh callback runs while the cross-process lock is held. It may take other locks (the
/// tenant store's list lock, say) but nothing that holds those locks may call back into this cache.
/// Callback exceptions propagate unchanged.
/// </summary>
public sealed class AzureTokenCache
{
    /// <summary>
    /// Cache duration cap. AAD access tokens live ~1 hour; capping at 50 min leaves a 10-min margin
    /// for clock skew and slow downstream calls even when the STS reports a full hour.
    /// </summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(50);

    /// <summary>Skew before a cached token counts as stale, so no caller gets a token that expires mid-call.</summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for the cross-process refresh lock before giving up.</summary>
    public static readonly TimeSpan CrossProcessLockTimeout = TimeSpan.FromSeconds(90);

    public const string DefaultFileName = "azure-token-cache.json";
    public const string DefaultLockName = "azure-token-cache.lock";

    private static readonly Lazy<AzureTokenCache> s_default = new(() => new AzureTokenCache(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli")));

    /// <summary>The process-wide instance backed by <c>~/.pks-cli</c>. DI registers this same
    /// instance so services and the hand-constructed <c>FoundryTokenCredential</c>s share one L1.</summary>
    public static AzureTokenCache Default => s_default.Value;

    private readonly string _directory;
    private readonly string _cachePath;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedToken> _memory = new(StringComparer.Ordinal);

    private sealed record CachedToken(string Token, DateTimeOffset Expiry);

    private sealed class DiskEntry
    {
        public string? Token { get; set; }
        public DateTimeOffset Expiry { get; set; }
    }

    public AzureTokenCache(string directory, string fileName = DefaultFileName, string lockName = DefaultLockName)
    {
        _directory = directory;
        _cachePath = Path.Combine(directory, fileName);
        _lockPath = Path.Combine(directory, lockName);
    }

    /// <summary>
    /// Returns a fresh access token for <paramref name="key"/>/<paramref name="scope"/>, invoking
    /// <paramref name="refresh"/> at most once per lifetime across every caller and process that
    /// shares the same directory. The stored expiry is the earlier of what the callback reports and
    /// now + <see cref="CacheLifetime"/>.
    /// </summary>
    public async Task<string> GetOrRefreshAsync(
        string key,
        string scope,
        Func<CancellationToken, Task<(string Token, DateTimeOffset ExpiresOn)>> refresh,
        CancellationToken ct)
        => (await GetOrRefreshWithExpiryAsync(key, scope, refresh, ct)).Token;

    /// <summary>Same as <see cref="GetOrRefreshAsync"/> but also returns the cached expiry, for
    /// <c>TokenCredential</c> adapters that must report it to the Azure SDK.</summary>
    public async Task<(string Token, DateTimeOffset ExpiresOn)> GetOrRefreshWithExpiryAsync(
        string key,
        string scope,
        Func<CancellationToken, Task<(string Token, DateTimeOffset ExpiresOn)>> refresh,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var entryKey = EntryKey(key, scope);

        // L1 fast path: in-memory, no IO.
        if (TryReadMemory(entryKey, out var cached)) return (cached.Token, cached.Expiry);

        // L2 fast path: disk cache shared across processes, no lock for a read.
        if (TryReadDisk(entryKey, out cached))
        {
            _memory[entryKey] = cached;
            return (cached.Token, cached.Expiry);
        }

        // Slow path. Serialize within the process first (L1), then across processes (L2 file
        // lock), re-checking the cache at each gate so only one refresh happens per lifetime
        // regardless of fan-out.
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (TryReadMemory(entryKey, out cached)) return (cached.Token, cached.Expiry);
            if (TryReadDisk(entryKey, out cached)) { _memory[entryKey] = cached; return (cached.Token, cached.Expiry); }

            using var crossProcessLock = await AcquireCrossProcessLockAsync(ct);

            // Another process may have refreshed while we waited for the lock.
            if (TryReadDisk(entryKey, out cached)) { _memory[entryKey] = cached; return (cached.Token, cached.Expiry); }

            var (token, expiresOn) = await refresh(ct);
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException($"Token refresh for '{key}' returned no access token.");

            var cap = DateTimeOffset.UtcNow + CacheLifetime;
            var entry = new CachedToken(token, expiresOn < cap ? expiresOn : cap);
            _memory[entryKey] = entry;
            WriteDisk(entryKey, entry);
            return (entry.Token, entry.Expiry);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Drops the cached token for one (key, scope) from memory and disk — for callers that
    /// learn a token is dead (401) before its expiry.</summary>
    public void Invalidate(string key, string scope)
    {
        var entryKey = EntryKey(key, scope);
        _memory.TryRemove(entryKey, out _);
        try
        {
            using var crossProcessLock = AcquireCrossProcessLockAsync(CancellationToken.None).GetAwaiter().GetResult();
            var all = ReadDiskAll();
            if (all.Remove(entryKey)) WriteDiskAll(all);
        }
        catch
        {
            // Best effort; the entry expires on its own.
        }
    }

    private static string EntryKey(string key, string scope) => key + "|" + scope;

    private static bool IsFresh(DateTimeOffset expiry) => expiry - DateTimeOffset.UtcNow > RefreshSkew;

    private bool TryReadMemory(string entryKey, out CachedToken token)
    {
        if (_memory.TryGetValue(entryKey, out var t) && !string.IsNullOrEmpty(t.Token) && IsFresh(t.Expiry))
        {
            token = t;
            return true;
        }
        token = null!;
        return false;
    }

    private bool TryReadDisk(string entryKey, out CachedToken token)
    {
        token = null!;
        try
        {
            var all = ReadDiskAll();
            if (!all.TryGetValue(entryKey, out var dt) || dt is null || string.IsNullOrEmpty(dt.Token)) return false;
            if (!IsFresh(dt.Expiry)) return false;
            token = new CachedToken(dt.Token!, dt.Expiry);
            return true;
        }
        catch
        {
            // Missing/torn/garbage cache — treat as a miss and refresh.
            return false;
        }
    }

    private Dictionary<string, DiskEntry> ReadDiskAll()
    {
        if (!File.Exists(_cachePath)) return new Dictionary<string, DiskEntry>(StringComparer.Ordinal);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, DiskEntry>>(File.ReadAllText(_cachePath));
            return parsed is null
                ? new Dictionary<string, DiskEntry>(StringComparer.Ordinal)
                : new Dictionary<string, DiskEntry>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, DiskEntry>(StringComparer.Ordinal);
        }
    }

    private void WriteDisk(string entryKey, CachedToken entry)
    {
        try
        {
            // Read-modify-write of the whole object; the caller holds the cross-process lock, so
            // no other writer can interleave. Entries for other keys survive.
            var all = ReadDiskAll();
            all[entryKey] = new DiskEntry { Token = entry.Token, Expiry = entry.Expiry };
            var now = DateTimeOffset.UtcNow;
            foreach (var stale in all.Where(kv => kv.Value.Expiry <= now).Select(kv => kv.Key).ToList())
                all.Remove(stale);
            WriteDiskAll(all);
        }
        catch
        {
            // A cache write failure is non-fatal: L1 still serves this process.
        }
    }

    private void WriteDiskAll(Dictionary<string, DiskEntry> all)
    {
        Directory.CreateDirectory(_directory);
        // Atomic write so lock-free readers never see a half-written file.
        var tmp = _cachePath + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all));
        // This file holds live bearer tokens: owner-only, never the default 0644.
        SecurityFiles.Restrict(tmp);
        File.Move(tmp, _cachePath, overwrite: true);
        SecurityFiles.Restrict(_cachePath);
    }

    private async Task<FileStream> AcquireCrossProcessLockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var deadline = DateTimeOffset.UtcNow + CrossProcessLockTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // FileShare.None makes this an OS-level mutex across processes.
                var stream = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try { SecurityFiles.Restrict(_lockPath); } catch { }
                return stream;
            }
            catch (IOException)
            {
                if (DateTimeOffset.UtcNow > deadline)
                    throw new TimeoutException("Timed out waiting for the Azure token refresh lock.");
                await Task.Delay(150, ct);
            }
        }
    }
}
