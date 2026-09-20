using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Azure;

/// <summary>The verticals that register Azure resources: <c>pks loganalytics</c>, <c>pks appinsights</c>,
/// <c>pks fileshare</c>/<c>storage</c> and <c>pks acs</c> (Communication Services SMS senders).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AzureResourceKind
{
    LogAnalytics,
    AppInsights,
    Storage,

    /// <summary>One entry per SMS <em>sender</em> — a phone number or an alphanumeric sender id —
    /// on an Azure Communication Services resource, not one per resource.</summary>
    CommunicationServices
}

/// <summary>
/// One known Azure resource. Non-secret by construction: nothing in here lets its holder
/// authenticate, so the file it lives in (<c>~/.pks-cli/azure-resources.json</c>) is safe to print.
/// </summary>
public sealed class AzureResourceEntry
{
    public AzureResourceKind Kind { get; set; }

    /// <summary><c>null</c> = unknown (entries lifted from the legacy single-resource keys never
    /// recorded one); consumers fall back to the single known tenant.</summary>
    public string? TenantId { get; set; }

    public string? SubscriptionId { get; set; }
    public string? SubscriptionName { get; set; }

    /// <summary>Display name: workspace name, App Insights resource name or storage account name.</summary>
    public string Name { get; set; } = "";

    /// <summary>ARM resource id when known.</summary>
    public string? ResourceId { get; set; }

    /// <summary>What consumers query with: the workspace customerId GUID, the App Insights appId,
    /// or the storage account name.</summary>
    public string Key { get; set; } = "";

    public string? ResourceGroup { get; set; }
    public bool Enabled { get; set; }
    public DateTime DiscoveredAt { get; set; }

    /// <summary>Data-plane host for kinds that have one (<see cref="AzureResourceKind.CommunicationServices"/>:
    /// the resource's <c>hostName</c>, e.g. <c>my-acs.europe.communication.azure.com</c>). Not a
    /// secret. <c>null</c> for the other kinds.</summary>
    public string? Endpoint { get; set; }
}
