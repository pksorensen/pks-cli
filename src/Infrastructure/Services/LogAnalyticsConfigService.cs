using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

public interface ILogAnalyticsConfigService
{
    Task<bool> IsConfiguredAsync();
    Task<LogAnalyticsConfig?> GetConfigAsync();
    Task StoreConfigAsync(string workspaceId, string? workspaceName, string? resourceId, string? subscriptionId);
    Task ClearConfigAsync();
}

/// <summary>
/// Single-workspace facade over <see cref="IAzureResourceRegistry"/> for the callers that still
/// think in terms of "the" workspace (<c>KustoCommand</c>, <c>LogAnalyticsQueryService</c>): the
/// first enabled <see cref="AzureResourceKind.LogAnalytics"/> entry is the configured one.
/// </summary>
public class LogAnalyticsConfigService : ILogAnalyticsConfigService
{
    private readonly IAzureResourceRegistry _registry;

    public LogAnalyticsConfigService(IAzureResourceRegistry registry)
    {
        _registry = registry;
    }

    public async Task<bool> IsConfiguredAsync()
        => (await _registry.ListEnabledAsync(AzureResourceKind.LogAnalytics)).Count > 0;

    public async Task<LogAnalyticsConfig?> GetConfigAsync()
    {
        var entry = (await _registry.ListEnabledAsync(AzureResourceKind.LogAnalytics)).FirstOrDefault();
        if (entry == null)
            return null;

        return new LogAnalyticsConfig
        {
            WorkspaceId = entry.Key,
            WorkspaceName = entry.Name,
            ResourceId = entry.ResourceId,
            SubscriptionId = entry.SubscriptionId,
            RegisteredAt = entry.DiscoveredAt
        };
    }

    public async Task StoreConfigAsync(string workspaceId, string? workspaceName, string? resourceId, string? subscriptionId)
    {
        await _registry.UpsertAsync(new[]
        {
            new AzureResourceEntry
            {
                Kind = AzureResourceKind.LogAnalytics,
                Key = workspaceId,
                Name = string.IsNullOrWhiteSpace(workspaceName) ? workspaceId : workspaceName,
                ResourceId = string.IsNullOrWhiteSpace(resourceId) ? null : resourceId,
                SubscriptionId = string.IsNullOrWhiteSpace(subscriptionId) ? null : subscriptionId,
                Enabled = true,
                DiscoveredAt = DateTime.UtcNow
            }
        });
        // Upsert preserves a user's "off" — but registering is an explicit "on".
        await _registry.SetEnabledAsync(AzureResourceKind.LogAnalytics, workspaceId, true);
    }

    public async Task ClearConfigAsync()
    {
        foreach (var entry in await _registry.ListAsync(AzureResourceKind.LogAnalytics))
            await _registry.RemoveAsync(AzureResourceKind.LogAnalytics, entry.Key);
    }
}
