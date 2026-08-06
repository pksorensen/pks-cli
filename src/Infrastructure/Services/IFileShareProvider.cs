using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Infrastructure.Services;

public interface IFileShareProvider
{
    string ProviderName { get; }
    string ProviderKey { get; }
    Task<bool> IsAuthenticatedAsync();
    Task<bool> AuthenticateAsync(IAnsiConsole console, CancellationToken ct = default);
    Task<IEnumerable<StorageResource>> ListResourcesAsync(CancellationToken ct = default);
    Task<SyncResult> SyncAsync(StorageSyncRequest request, Action<SyncProgressUpdate> progress, CancellationToken ct = default);
    Task<StorageListResult> ListDirectoryAsync(string accountName, string resourceName, StorageListRequest request, CancellationToken ct = default);

    /// <summary>
    /// Resolve the files a delete would touch, share-relative. Read-only: callers show this set to
    /// the human and bind approval to it, so deletion never runs on an unresolved pattern.
    /// </summary>
    Task<IReadOnlyList<StorageFileRef>> EnumerateFilesAsync(
        string accountName, string resourceName, string path, bool recursive, CancellationToken ct = default);

    /// <summary>Delete an explicit list of share-relative file paths. Never expands or globs.</summary>
    Task<StorageDeleteResult> DeleteFilesAsync(
        string accountName, string resourceName, IReadOnlyList<string> paths, CancellationToken ct = default);
}
