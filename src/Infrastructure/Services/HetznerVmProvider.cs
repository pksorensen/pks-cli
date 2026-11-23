using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// <see cref="IVmProvider"/> backed by the Hetzner Cloud API. Start → "poweron"; stop →
/// "shutdown" (ACPI, graceful). <b>Stopping does not stop billing on Hetzner</b> — a server
/// is charged until it is deleted — so the commands that surface stop say so, and destroy
/// deletes directly without a poweroff round-trip. No scheduled-shutdown API exists.
/// </summary>
public class HetznerVmProvider : IVmProvider
{
    private readonly IHetznerCloudService _hetzner;

    public HetznerVmProvider(IHetznerCloudService hetzner)
    {
        _hetzner = hetzner;
    }

    public string ProviderKey => "hetzner";
    public string DisplayName => "Hetzner Cloud";
    public bool SupportsScheduledShutdown => false;

    public Task<bool> IsAuthenticatedAsync() => _hetzner.IsAuthenticatedAsync();

    public async Task<IReadOnlyList<AzureVmRecord>> DiscoverAsync()
    {
        var creds = await _hetzner.GetStoredCredentialsAsync();
        if (creds == null || !creds.Token.HasValue)
            return Array.Empty<AzureVmRecord>();

        var servers = await _hetzner.ListServersAsync();
        return servers.Select(s => ToRecord(s, creds)).ToList();
    }

    /// <summary>Map a live Hetzner server into the shared VM record shape.</summary>
    public static AzureVmRecord ToRecord(HetznerServer s, HetznerStoredCredentials creds)
    {
        var location = string.IsNullOrEmpty(s.LocationName) ? creds.DefaultLocation : s.LocationName;
        return new AzureVmRecord
        {
            Provider = "hetzner",
            VmName = s.Name,
            AdminUsername = "root",
            Zone = location,
            ServerId = s.Id.ToString(),
            ProjectId = creds.ProjectName,
            Location = location,
            PublicIpAddress = s.PublicIpAddress,
            VmSize = s.ServerTypeName,
            OsDiskSizeGb = s.PrimaryDiskSize ?? s.ServerType?.Disk ?? 0,
            IdleShutdownMinutes = 0,
            CreatedAt = DateTime.TryParse(s.Created, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var c) ? c : DateTime.UtcNow
        };
    }

    public async Task<string?> GetStatusAsync(AzureVmRecord record)
    {
        if (!TryId(record, out var id)) return null;
        var raw = await _hetzner.GetServerStatusAsync(id);
        return Normalize(raw);
    }

    public async Task<string?> GetPublicIpAsync(AzureVmRecord record)
    {
        if (!TryId(record, out var id)) return null;
        var server = await _hetzner.GetServerAsync(id);
        return server?.PublicIpAddress;
    }

    public Task StartAsync(AzureVmRecord record) => _hetzner.PerformActionAsync(RequireId(record), "poweron");

    public Task StopAsync(AzureVmRecord record) => _hetzner.PerformActionAsync(RequireId(record), "shutdown");

    public async Task DestroyAsync(AzureVmRecord record, Action<string>? onProgress = null)
    {
        var id = RequireId(record);
        onProgress?.Invoke("Deleting server (billing stops here)...");
        await _hetzner.DeleteServerAsync(id);
    }

    private static bool TryId(AzureVmRecord r, out long id) => long.TryParse(r.ServerId, out id);

    private static long RequireId(AzureVmRecord r)
    {
        if (!TryId(r, out var id))
            throw new InvalidOperationException($"Hetzner record '{r.VmName}' is missing its numeric ServerId.");
        return id;
    }

    // Hetzner: running | initializing | starting | stopping | off | deleting | migrating | rebuilding | unknown
    private static string? Normalize(string? status) => status?.ToLowerInvariant() switch
    {
        null => null,
        "running" => VmPowerState.Running,
        "initializing" => VmPowerState.Starting,
        "starting" => VmPowerState.Starting,
        "stopping" => VmPowerState.Stopping,
        "off" => VmPowerState.Stopped,
        _ => VmPowerState.Unknown
    };
}
