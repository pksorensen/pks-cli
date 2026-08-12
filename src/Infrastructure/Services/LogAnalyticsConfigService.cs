using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

public interface ILogAnalyticsConfigService
{
    Task<bool> IsConfiguredAsync();
    Task<LogAnalyticsConfig?> GetConfigAsync();
    Task StoreConfigAsync(string workspaceId, string? workspaceName, string? resourceId, string? subscriptionId);
    Task ClearConfigAsync();
}

public class LogAnalyticsConfigService : ILogAnalyticsConfigService
{
    private const string KeyWorkspaceId = "loganalytics.workspace_id";
    private const string KeyWorkspaceName = "loganalytics.workspace_name";
    private const string KeyResourceId = "loganalytics.resource_id";
    private const string KeySubscriptionId = "loganalytics.subscription_id";
    private const string KeyRegisteredAt = "loganalytics.registered_at";

    private readonly IConfigurationService _config;

    public LogAnalyticsConfigService(IConfigurationService config)
    {
        _config = config;
    }

    public async Task<bool> IsConfiguredAsync()
    {
        var workspaceId = await _config.GetAsync(KeyWorkspaceId);
        return !string.IsNullOrWhiteSpace(workspaceId);
    }

    public async Task<LogAnalyticsConfig?> GetConfigAsync()
    {
        var workspaceId = await _config.GetAsync(KeyWorkspaceId);
        if (string.IsNullOrWhiteSpace(workspaceId))
            return null;

        return new LogAnalyticsConfig
        {
            WorkspaceId = workspaceId,
            WorkspaceName = await _config.GetAsync(KeyWorkspaceName),
            ResourceId = await _config.GetAsync(KeyResourceId),
            SubscriptionId = await _config.GetAsync(KeySubscriptionId),
            RegisteredAt = DateTime.TryParse(
                await _config.GetAsync(KeyRegisteredAt), out var dt) ? dt : DateTime.MinValue
        };
    }

    public async Task StoreConfigAsync(string workspaceId, string? workspaceName, string? resourceId, string? subscriptionId)
    {
        await _config.SetAsync(KeyWorkspaceId, workspaceId, global: true);
        await _config.SetAsync(KeyWorkspaceName, workspaceName ?? string.Empty, global: true);
        await _config.SetAsync(KeyResourceId, resourceId ?? string.Empty, global: true);
        await _config.SetAsync(KeySubscriptionId, subscriptionId ?? string.Empty, global: true);
        await _config.SetAsync(KeyRegisteredAt, DateTime.UtcNow.ToString("O"), global: true);
    }

    public async Task ClearConfigAsync()
    {
        await _config.DeleteAsync(KeyWorkspaceId);
        await _config.DeleteAsync(KeyWorkspaceName);
        await _config.DeleteAsync(KeyResourceId);
        await _config.DeleteAsync(KeySubscriptionId);
        await _config.DeleteAsync(KeyRegisteredAt);
    }
}
