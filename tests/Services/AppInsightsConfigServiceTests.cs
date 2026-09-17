using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Security;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using Xunit;

namespace PKS.CLI.Tests.Services;

/// <summary>
/// <see cref="AppInsightsConfigService"/> is a facade over <see cref="IAzureResourceRegistry"/>:
/// the interface the Otel commands and <c>AppInsightsQueryService</c> depend on is unchanged, but
/// the data now lives as <see cref="AzureResourceKind.AppInsights"/> entries with an enabled flag.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "AppInsights")]
public sealed class AppInsightsConfigServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly AzureResourceRegistry _registry;

    public AppInsightsConfigServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"pks-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        var config = new Mock<IConfigurationService>();
        config.Setup(m => m.GetAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        config.Setup(m => m.DeleteAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        _registry = new AzureResourceRegistry(
            config.Object,
            FakeSecretResolver.Empty,
            Path.Combine(_testDirectory, "azure-resources.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, recursive: true); } catch { /* best effort */ }
    }

    private AppInsightsConfigService CreateService() => new(_registry);

    private static AzureResourceEntry Entry(string key, string name, bool enabled = true) => new()
    {
        Kind = AzureResourceKind.AppInsights,
        Key = key,
        Name = name,
        SubscriptionId = "sub-999",
        Enabled = enabled,
        DiscoveredAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
    };

    [Fact]
    public async Task IsConfiguredAsync_ReturnsFalse_WhenNoEntries()
    {
        var svc = CreateService();
        (await svc.IsConfiguredAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsConfiguredAsync_ReturnsTrue_WhenAnEnabledEntryExists()
    {
        await _registry.UpsertAsync(new[] { Entry("my-app-id", "My App Insights") });
        var svc = CreateService();
        (await svc.IsConfiguredAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task IsConfiguredAsync_ReturnsFalse_WhenTheOnlyEntryIsDisabled()
    {
        await _registry.UpsertAsync(new[] { Entry("my-app-id", "My App Insights", enabled: false) });
        var svc = CreateService();
        (await svc.IsConfiguredAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsConfiguredAsync_IgnoresOtherKinds()
    {
        await _registry.UpsertAsync(new[]
        {
            new AzureResourceEntry { Kind = AzureResourceKind.LogAnalytics, Key = "ws", Name = "ws", Enabled = true }
        });
        var svc = CreateService();
        (await svc.IsConfiguredAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task GetConfigAsync_ReturnsNull_WhenNotConfigured()
    {
        var svc = CreateService();
        (await svc.GetConfigAsync()).Should().BeNull();
    }

    [Fact]
    public async Task GetConfigAsync_MapsTheFirstEnabledEntry()
    {
        await _registry.UpsertAsync(new[]
        {
            Entry("disabled-app", "Disabled", enabled: false),
            Entry("my-app-id", "My App Insights")
        });
        var svc = CreateService();

        var result = await svc.GetConfigAsync();

        result.Should().NotBeNull();
        result!.AppId.Should().Be("my-app-id");
        result.ResourceName.Should().Be("My App Insights");
        result.SubscriptionId.Should().Be("sub-999");
        result.RegisteredAt.Should().Be(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
    }

    [Fact]
    public async Task StoreConfigAsync_UpsertsAnEnabledEntry()
    {
        var svc = CreateService();

        await svc.StoreConfigAsync("app-id-123", "My Resource", "sub-456");

        var entries = await _registry.ListAsync(AzureResourceKind.AppInsights);
        entries.Should().ContainSingle();
        entries[0].Key.Should().Be("app-id-123");
        entries[0].Name.Should().Be("My Resource");
        entries[0].SubscriptionId.Should().Be("sub-456");
        entries[0].Enabled.Should().BeTrue();
        entries[0].DiscoveredAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task StoreConfigAsync_ReEnablesAnEntryTheUserHadSwitchedOff()
    {
        await _registry.UpsertAsync(new[] { Entry("app-id-123", "My Resource", enabled: false) });
        var svc = CreateService();

        await svc.StoreConfigAsync("app-id-123", "My Resource", "sub-456");

        (await _registry.ListEnabledAsync(AzureResourceKind.AppInsights)).Should().ContainSingle();
    }

    [Fact]
    public async Task StoreConfigAsync_UsesAppIdAsName_WhenResourceNameIsNull()
    {
        var svc = CreateService();

        await svc.StoreConfigAsync("app-id-123", null, null);

        var entry = (await _registry.ListAsync(AzureResourceKind.AppInsights)).Single();
        entry.Name.Should().Be("app-id-123");
        entry.SubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task ClearConfigAsync_RemovesAllAppInsightsEntries_ButNothingElse()
    {
        await _registry.UpsertAsync(new[]
        {
            Entry("a", "a"),
            Entry("b", "b", enabled: false),
            new AzureResourceEntry { Kind = AzureResourceKind.LogAnalytics, Key = "ws", Name = "ws", Enabled = true }
        });
        var svc = CreateService();

        await svc.ClearConfigAsync();

        (await _registry.ListAsync(AzureResourceKind.AppInsights)).Should().BeEmpty();
        (await _registry.ListAsync(AzureResourceKind.LogAnalytics)).Should().ContainSingle();
    }
}
