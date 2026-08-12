using FluentAssertions;
using Moq;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Services;

[Trait("Category", "LogAnalytics")]
public class LogAnalyticsConfigServiceTests
{
    private static Mock<IConfigurationService> CreateConfigMock(Dictionary<string, string?>? data = null)
    {
        var store = data != null
            ? new Dictionary<string, string?>(data)
            : new Dictionary<string, string?>();

        var mock = new Mock<IConfigurationService>();

        mock.Setup(m => m.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => store.TryGetValue(key, out var v) ? v : null);

        mock.Setup(m => m.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<string, string, bool, bool>((key, value, global, encrypt) => store[key] = value)
            .Returns(Task.CompletedTask);

        mock.Setup(m => m.DeleteAsync(It.IsAny<string>()))
            .Callback<string>(key => store.Remove(key))
            .Returns(Task.CompletedTask);

        return mock;
    }

    private static LogAnalyticsConfigService CreateService(Mock<IConfigurationService>? configMock = null)
        => new((configMock ?? CreateConfigMock()).Object);

    [Fact]
    public async Task IsConfiguredAsync_ReturnsFalse_WhenNoConfig()
    {
        var svc = CreateService();
        (await svc.IsConfiguredAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsConfiguredAsync_ReturnsTrue_WhenWorkspaceIdPresent()
    {
        var mock = CreateConfigMock(new Dictionary<string, string?>
        {
            ["loganalytics.workspace_id"] = "e8d8a461-f63b-464d-ae40-8771bcb46140"
        });
        var svc = CreateService(mock);
        (await svc.IsConfiguredAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task GetConfigAsync_ReturnsNull_WhenNotConfigured()
    {
        var svc = CreateService();
        (await svc.GetConfigAsync()).Should().BeNull();
    }

    [Fact]
    public async Task GetConfigAsync_ReturnsConfig_WhenConfigured()
    {
        var mock = CreateConfigMock(new Dictionary<string, string?>
        {
            ["loganalytics.workspace_id"] = "ws-guid",
            ["loganalytics.workspace_name"] = "law-prod",
            ["loganalytics.resource_id"] = "/subscriptions/sub-999/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/law-prod",
            ["loganalytics.subscription_id"] = "sub-999",
            ["loganalytics.registered_at"] = DateTime.UtcNow.ToString("O")
        });
        var svc = CreateService(mock);

        var result = await svc.GetConfigAsync();

        result.Should().NotBeNull();
        result!.WorkspaceId.Should().Be("ws-guid");
        result.WorkspaceName.Should().Be("law-prod");
        result.SubscriptionId.Should().Be("sub-999");
        result.ResourceId.Should().Contain("law-prod");
    }

    [Fact]
    public async Task StoreConfigAsync_PersistsAllKeys()
    {
        var mock = CreateConfigMock();
        var svc = CreateService(mock);

        await svc.StoreConfigAsync("ws-guid", "law-prod", "/subscriptions/sub-456/x", "sub-456");

        mock.Verify(m => m.SetAsync("loganalytics.workspace_id", "ws-guid", true, false), Times.Once);
        mock.Verify(m => m.SetAsync("loganalytics.workspace_name", "law-prod", true, false), Times.Once);
        mock.Verify(m => m.SetAsync("loganalytics.resource_id", "/subscriptions/sub-456/x", true, false), Times.Once);
        mock.Verify(m => m.SetAsync("loganalytics.subscription_id", "sub-456", true, false), Times.Once);
        mock.Verify(m => m.SetAsync("loganalytics.registered_at", It.IsAny<string>(), true, false), Times.Once);
    }

    [Fact]
    public async Task StoreConfigAsync_HandlesNullOptionalValues()
    {
        var mock = CreateConfigMock();
        var svc = CreateService(mock);

        await svc.StoreConfigAsync("ws-guid", null, null, null);

        mock.Verify(m => m.SetAsync("loganalytics.workspace_name", string.Empty, true, false), Times.Once);
        mock.Verify(m => m.SetAsync("loganalytics.resource_id", string.Empty, true, false), Times.Once);
    }

    [Fact]
    public async Task ClearConfigAsync_DeletesAllKeys()
    {
        var mock = CreateConfigMock();
        var svc = CreateService(mock);

        await svc.ClearConfigAsync();

        mock.Verify(m => m.DeleteAsync("loganalytics.workspace_id"), Times.Once);
        mock.Verify(m => m.DeleteAsync("loganalytics.workspace_name"), Times.Once);
        mock.Verify(m => m.DeleteAsync("loganalytics.resource_id"), Times.Once);
        mock.Verify(m => m.DeleteAsync("loganalytics.subscription_id"), Times.Once);
        mock.Verify(m => m.DeleteAsync("loganalytics.registered_at"), Times.Once);
    }
}
