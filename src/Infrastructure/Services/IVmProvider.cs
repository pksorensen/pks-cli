using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Normalized VM power states shared across cloud providers. Each provider maps its
/// own vocabulary onto these (e.g. Azure "deallocated" and Scaleway "stopped in place"
/// both become <see cref="Stopped"/>).
/// </summary>
public static class VmPowerState
{
    public const string Running = "running";
    public const string Stopped = "stopped";
    public const string Starting = "starting";
    public const string Stopping = "stopping";
    public const string Unknown = "unknown";
}

/// <summary>
/// Cloud-agnostic VM lifecycle operations. Each provider owns its own credential /
/// token acquisition internally, so callers (the <c>pks vm</c> commands) never plumb
/// provider-specific auth around. The record's <see cref="AzureVmRecord.Provider"/>
/// selects which provider handles it (see <see cref="VmProviderRegistry"/>).
/// </summary>
public interface IVmProvider
{
    /// <summary>Stable key stored in <see cref="AzureVmRecord.Provider"/> (e.g. "azure", "scaleway").</summary>
    string ProviderKey { get; }

    /// <summary>Human-readable name for prompts/tables (e.g. "Azure", "Scaleway").</summary>
    string DisplayName { get; }

    /// <summary>Whether this provider supports server-side scheduled/idle shutdown.</summary>
    bool SupportsScheduledShutdown { get; }

    Task<bool> IsAuthenticatedAsync();

    /// <summary>
    /// Discover VMs that exist in the provider account but may not be tracked locally
    /// (e.g. an instance created in the cloud console). Returns records ready to merge
    /// with the local store. Providers that rely solely on local tracking return empty.
    /// </summary>
    Task<IReadOnlyList<AzureVmRecord>> DiscoverAsync();

    /// <summary>Returns a normalized <see cref="VmPowerState"/>, or null if status could not be read.</summary>
    Task<string?> GetStatusAsync(AzureVmRecord record);

    Task<string?> GetPublicIpAsync(AzureVmRecord record);

    Task StartAsync(AzureVmRecord record);

    /// <summary>Stop/deallocate the VM so it stops billing compute (Azure deallocate / Scaleway poweroff).</summary>
    Task StopAsync(AzureVmRecord record);

    Task DestroyAsync(AzureVmRecord record, Action<string>? onProgress = null);
}
