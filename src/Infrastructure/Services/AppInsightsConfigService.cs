using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

public interface IAppInsightsConfigService
{
    Task<bool> IsConfiguredAsync();
    Task<AppInsightsConfig?> GetConfigAsync();
    Task StoreConfigAsync(string appId, string? resourceName, string? subscriptionId);
    Task ClearConfigAsync();
}

/// <summary>
/// Single-resource facade over <see cref="IAzureResourceRegistry"/> for the callers that still
/// think in terms of "the" App Insights resource (the <c>Otel*</c> commands,
/// <c>AppInsightsQueryService</c>): the first enabled <see cref="AzureResourceKind.AppInsights"/>
/// entry is the configured one.
/// </summary>
public class AppInsightsConfigService : IAppInsightsConfigService
{
    private readonly IAzureResourceRegistry _registry;

    public AppInsightsConfigService(IAzureResourceRegistry registry)
    {
        _registry = registry;
    }

    public async Task<bool> IsConfiguredAsync()
        => (await _registry.ListEnabledAsync(AzureResourceKind.AppInsights)).Count > 0;

    public async Task<AppInsightsConfig?> GetConfigAsync()
    {
        var entry = (await _registry.ListEnabledAsync(AzureResourceKind.AppInsights)).FirstOrDefault();
        if (entry == null)
            return null;

        return new AppInsightsConfig
        {
            AppId = entry.Key,
            ResourceName = entry.Name,
            SubscriptionId = entry.SubscriptionId,
            RegisteredAt = entry.DiscoveredAt
        };
    }

    public async Task StoreConfigAsync(string appId, string? resourceName, string? subscriptionId)
    {
        await _registry.UpsertAsync(new[]
        {
            new AzureResourceEntry
            {
                Kind = AzureResourceKind.AppInsights,
                Key = appId,
                Name = string.IsNullOrWhiteSpace(resourceName) ? appId : resourceName,
                SubscriptionId = string.IsNullOrWhiteSpace(subscriptionId) ? null : subscriptionId,
                Enabled = true,
                DiscoveredAt = DateTime.UtcNow
            }
        });
        // Upsert preserves a user's "off" — but registering is an explicit "on".
        await _registry.SetEnabledAsync(AzureResourceKind.AppInsights, appId, true);
    }

    public async Task ClearConfigAsync()
    {
        foreach (var entry in await _registry.ListAsync(AzureResourceKind.AppInsights))
            await _registry.RemoveAsync(AzureResourceKind.AppInsights, entry.Key);
    }
}
