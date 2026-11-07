using System.Globalization;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Hetzner Cloud credentials persisted under the "hetzner.auth.credentials" config key.
/// A Hetzner Cloud API token is scoped to ONE project (there is no org/sub-account API),
/// so the token is the project: <see cref="ProjectName"/> is only a human label.
/// </summary>
public class HetznerStoredCredentials
{
    public SecretValue Token { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    /// <summary>Location used by <c>pks vm init</c> unless overridden (e.g. "fsn1", "nbg1", "hel1").</summary>
    public string DefaultLocation { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// --- shared ---

/// <summary>A net/gross price pair; Hetzner serializes amounts as decimal strings.</summary>
public class HetznerAmount
{
    [JsonPropertyName("net")] public string? Net { get; set; }
    [JsonPropertyName("gross")] public string? Gross { get; set; }

    [JsonIgnore] public decimal NetValue => Parse(Net);
    [JsonIgnore] public decimal GrossValue => Parse(Gross);

    internal static decimal Parse(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0m;
}

public class HetznerDeprecation
{
    [JsonPropertyName("announced")] public string? Announced { get; set; }
    [JsonPropertyName("unavailable_after")] public string? UnavailableAfter { get; set; }
}

public class HetznerMeta
{
    [JsonPropertyName("pagination")] public HetznerPagination? Pagination { get; set; }
}

public class HetznerPagination
{
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("next_page")] public int? NextPage { get; set; }
}

// --- GET /locations ---

public class HetznerLocation
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("network_zone")] public string? NetworkZone { get; set; }

    [JsonIgnore] public string DisplayLabel => string.IsNullOrEmpty(City) ? Name : $"{Name} — {City}, {Country}";
}

public class HetznerLocationListResponse
{
    [JsonPropertyName("locations")] public List<HetznerLocation> Locations { get; set; } = new();
    [JsonPropertyName("meta")] public HetznerMeta? Meta { get; set; }
}

// --- GET /server_types ---

public class HetznerServerTypePrice
{
    [JsonPropertyName("location")] public string Location { get; set; } = string.Empty;
    [JsonPropertyName("price_hourly")] public HetznerAmount? PriceHourly { get; set; }
    [JsonPropertyName("price_monthly")] public HetznerAmount? PriceMonthly { get; set; }
    [JsonPropertyName("included_traffic")] public long? IncludedTraffic { get; set; }
    [JsonPropertyName("price_per_tb_traffic")] public HetznerAmount? PricePerTbTraffic { get; set; }

    [JsonIgnore] public decimal HourlyNet => PriceHourly?.NetValue ?? 0m;
    [JsonIgnore] public decimal MonthlyNet => PriceMonthly?.NetValue ?? 0m;
    [JsonIgnore] public double IncludedTrafficTb => (IncludedTraffic ?? 0) / 1024.0 / 1024.0 / 1024.0 / 1024.0;
}

public class HetznerServerTypeLocation
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("recommended")] public bool Recommended { get; set; }
    [JsonPropertyName("deprecation")] public HetznerDeprecation? Deprecation { get; set; }
}

/// <summary>
/// A Hetzner server type (e.g. "cx23"). Prices are per location and are net (excl. VAT),
/// which is what the Hetzner website quotes. The primary IPv4 is NOT in this price —
/// see <see cref="HetznerPricing.Ipv4HourlyNetAt"/>.
/// </summary>
public class HetznerServerType
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("cores")] public int Cores { get; set; }
    /// <summary>RAM in GB (Hetzner reports a float, e.g. 4.0).</summary>
    [JsonPropertyName("memory")] public double MemoryGb { get; set; }
    /// <summary>Local disk in GB.</summary>
    [JsonPropertyName("disk")] public int DiskGb { get; set; }
    [JsonPropertyName("cpu_type")] public string? CpuType { get; set; }
    [JsonPropertyName("architecture")] public string? Architecture { get; set; }
    [JsonPropertyName("storage_type")] public string? StorageType { get; set; }
    [JsonPropertyName("deprecation")] public HetznerDeprecation? Deprecation { get; set; }
    [JsonPropertyName("prices")] public List<HetznerServerTypePrice> Prices { get; set; } = new();
    [JsonPropertyName("locations")] public List<HetznerServerTypeLocation> Locations { get; set; } = new();

    [JsonIgnore] public bool IsDeprecated => Deprecation != null;
    [JsonIgnore] public bool IsArm => string.Equals(Architecture, "arm", StringComparison.OrdinalIgnoreCase);

    public HetznerServerTypePrice? PriceAt(string location) =>
        Prices.FirstOrDefault(p => string.Equals(p.Location, location, StringComparison.OrdinalIgnoreCase));

    public bool IsAvailableAt(string location) =>
        Locations.Any(l => string.Equals(l.Name, location, StringComparison.OrdinalIgnoreCase) && l.Available);

    /// <summary>Selection-list label, Azure-style: name, shape, then the price at the chosen location.</summary>
    public string DisplayLabel(string location)
    {
        var cpu = string.Equals(CpuType, "dedicated", StringComparison.OrdinalIgnoreCase) ? "dedicated " : string.Empty;
        var arch = IsArm ? ", Arm" : string.Empty;
        var shape = $"{Cores} {cpu}vCPU, {MemoryGb:0.#} GB RAM, {DiskGb} GB disk{arch}";
        var p = PriceAt(location);
        var price = p == null
            ? string.Empty
            : string.Format(CultureInfo.InvariantCulture, " — €{0:0.0000}/hr (€{1:0.00}/mo cap)", p.HourlyNet, p.MonthlyNet);
        return $"{Name} ({shape}){price}";
    }
}

