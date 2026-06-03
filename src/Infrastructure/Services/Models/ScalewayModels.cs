using System.Linq;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Scaleway API credentials persisted under the "scaleway.auth.credentials" config key.
/// Scaleway uses static API keys (no OAuth): the secret key is sent as the
/// <c>X-Auth-Token</c> header on every request.
/// </summary>
public class ScalewayStoredCredentials
{
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string DefaultProjectId { get; set; } = string.Empty;
    public string DefaultProjectName { get; set; } = string.Empty;
    public string DefaultZone { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// --- IAM: api-key lookup (GET /iam/v1alpha1/api-keys/{access-key}) ---

public class ScalewayApiKeyInfo
{
    [JsonPropertyName("access_key")] public string AccessKey { get; set; } = string.Empty;
    [JsonPropertyName("default_project_id")] public string DefaultProjectId { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
}

// --- Account v3: projects ---

public class ScalewayProject
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("organization_id")] public string OrganizationId { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public class ScalewayProjectListResponse
{
    [JsonPropertyName("projects")] public List<ScalewayProject> Projects { get; set; } = new();
    [JsonPropertyName("total_count")] public int TotalCount { get; set; }
}

// --- Instance API: servers ---

public class ScalewayPublicIp
{
    [JsonPropertyName("address")] public string? Address { get; set; }
    /// <summary>"inet" for IPv4, "inet6" for IPv6.</summary>
    [JsonPropertyName("family")] public string? Family { get; set; }

    [JsonIgnore] public bool IsV4 => string.Equals(Family, "inet", StringComparison.OrdinalIgnoreCase);
}

public class ScalewayServer
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("commercial_type")] public string CommercialType { get; set; } = string.Empty;

    /// <summary>Raw Scaleway state: running | stopped | stopped in place | starting | stopping | locked.</summary>
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;

    /// <summary>Deprecated single public IP (often the IPv6 one). Prefer <see cref="PublicIps"/>.</summary>
    [JsonPropertyName("public_ip")] public ScalewayPublicIp? PublicIp { get; set; }
    /// <summary>All attached flexible IPs (IPv4 + IPv6). A VM you attached an IPv4 to has it here.</summary>
    [JsonPropertyName("public_ips")] public List<ScalewayPublicIp>? PublicIps { get; set; }
    [JsonPropertyName("zone")] public string? Zone { get; set; }
    [JsonPropertyName("project")] public string? Project { get; set; }
    [JsonPropertyName("image")] public ScalewayImageRef? Image { get; set; }

    /// <summary>Best public address for SSH: prefer an IPv4 flexible IP, then any flexible IP, then the legacy single IP.</summary>
    [JsonIgnore]
    public string PublicIpAddress
    {
        get
        {
            if (PublicIps is { Count: > 0 })
            {
                var v4 = PublicIps.FirstOrDefault(p => p.IsV4 && !string.IsNullOrEmpty(p.Address));
                if (v4 != null) return v4.Address!;
                var any = PublicIps.FirstOrDefault(p => !string.IsNullOrEmpty(p.Address));
                if (any != null) return any.Address!;
            }
            return PublicIp?.Address ?? string.Empty;
        }
    }
}

public class ScalewayImageRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; set; }
}

// --- Instance API: images (GET /instance/v1/zones/{zone}/images) ---

public class ScalewayImage
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("arch")] public string? Arch { get; set; }
}

public class ScalewayImageListResponse
{
    [JsonPropertyName("images")] public List<ScalewayImage> Images { get; set; } = new();
}

public class ScalewayServerSingleResponse
{
    [JsonPropertyName("server")] public ScalewayServer? Server { get; set; }
}

public class ScalewayServerListResponse
{
    [JsonPropertyName("servers")] public List<ScalewayServer> Servers { get; set; } = new();
}

// --- Instance API: server types (GET /instance/v1/zones/{zone}/products/servers) ---

/// <summary>
/// A commercial instance type (e.g. "H100-1-80G"). The products endpoint returns a
/// map keyed by type name; <see cref="Name"/> is filled in from that key.
/// </summary>
public class ScalewayServerType
{
    [JsonIgnore] public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ncpus")] public int Ncpus { get; set; }
    [JsonPropertyName("ram")] public long RamBytes { get; set; }
    [JsonPropertyName("gpu")] public int? Gpu { get; set; }
    [JsonPropertyName("gpu_info")] public ScalewayGpuInfo? GpuInfo { get; set; }
    [JsonPropertyName("arch")] public string? Arch { get; set; }
    [JsonPropertyName("hourly_price")] public decimal? HourlyPrice { get; set; }

    [JsonIgnore] public bool IsGpu => (Gpu ?? 0) > 0;
    [JsonIgnore] public double RamGb => RamBytes / 1024.0 / 1024.0 / 1024.0;

    [JsonIgnore]
    public string DisplayLabel
    {
        get
        {
            var gpuPart = IsGpu
                ? $"{Gpu}× {GpuInfo?.Name ?? "GPU"}, "
                : string.Empty;
            var pricePart = HourlyPrice.HasValue ? $" — €{HourlyPrice.Value:0.00}/hr" : string.Empty;
            return $"{Name} ({gpuPart}{Ncpus} vCPU, {RamGb:0.#} GB RAM){pricePart}";
        }
    }
}

public class ScalewayGpuInfo
{
    [JsonPropertyName("gpu_manufacturer")] public string? Manufacturer { get; set; }
    [JsonPropertyName("gpu_name")] public string? Name { get; set; }
    [JsonPropertyName("gpu_memory")] public long? MemoryBytes { get; set; }
}

/// <summary>
/// The products/servers endpoint shape: { "servers": { "H100-1-80G": {...}, ... } }.
/// </summary>
public class ScalewayServerTypesResponse
{
    [JsonPropertyName("servers")] public Dictionary<string, ScalewayServerType> Servers { get; set; } = new();
}

// --- Instance API: create server ---

public class ScalewayCreateOptions
{
    public string Zone { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CommercialType { get; set; } = string.Empty;

    /// <summary>Marketplace image label or id (e.g. "ubuntu_jammy"). Resolved to volumes by Scaleway.</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>SSH public key to authorize via cloud-init user-data (so our key works at first boot).</summary>
    public string SshPublicKey { get; set; } = string.Empty;

    /// <summary>Whether to assign a routed/dynamic public IP.</summary>
    public bool EnablePublicIp { get; set; } = true;

    public string[] Tags { get; set; } = Array.Empty<string>();
}
