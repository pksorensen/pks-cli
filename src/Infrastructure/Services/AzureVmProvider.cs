using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Infrastructure.Services;

/// <summary>
/// <see cref="IVmProvider"/> backed by the existing Azure ARM stack
/// (<see cref="IAzureAuthService"/> + <see cref="IAzureVmService"/>). Acquires a
/// management token on each call so commands no longer thread a token around.
/// Start, stop and destroy offer PIM activation on a 403 when a console is available.
/// </summary>
public class AzureVmProvider : IVmProvider
{
    private const string ManagementScope = "https://management.azure.com/.default";

    private readonly IAzureAuthService _auth;
    private readonly IAzureVmService _vm;
    private readonly IAnsiConsole? _console;
    private readonly IHttpClientFactory? _httpClientFactory;

    public AzureVmProvider(IAzureAuthService auth, IAzureVmService vm,
        IAnsiConsole? console = null, IHttpClientFactory? httpClientFactory = null)
    {
        _auth = auth;
        _vm = vm;
        _console = console;
        _httpClientFactory = httpClientFactory;
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
        await WithPimAsync(token, record, $"start VM {record.VmName}",
            () => _vm.StartVmAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName));
    }

    public async Task StopAsync(AzureVmRecord record)
    {
        var token = await RequireTokenAsync();
        await WithPimAsync(token, record, $"stop VM {record.VmName}",
            () => _vm.DeallocateVmAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName));
    }

    public async Task DestroyAsync(AzureVmRecord record, Action<string>? onProgress = null)
    {
        var token = await RequireTokenAsync();
        // Safe to repeat: deletes treat 404 as done, and leftover OS disks are found by name.
        await WithPimAsync(token, record, $"destroy VM {record.VmName}",
            () => _vm.DestroyVmAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName, onProgress));
    }

    private Task WithPimAsync(string token, AzureVmRecord record, string purpose, Func<Task> operation)
        => _console == null || _httpClientFactory == null
            ? operation()
            : AzurePimRetry.RunAsync(operation, _console, _httpClientFactory.CreateClient(), token, record.SubscriptionId, purpose);

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
