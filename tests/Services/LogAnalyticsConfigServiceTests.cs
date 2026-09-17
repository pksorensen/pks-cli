using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Security;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using Xunit;

namespace PKS.CLI.Tests.Services;

/// <summary>
/// <see cref="LogAnalyticsConfigService"/> is a facade over <see cref="IAzureResourceRegistry"/>:
/// <c>KustoCommand</c> and <c>LogAnalyticsQueryService</c> keep the single-workspace interface,
/// while the data lives as <see cref="AzureResourceKind.LogAnalytics"/> entries with an enabled flag.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "LogAnalytics")]
public sealed class LogAnalyticsConfigServiceTests : IDisposable
{
    private const string ResourceId =
        "/subscriptions/sub-999/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/law-prod";

    private readonly string _testDirectory;
    private readonly AzureResourceRegistry _registry;

    public LogAnalyticsConfigServiceTests()
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

    private LogAnalyticsConfigService CreateService() => new(_registry);

    private static AzureResourceEntry Entry(string key, string name, bool enabled = true) => new()
    {
        Kind = AzureResourceKind.LogAnalytics,
        Key = key,
        Name = name,
        ResourceId = $"/subscriptions/sub-999/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/{name}",
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
        await _registry.UpsertAsync(new[] { Entry("e8d8a461-f63b-464d-ae40-8771bcb46140", "law-prod") });
        var svc = CreateService();
        (await svc.IsConfiguredAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task IsConfiguredAsync_ReturnsFalse_WhenTheOnlyEntryIsDisabled()
    {
        await _registry.UpsertAsync(new[] { Entry("ws-guid", "law-prod", enabled: false) });
        var svc = CreateService();
        (await svc.IsConfiguredAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsConfiguredAsync_IgnoresOtherKinds()
    {
        await _registry.UpsertAsync(new[]
        {
            new AzureResourceEntry { Kind = AzureResourceKind.AppInsights, Key = "app", Name = "app", Enabled = true }
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
            Entry("disabled-ws", "law-old", enabled: false),
            Entry("ws-guid", "law-prod")
        });
        var svc = CreateService();

        var result = await svc.GetConfigAsync();

        result.Should().NotBeNull();
        result!.WorkspaceId.Should().Be("ws-guid");
        result.WorkspaceName.Should().Be("law-prod");
        result.SubscriptionId.Should().Be("sub-999");
        result.ResourceId.Should().Be(ResourceId);
        result.RegisteredAt.Should().Be(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
    }

    [Fact]
    public async Task StoreConfigAsync_UpsertsAnEnabledEntry()
    {
        var svc = CreateService();

        await svc.StoreConfigAsync("ws-guid", "law-prod", "/subscriptions/sub-456/x", "sub-456");

        var entries = await _registry.ListAsync(AzureResourceKind.LogAnalytics);
        entries.Should().ContainSingle();
        entries[0].Key.Should().Be("ws-guid");
        entries[0].Name.Should().Be("law-prod");
        entries[0].ResourceId.Should().Be("/subscriptions/sub-456/x");
        entries[0].SubscriptionId.Should().Be("sub-456");
        entries[0].Enabled.Should().BeTrue();
        entries[0].DiscoveredAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task StoreConfigAsync_ReEnablesAnEntryTheUserHadSwitchedOff()
    {
        await _registry.UpsertAsync(new[] { Entry("ws-guid", "law-prod", enabled: false) });
        var svc = CreateService();

        await svc.StoreConfigAsync("ws-guid", "law-prod", ResourceId, "sub-999");

        (await _registry.ListEnabledAsync(AzureResourceKind.LogAnalytics)).Should().ContainSingle();
    }

    [Fact]
    public async Task StoreConfigAsync_UsesWorkspaceIdAsName_WhenOptionalValuesAreNull()
    {
        var svc = CreateService();

        await svc.StoreConfigAsync("ws-guid", null, null, null);

        var entry = (await _registry.ListAsync(AzureResourceKind.LogAnalytics)).Single();
        entry.Name.Should().Be("ws-guid");
        entry.ResourceId.Should().BeNull();
        entry.SubscriptionId.Should().BeNull();
    }

    [Fact]
    public async Task ClearConfigAsync_RemovesAllLogAnalyticsEntries_ButNothingElse()
    {
        await _registry.UpsertAsync(new[]
        {
            Entry("a", "a"),
            Entry("b", "b", enabled: false),
            new AzureResourceEntry { Kind = AzureResourceKind.AppInsights, Key = "app", Name = "app", Enabled = true }
        });
        var svc = CreateService();

        await svc.ClearConfigAsync();

        (await _registry.ListAsync(AzureResourceKind.LogAnalytics)).Should().BeEmpty();
        (await _registry.ListAsync(AzureResourceKind.AppInsights)).Should().ContainSingle();
    }
}
