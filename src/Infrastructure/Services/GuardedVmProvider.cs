using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Decorates an <see cref="IVmProvider"/> so billable power operations require a second factor
/// (per the action policy) before they run; read-only operations pass straight through. Because
/// every VM command resolves its provider through <see cref="VmProviderRegistry"/>, wrapping here
/// is the single shared choke-point that covers <c>vm start</c>, the <c>vm status</c> menu,
/// <c>vm destroy</c>, <c>vm tailscale</c>, and the silent auto-start inside
/// <see cref="Commands.Vm.VmConnection.EnsureReachableAsync"/>.
/// </summary>
public sealed class GuardedVmProvider : IVmProvider
{
    private readonly IVmProvider _inner;
    private readonly IActionGuard _guard;

    public GuardedVmProvider(IVmProvider inner, IActionGuard guard)
    {
        _inner = inner;
        _guard = guard;
    }

    public string ProviderKey => _inner.ProviderKey;
    public string DisplayName => _inner.DisplayName;
    public bool SupportsScheduledShutdown => _inner.SupportsScheduledShutdown;

    public Task<bool> IsAuthenticatedAsync() => _inner.IsAuthenticatedAsync();
    public Task<IReadOnlyList<AzureVmRecord>> DiscoverAsync() => _inner.DiscoverAsync();
    public Task<string?> GetStatusAsync(AzureVmRecord record) => _inner.GetStatusAsync(record);
    public Task<string?> GetPublicIpAsync(AzureVmRecord record) => _inner.GetPublicIpAsync(record);

    public async Task StartAsync(AzureVmRecord record)
    {
        await _guard.RequireAsync(new ActionRequest(
            ActionIds.VmStart,
            $"Start VM '{record.VmName}' on {_inner.DisplayName}",
            "This resumes compute/GPU billing."));
        await _inner.StartAsync(record);
    }

    public async Task StopAsync(AzureVmRecord record)
    {
        await _guard.RequireAsync(new ActionRequest(
            ActionIds.VmStop,
            $"Stop VM '{record.VmName}' on {_inner.DisplayName}"));
        await _inner.StopAsync(record);
    }

    public async Task DestroyAsync(AzureVmRecord record, Action<string>? onProgress = null)
    {
        await _guard.RequireAsync(new ActionRequest(
            ActionIds.VmDestroy,
            $"Destroy VM '{record.VmName}' on {_inner.DisplayName}",
            "This permanently deletes the VM and all its resources."));
        await _inner.DestroyAsync(record, onProgress);
    }
}
