using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Models;

public class AzureVmCreateOptions
{
    public string AccessToken { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceGroupName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string VmName { get; set; } = string.Empty;
    public string VmSize { get; set; } = "Standard_B2s";
    public string AdminUsername { get; set; } = "azureuser";
    public string SshPublicKey { get; set; } = string.Empty;
    public bool InstallDocker { get; set; } = true;
    public int IdleShutdownMinutes { get; set; } = 60;
    public string? ScheduledShutdownUtc { get; set; }
    public int OsDiskSizeGb { get; set; } = 128;
}

public class AzureVmRecord
{
    /// <summary>
    /// Cloud provider that owns this VM. Determines which <c>IVmProvider</c> handles
    /// lifecycle operations. Legacy records (written before multi-cloud support) lack
    /// this field, so it defaults to "azure".
    /// </summary>
    public string Provider { get; set; } = "azure";

    public string VmName { get; set; } = string.Empty;

    /// <summary>SSH username for this VM. Azure images use "azureuser"; Scaleway images use "root".</summary>
    public string AdminUsername { get; set; } = "azureuser";

    // --- Azure identifiers ---
    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;

    // --- Scaleway identifiers ---
    /// <summary>Scaleway availability zone (e.g. "fr-par-2"). Null for Azure records.</summary>
    public string? Zone { get; set; }
    /// <summary>Scaleway server (instance) id. Null for Azure records.</summary>
    public string? ServerId { get; set; }
    /// <summary>Scaleway project id this server belongs to. Null for Azure records.</summary>
    public string? ProjectId { get; set; }

    // --- Common ---
    public string Location { get; set; } = string.Empty;
    public string PublicIpAddress { get; set; } = string.Empty;
    public string SshKeyPath { get; set; } = string.Empty;
    public string VmSize { get; set; } = string.Empty;
    public int IdleShutdownMinutes { get; set; } = 60;
    public string? ScheduledShutdownUtc { get; set; }
    public int OsDiskSizeGb { get; set; } = 128;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AzureVmStore
{
    public List<AzureVmRecord> Vms { get; set; } = new();
}

public class AzureVmInfo
{
    public string VmName { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string VmSize { get; set; } = string.Empty;
    public string PublicIpAddress { get; set; } = string.Empty;
    public string AdminUsername { get; set; } = string.Empty;
    public string SshKeyPath { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string ProvisioningState { get; set; } = string.Empty;
}

public class AzureResourceGroup
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("location")] public string Location { get; set; } = string.Empty;
}

public class AzureResourceGroupListResponse
{
    [JsonPropertyName("value")] public List<AzureResourceGroup> Value { get; set; } = new();
}

public class AzureVmSizeInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("numberOfCores")] public int NumberOfCores { get; set; }
    [JsonPropertyName("memoryInMB")] public int MemoryInMB { get; set; }
    public decimal? PricePerHour { get; set; }

    public string DisplayLabel =>
        PricePerHour.HasValue
            ? $"{Name} ({NumberOfCores} vCPU, {MemoryInMB / 1024.0:0.#} GB RAM) — ${PricePerHour.Value:0.000}/hr"
            : $"{Name} ({NumberOfCores} vCPU, {MemoryInMB / 1024.0:0.#} GB RAM)";
}

public class AzureVmSizeListResponse
{
    [JsonPropertyName("value")] public List<AzureVmSizeInfo> Value { get; set; } = new();
}
