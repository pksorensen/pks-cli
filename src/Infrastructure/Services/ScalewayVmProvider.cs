using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// <see cref="IVmProvider"/> backed by the Scaleway Instance API. Power operations map
/// to instance actions: start → "poweron", stop → "poweroff" (releases compute/GPU
/// billing; safe for block-storage-backed instances such as H100). Destroy deletes the
/// server. Scaleway has no native scheduled-shutdown API, so that capability is off.
/// </summary>
public class ScalewayVmProvider : IVmProvider
{
    private readonly IScalewayService _scaleway;

    public ScalewayVmProvider(IScalewayService scaleway)
    {
        _scaleway = scaleway;
    }

    public string ProviderKey => "scaleway";
    public string DisplayName => "Scaleway";
    public bool SupportsScheduledShutdown => false;

    public Task<bool> IsAuthenticatedAsync() => _scaleway.IsAuthenticatedAsync();

    public async Task<IReadOnlyList<AzureVmRecord>> DiscoverAsync()
    {
        var creds = await _scaleway.GetStoredCredentialsAsync();
        if (creds == null || string.IsNullOrEmpty(creds.DefaultZone))
            return Array.Empty<AzureVmRecord>();

        var servers = await _scaleway.ListServersAsync(creds.DefaultZone, creds.DefaultProjectId);
        return servers.Select(s => ToRecord(s, creds)).ToList();
    }

    /// <summary>Map a live Scaleway server into the shared VM record shape.</summary>
    public static AzureVmRecord ToRecord(ScalewayServer s, ScalewayStoredCredentials creds) => new()
    {
        Provider = "scaleway",
        VmName = s.Name,
        AdminUsername = "root",
        Zone = s.Zone ?? creds.DefaultZone,
        ServerId = s.Id,
        ProjectId = s.Project ?? creds.DefaultProjectId,
        Location = s.Zone ?? creds.DefaultZone,
        PublicIpAddress = s.PublicIpAddress,
        VmSize = s.CommercialType,
        IdleShutdownMinutes = 0,
        CreatedAt = DateTime.UtcNow
    };

    public async Task<string?> GetStatusAsync(AzureVmRecord record)
    {
        if (!HasIds(record)) return null;
        var raw = await _scaleway.GetServerStateAsync(record.Zone!, record.ServerId!);
        return Normalize(raw);
    }

    public async Task<string?> GetPublicIpAsync(AzureVmRecord record)
    {
        if (!HasIds(record)) return null;
        var server = await _scaleway.GetServerAsync(record.Zone!, record.ServerId!);
        return server?.PublicIpAddress;
    }

    public Task StartAsync(AzureVmRecord record)
    {
        RequireIds(record);
        return _scaleway.PerformActionAsync(record.Zone!, record.ServerId!, "poweron");
    }

    public Task StopAsync(AzureVmRecord record)
    {
        RequireIds(record);
        return _scaleway.PerformActionAsync(record.Zone!, record.ServerId!, "poweroff");
    }

    public async Task DestroyAsync(AzureVmRecord record, Action<string>? onProgress = null)
    {
        RequireIds(record);
        // Power off first so the server can be deleted cleanly, then delete it.
        var state = await _scaleway.GetServerStateAsync(record.Zone!, record.ServerId!);
        if (!string.Equals(state, "stopped", StringComparison.OrdinalIgnoreCase))
        {
            onProgress?.Invoke("Powering off...");
            try { await _scaleway.PerformActionAsync(record.Zone!, record.ServerId!, "poweroff"); } catch { }
        }
        onProgress?.Invoke("Deleting server...");
        await _scaleway.DeleteServerAsync(record.Zone!, record.ServerId!);
    }

    private static bool HasIds(AzureVmRecord r) =>
        !string.IsNullOrEmpty(r.Zone) && !string.IsNullOrEmpty(r.ServerId);

    private static void RequireIds(AzureVmRecord r)
    {
        if (!HasIds(r))
            throw new InvalidOperationException($"Scaleway record '{r.VmName}' is missing Zone/ServerId.");
    }

    // Scaleway: running | stopped | stopped in place | starting | stopping | locked
    private static string? Normalize(string? state) => state?.ToLowerInvariant() switch
    {
        null => null,
        "running" => VmPowerState.Running,
        "starting" => VmPowerState.Starting,
        "stopping" => VmPowerState.Stopping,
        "stopped" => VmPowerState.Stopped,
        "stopped in place" => VmPowerState.Stopped,
        _ => VmPowerState.Unknown
    };
}
