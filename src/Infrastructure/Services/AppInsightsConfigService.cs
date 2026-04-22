using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

public interface IAppInsightsConfigService
{
    Task<bool> IsConfiguredAsync();
    Task<AppInsightsConfig?> GetConfigAsync();
    Task StoreConfigAsync(string appId, string? resourceName, string? subscriptionId);
    Task ClearConfigAsync();
}

public class AppInsightsConfigService : IAppInsightsConfigService
{
    private const string KeyAppId = "appinsights.app_id";
    private const string KeyResourceName = "appinsights.resource_name";
    private const string KeySubscriptionId = "appinsights.subscription_id";
    private const string KeyRegisteredAt = "appinsights.registered_at";

    private readonly IConfigurationService _config;

    public AppInsightsConfigService(IConfigurationService config)
    {
        _config = config;
    }

    public async Task<bool> IsConfiguredAsync()
    {
        var appId = await _config.GetAsync(KeyAppId);
        return !string.IsNullOrWhiteSpace(appId);
    }

    public async Task<AppInsightsConfig?> GetConfigAsync()
    {
        var appId = await _config.GetAsync(KeyAppId);
        if (string.IsNullOrWhiteSpace(appId))
            return null;

        return new AppInsightsConfig
        {
            AppId = appId,
            ResourceName = await _config.GetAsync(KeyResourceName),
            SubscriptionId = await _config.GetAsync(KeySubscriptionId),
            RegisteredAt = DateTime.TryParse(
                await _config.GetAsync(KeyRegisteredAt), out var dt) ? dt : DateTime.MinValue
        };
    }

    public async Task StoreConfigAsync(string appId, string? resourceName, string? subscriptionId)
    {
        await _config.SetAsync(KeyAppId, appId, global: true);
        await _config.SetAsync(KeyResourceName, resourceName ?? string.Empty, global: true);
        await _config.SetAsync(KeySubscriptionId, subscriptionId ?? string.Empty, global: true);
        await _config.SetAsync(KeyRegisteredAt, DateTime.UtcNow.ToString("O"), global: true);
    }

    public async Task ClearConfigAsync()
    {
        await _config.DeleteAsync(KeyAppId);
        await _config.DeleteAsync(KeyResourceName);
        await _config.DeleteAsync(KeySubscriptionId);
        await _config.DeleteAsync(KeyRegisteredAt);
    }
}