public class HetznerServerTypeListResponse
{
    [JsonPropertyName("server_types")] public List<HetznerServerType> ServerTypes { get; set; } = new();
    [JsonPropertyName("meta")] public HetznerMeta? Meta { get; set; }
}

// --- GET /images ---

public class HetznerImage
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    /// <summary>System image name, e.g. "ubuntu-24.04". Null for snapshots (use <see cref="Description"/>).</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("os_flavor")] public string? OsFlavor { get; set; }
    [JsonPropertyName("os_version")] public string? OsVersion { get; set; }
    [JsonPropertyName("architecture")] public string? Architecture { get; set; }
    [JsonPropertyName("rapid_deploy")] public bool RapidDeploy { get; set; }
    [JsonPropertyName("deprecated")] public string? Deprecated { get; set; }

    [JsonIgnore] public string DisplayName => Name ?? Description ?? Id.ToString(CultureInfo.InvariantCulture);
}

public class HetznerImageListResponse
{
    [JsonPropertyName("images")] public List<HetznerImage> Images { get; set; } = new();
    [JsonPropertyName("meta")] public HetznerMeta? Meta { get; set; }
}

// --- GET /servers ---

public class HetznerIpv4
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("blocked")] public bool Blocked { get; set; }
}

public class HetznerPublicNet
{
    [JsonPropertyName("ipv4")] public HetznerIpv4? Ipv4 { get; set; }
    [JsonPropertyName("ipv6")] public HetznerIpv4? Ipv6 { get; set; }
}

public class HetznerServerTypeRef
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("cores")] public int Cores { get; set; }
    [JsonPropertyName("memory")] public double MemoryGb { get; set; }
    [JsonPropertyName("disk")] public int Disk { get; set; }
}

public class HetznerImageRef
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public class HetznerServer
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    /// <summary>Raw status: running | initializing | starting | stopping | off | deleting | migrating | rebuilding | unknown.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("created")] public string? Created { get; set; }
    [JsonPropertyName("public_net")] public HetznerPublicNet? PublicNet { get; set; }
    [JsonPropertyName("server_type")] public HetznerServerTypeRef? ServerType { get; set; }
    [JsonPropertyName("location")] public HetznerLocation? Location { get; set; }
    [JsonPropertyName("image")] public HetznerImageRef? Image { get; set; }
    [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; set; }
    [JsonPropertyName("primary_disk_size")] public int? PrimaryDiskSize { get; set; }

    [JsonIgnore] public string PublicIpAddress => PublicNet?.Ipv4?.Ip ?? string.Empty;
    [JsonIgnore] public string LocationName => Location?.Name ?? string.Empty;
    [JsonIgnore] public string ServerTypeName => ServerType?.Name ?? string.Empty;
}

public class HetznerServerListResponse
{
    [JsonPropertyName("servers")] public List<HetznerServer> Servers { get; set; } = new();
    [JsonPropertyName("meta")] public HetznerMeta? Meta { get; set; }
}

public class HetznerServerSingleResponse
{
    [JsonPropertyName("server")] public HetznerServer? Server { get; set; }
}

public class HetznerAction
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("progress")] public int Progress { get; set; }
    [JsonPropertyName("error")] public HetznerError? Error { get; set; }
}

