using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// <see cref="IVmProvider"/> backed by the existing Azure ARM stack
/// (<see cref="IAzureAuthService"/> + <see cref="IAzureVmService"/>). Acquires a
/// management token on each call so commands no longer thread a token around.
/// </summary>
public class AzureVmProvider : IVmProvider
{
    private const string ManagementScope = "https://management.azure.com/.default";

    private readonly IAzureAuthService _auth;
    private readonly IAzureVmService _vm;

    public AzureVmProvider(IAzureAuthService auth, IAzureVmService vm)
    {
        _auth = auth;
        _vm = vm;
    }

    public string ProviderKey => "azure";
    public string DisplayName => "Azure";
    public bool SupportsScheduledShutdown => true;

    public Task<bool> IsAuthenticatedAsync() => _auth.IsAuthenticatedAsync();

    // Azure VMs are tracked via the local metadata store; no live discovery here.
    public Task<IReadOnlyList<AzureVmRecord>> DiscoverAsync() =>
        Task.FromResult<IReadOnlyList<AzureVmRecord>>(Array.Empty<AzureVmRecord>());

    public async Task<string?> GetStatusAsync(AzureVmRecord record)
    {
        var token = await GetTokenAsync();
        if (token == null) return null;
        var raw = await _vm.GetVmStatusAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName);
        return Normalize(raw);
    }

    public async Task<string?> GetPublicIpAsync(AzureVmRecord record)
    {
        var token = await GetTokenAsync();
        if (token == null) return null;
        return await _vm.GetVmPublicIpAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName);
    }

    public async Task StartAsync(AzureVmRecord record)
    {
        var token = await RequireTokenAsync();
        await _vm.StartVmAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName);
    }

    public async Task StopAsync(AzureVmRecord record)
    {
        var token = await RequireTokenAsync();
        await _vm.DeallocateVmAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName);
    }

    public async Task DestroyAsync(AzureVmRecord record, Action<string>? onProgress = null)
    {
        var token = await RequireTokenAsync();
        await _vm.DestroyVmAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName, onProgress);
    }

    private Task<string?> GetTokenAsync() => _auth.GetAccessTokenAsync(ManagementScope);

    private async Task<string> RequireTokenAsync()
    {
        var token = await GetTokenAsync();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Failed to obtain an Azure management token. Run 'pks azure init' first.");
        return token;
    }

    private static string? Normalize(string? azureState) => azureState switch
    {
        null => null,
        "running" => VmPowerState.Running,
        "starting" => VmPowerState.Starting,
        "stopping" => VmPowerState.Stopping,
        "deallocating" => VmPowerState.Stopping,
        "stopped" => VmPowerState.Stopped,
        "deallocated" => VmPowerState.Stopped,
        _ => VmPowerState.Unknown
    };
}
