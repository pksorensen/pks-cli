using FluentAssertions;
using Moq;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services;

[Trait("Category", "AppInsights")]
public class AppInsightsConfigServiceTests
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

    private static AppInsightsConfigService CreateService(Mock<IConfigurationService>? configMock = null)
        => new((configMock ?? CreateConfigMock()).Object);

    [Fact]
    public async Task IsConfiguredAsync_ReturnsFalse_WhenNoConfig()
    {
        var svc = CreateService();
        var result = await svc.IsConfiguredAsync();
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsConfiguredAsync_ReturnsTrue_WhenAppIdPresent()
    {
        var mock = CreateConfigMock(new Dictionary<string, string?> { ["appinsights.app_id"] = "my-app-id" });
        var svc = CreateService(mock);
        var result = await svc.IsConfiguredAsync();
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsConfiguredAsync_ReturnsFalse_WhenAppIdIsEmpty()
    {
        var mock = CreateConfigMock(new Dictionary<string, string?> { ["appinsights.app_id"] = "" });
        var svc = CreateService(mock);
        var result = await svc.IsConfiguredAsync();
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetConfigAsync_ReturnsNull_WhenNotConfigured()
    {
        var svc = CreateService();
        var result = await svc.GetConfigAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetConfigAsync_ReturnsConfig_WhenConfigured()
    {
        var registeredAt = DateTime.UtcNow.ToString("O");
        var mock = CreateConfigMock(new Dictionary<string, string?>
        {
            ["appinsights.app_id"] = "my-app-id",
            ["appinsights.resource_name"] = "My App Insights",
            ["appinsights.subscription_id"] = "sub-999",
            ["appinsights.registered_at"] = registeredAt
        });
        var svc = CreateService(mock);

        var result = await svc.GetConfigAsync();

        result.Should().NotBeNull();
        result!.AppId.Should().Be("my-app-id");
        result.ResourceName.Should().Be("My App Insights");
        result.SubscriptionId.Should().Be("sub-999");
    }

    [Fact]
    public async Task StoreConfigAsync_PersistsAllKeys()
    {
        var mock = CreateConfigMock();
        var svc = CreateService(mock);

        await svc.StoreConfigAsync("app-id-123", "My Resource", "sub-456");

        mock.Verify(m => m.SetAsync("appinsights.app_id", "app-id-123", true, false), Times.Once);
        mock.Verify(m => m.SetAsync("appinsights.resource_name", "My Resource", true, false), Times.Once);
        mock.Verify(m => m.SetAsync("appinsights.subscription_id", "sub-456", true, false), Times.Once);
        mock.Verify(m => m.SetAsync("appinsights.registered_at", It.IsAny<string>(), true, false), Times.Once);
    }

    [Fact]
    public async Task StoreConfigAsync_HandlesNullResourceName()
    {
        var mock = CreateConfigMock();
        var svc = CreateService(mock);

        await svc.StoreConfigAsync("app-id-123", null, null);

        mock.Verify(m => m.SetAsync("appinsights.resource_name", string.Empty, true, false), Times.Once);
    }

    [Fact]
    public async Task ClearConfigAsync_DeletesAllKeys()
    {
        var mock = CreateConfigMock();
        var svc = CreateService(mock);

        await svc.ClearConfigAsync();

        mock.Verify(m => m.DeleteAsync("appinsights.app_id"), Times.Once);
        mock.Verify(m => m.DeleteAsync("appinsights.resource_name"), Times.Once);
        mock.Verify(m => m.DeleteAsync("appinsights.subscription_id"), Times.Once);
        mock.Verify(m => m.DeleteAsync("appinsights.registered_at"), Times.Once);
    }
}