public class HetznerError
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public class HetznerErrorResponse
{
    [JsonPropertyName("error")] public HetznerError? Error { get; set; }
}

public class HetznerServerCreateResponse
{
    [JsonPropertyName("server")] public HetznerServer? Server { get; set; }
    [JsonPropertyName("action")] public HetznerAction? Action { get; set; }
    [JsonPropertyName("root_password")] public string? RootPassword { get; set; }
}

// --- SSH keys ---

public class HetznerSshKey
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("fingerprint")] public string? Fingerprint { get; set; }
    [JsonPropertyName("public_key")] public string? PublicKey { get; set; }
}

public class HetznerSshKeyListResponse
{
    [JsonPropertyName("ssh_keys")] public List<HetznerSshKey> SshKeys { get; set; } = new();
    [JsonPropertyName("meta")] public HetznerMeta? Meta { get; set; }
}

public class HetznerSshKeySingleResponse
{
    [JsonPropertyName("ssh_key")] public HetznerSshKey? SshKey { get; set; }
}

// --- GET /pricing ---

public class HetznerPrimaryIpPricing
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("prices")] public List<HetznerServerTypePrice> Prices { get; set; } = new();
}

public class HetznerPerGbPricing
{
    [JsonPropertyName("price_per_gb_month")] public HetznerAmount? PricePerGbMonth { get; set; }
}

public class HetznerPricing
{
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("vat_rate")] public string? VatRate { get; set; }
    [JsonPropertyName("primary_ips")] public List<HetznerPrimaryIpPricing> PrimaryIps { get; set; } = new();
    [JsonPropertyName("image")] public HetznerPerGbPricing? Image { get; set; }
    [JsonPropertyName("volume")] public HetznerPerGbPricing? Volume { get; set; }

    private HetznerServerTypePrice? Ipv4At(string location) =>
        PrimaryIps.FirstOrDefault(p => string.Equals(p.Type, "ipv4", StringComparison.OrdinalIgnoreCase))?
            .Prices.FirstOrDefault(p => string.Equals(p.Location, location, StringComparison.OrdinalIgnoreCase));

    /// <summary>Hourly net price of a primary IPv4 at the location — billed on top of the server type, even while the server is off.</summary>
    public decimal? Ipv4HourlyNetAt(string location) => Ipv4At(location)?.HourlyNet;
    public decimal? Ipv4MonthlyNetAt(string location) => Ipv4At(location)?.MonthlyNet;

    /// <summary>Snapshot (image) storage, net €/GB/month — the price of parking a machine's disk while it is deleted.</summary>
    [JsonIgnore] public decimal SnapshotPerGbMonthNet => Image?.PricePerGbMonth?.NetValue ?? 0m;
    /// <summary>Block volume storage, net €/GB/month — a persistent disk that outlives the server.</summary>
    [JsonIgnore] public decimal VolumePerGbMonthNet => Volume?.PricePerGbMonth?.NetValue ?? 0m;
}

public class HetznerPricingResponse
{
    [JsonPropertyName("pricing")] public HetznerPricing? Pricing { get; set; }
}

/// <summary>Cost arithmetic shown in the <c>pks vm init</c> confirmation panel.</summary>
public static class HetznerCost
{
    /// <summary>What N hours a day costs over a 30-day month at an hourly net rate.</summary>
    public static decimal MonthlyForDailyHours(decimal hourlyNet, decimal hoursPerDay) => hourlyNet * hoursPerDay * 30m;
}

// --- POST /servers ---

public class HetznerCreateOptions
{
    public string Name { get; set; } = string.Empty;
    public string ServerType { get; set; } = string.Empty;
    /// <summary>System image name (e.g. "ubuntu-24.04") or a snapshot id.</summary>
    public string Image { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    /// <summary>Ids of SSH keys already uploaded to the project (see <c>EnsureSshKeyAsync</c>). Passing one suppresses the root password email.</summary>
    public long[] SshKeyIds { get; set; } = Array.Empty<long>();
    /// <summary>Also written to root's authorized_keys via cloud-init, so first contact works even if the key id was ignored.</summary>
    public string SshPublicKey { get; set; } = string.Empty;
    public bool InstallDocker { get; set; } = true;
    public Dictionary<string, string> Labels { get; set; } = new() { ["managed-by"] = "pks" };
    /// <summary>Same contract as <see cref="ScalewayCreateOptions.TailscaleUpArgs"/>: read only where cloud-init is assembled.</summary>
    public SecretValue TailscaleUpArgs { get; set; }
}
