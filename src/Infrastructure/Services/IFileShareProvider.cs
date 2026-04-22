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
    Task<SyncResult> SyncAsync(StorageSyncRequest request, Action<string> progress, CancellationToken ct = default);
}
