using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Security;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Azure;

/// <summary>
/// The registry is the one place the Log Analytics, App Insights and storage verticals record which
/// Azure resources they know about and which of them are switched on. It owns a JSON file of its
/// own, and — exactly once, the first time that file is missing — lifts the single-resource keys the
/// old services wrote into <c>settings.json</c> into entries. The fileshare credential is read for
/// its non-secret fields only and never written or deleted here: the refresh token in it belongs to
/// another step.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "LogAnalytics")]
[Trait("Category", "AppInsights")]
public sealed class AzureResourceRegistryTests : IDisposable
{
    private const string FileShareKey = "fileshare.azure.credentials";

    private readonly string _testDirectory;
    private readonly string _registryPath;

    public AzureResourceRegistryTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"pks-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _registryPath = Path.Combine(_testDirectory, "azure-resources.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, recursive: true); } catch { /* best effort */ }
    }

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

    private AzureResourceRegistry CreateRegistry(
        Mock<IConfigurationService>? config = null,
        FakeSecretResolver? secrets = null)
        => new((config ?? CreateConfigMock()).Object, secrets ?? FakeSecretResolver.Empty, _registryPath);

    private static AzureResourceEntry Workspace(
        string key = "11111111-1111-1111-1111-111111111111",
        string name = "law-prod",
        string? resourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/law-prod",
        bool enabled = true)
        => new()
        {
            Kind = AzureResourceKind.LogAnalytics,
            Key = key,
            Name = name,
            ResourceId = resourceId,
            SubscriptionId = "sub-1",
            Enabled = enabled,
            DiscoveredAt = DateTime.UtcNow
        };

    // ── Basic file behaviour ──────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_ReturnsEmpty_WhenNothingStored()
    {
        var registry = CreateRegistry();

        var all = await registry.ListAsync();

        all.Should().BeEmpty();
        File.Exists(_registryPath).Should().BeTrue("the registry writes its file so migration never re-runs");
    }

    [Fact]
    public async Task UpsertAsync_ThenList_RoundTripsThroughTheFile()
    {
        var registry = CreateRegistry();

        await registry.UpsertAsync(new[] { Workspace() });

        var reloaded = CreateRegistry();
        var all = await reloaded.ListAsync();
        all.Should().ContainSingle();
        all[0].Kind.Should().Be(AzureResourceKind.LogAnalytics);
        all[0].Key.Should().Be("11111111-1111-1111-1111-111111111111");
        all[0].Name.Should().Be("law-prod");
        all[0].SubscriptionId.Should().Be("sub-1");
        all[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task File_UsesCamelCaseAndStringEnums()
    {
        var registry = CreateRegistry();

        await registry.UpsertAsync(new[] { Workspace() });

        var json = await File.ReadAllTextAsync(_registryPath);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("resources", out var resources).Should().BeTrue();
        doc.RootElement.TryGetProperty("lastModified", out _).Should().BeTrue();
        resources[0].GetProperty("kind").GetString().Should().Be("LogAnalytics");
        resources[0].GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ListAsync_FiltersByKind()
    {
        var registry = CreateRegistry();
        await registry.UpsertAsync(new[]
        {
            Workspace(),
            new AzureResourceEntry { Kind = AzureResourceKind.AppInsights, Key = "app-1", Name = "ai-prod", Enabled = true },
            new AzureResourceEntry { Kind = AzureResourceKind.Storage, Key = "stprod", Name = "stprod", Enabled = true }
        });

        (await registry.ListAsync()).Should().HaveCount(3);
        (await registry.ListAsync(AzureResourceKind.AppInsights)).Should().ContainSingle(e => e.Key == "app-1");
        (await registry.ListAsync(AzureResourceKind.Storage)).Should().ContainSingle(e => e.Key == "stprod");
    }

    // ── Upsert identity and merge ─────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_PreservesEnabledFlag_AndRefreshesName()
    {
        var registry = CreateRegistry();
        await registry.UpsertAsync(new[] { Workspace(name: "old-name") });
        await registry.SetEnabledAsync(AzureResourceKind.LogAnalytics, "old-name", false);

        await registry.UpsertAsync(new[] { Workspace(name: "new-name", enabled: true) });

        var all = await registry.ListAsync(AzureResourceKind.LogAnalytics);
        all.Should().ContainSingle();
        all[0].Name.Should().Be("new-name");
        all[0].Enabled.Should().BeFalse("a re-discovery must not flip a resource the user switched off");
    }

    [Fact]
    public async Task UpsertAsync_MatchesByResourceId_WhenBothHaveOne_EvenIfKeyChanged()
    {
        var registry = CreateRegistry();
        await registry.UpsertAsync(new[] { Workspace(key: "old-customer-id") });

        await registry.UpsertAsync(new[] { Workspace(key: "NEW-customer-id") });

        var all = await registry.ListAsync(AzureResourceKind.LogAnalytics);
        all.Should().ContainSingle();
        all[0].Key.Should().Be("NEW-customer-id");
    }

    [Fact]
    public async Task UpsertAsync_MatchesByKey_CaseInsensitively_WhenResourceIdMissing()
    {
        var registry = CreateRegistry();
        await registry.UpsertAsync(new[] { Workspace(key: "abc-123", resourceId: null, name: "first") });

        await registry.UpsertAsync(new[] { Workspace(key: "ABC-123", resourceId: null, name: "second") });

        var all = await registry.ListAsync(AzureResourceKind.LogAnalytics);
        all.Should().ContainSingle();
        all[0].Name.Should().Be("second");
    }

    [Fact]
    public async Task UpsertAsync_MatchesByKey_WhenOnlyOneSideHasResourceId()
    {
        var registry = CreateRegistry();
        await registry.UpsertAsync(new[] { Workspace(key: "abc-123", resourceId: null) });

        await registry.UpsertAsync(new[] { Workspace(key: "abc-123", resourceId: "/subscriptions/sub-1/x") });

        var all = await registry.ListAsync(AzureResourceKind.LogAnalytics);
        all.Should().ContainSingle();
        all[0].ResourceId.Should().Be("/subscriptions/sub-1/x", "the incoming entry refreshes the ARM id");
    }

    [Fact]
    public async Task UpsertAsync_SameKeyDifferentKind_AreDistinctEntries()
    {
        var registry = CreateRegistry();

        await registry.UpsertAsync(new[]
        {
            new AzureResourceEntry { Kind = AzureResourceKind.LogAnalytics, Key = "shared", Name = "a", Enabled = true },
            new AzureResourceEntry { Kind = AzureResourceKind.AppInsights, Key = "shared", Name = "b", Enabled = true }
        });

        (await registry.ListAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task UpsertAsync_OnlyOverwritesTenantId_WhenIncomingHasOne()
    {
        var registry = CreateRegistry();
        var first = Workspace();
        first.TenantId = "tenant-known";
        await registry.UpsertAsync(new[] { first });

        var again = Workspace();
        again.TenantId = null;
        await registry.UpsertAsync(new[] { again });
        (await registry.FindAsync(AzureResourceKind.LogAnalytics, "law-prod"))!.TenantId.Should().Be("tenant-known");

        var newer = Workspace();
        newer.TenantId = "tenant-new";
        await registry.UpsertAsync(new[] { newer });
        (await registry.FindAsync(AzureResourceKind.LogAnalytics, "law-prod"))!.TenantId.Should().Be("tenant-new");
    }

    [Fact]
    public async Task UpsertAsync_StampsDiscoveredAt_WhenCallerLeftItDefault()
    {
        var registry = CreateRegistry();
        var entry = Workspace();
        entry.DiscoveredAt = default;

        await registry.UpsertAsync(new[] { entry });

        var stored = (await registry.ListAsync())[0];
        stored.DiscoveredAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    // ── Enable / remove / find ────────────────────────────────────────────

    [Fact]
    public async Task SetEnabledAsync_TogglesAndPersists()
    {
        var registry = CreateRegistry();
        await registry.UpsertAsync(new[] { Workspace() });

        (await registry.SetEnabledAsync(AzureResourceKind.LogAnalytics, "law-prod", false)).Should().BeTrue();
        (await CreateRegistry().ListEnabledAsync(AzureResourceKind.LogAnalytics)).Should().BeEmpty();

        (await registry.SetEnabledAsync(AzureResourceKind.LogAnalytics, "law-prod", true)).Should().BeTrue();
        (await CreateRegistry().ListEnabledAsync(AzureResourceKind.LogAnalytics)).Should().ContainSingle();
    }

    [Fact]
    public async Task SetEnabledAsync_ReturnsFalse_WhenNotFound()
    {
        var registry = CreateRegistry();
        await registry.UpsertAsync(new[] { Workspace() });

        (await registry.SetEnabledAsync(AzureResourceKind.LogAnalytics, "no-such", true)).Should().BeFalse();
        (await registry.SetEnabledAsync(AzureResourceKind.AppInsights, "law-prod", true)).Should().BeFalse("kind is part of the identity");
    }

    [Fact]
    public async Task RemoveAsync_RemovesOnlyThatEntry()
    {
        var registry = CreateRegistry();
        await registry.UpsertAsync(new[]
        {
            Workspace(key: "k1", name: "one", resourceId: null),
            Workspace(key: "k2", name: "two", resourceId: null)
        });

        (await registry.RemoveAsync(AzureResourceKind.LogAnalytics, "K1")).Should().BeTrue();
        (await registry.RemoveAsync(AzureResourceKind.LogAnalytics, "K1")).Should().BeFalse();

        var all = await CreateRegistry().ListAsync();
        all.Should().ContainSingle(e => e.Key == "k2");
    }

    [Fact]
    public async Task FindAsync_MatchesNameOrKey_CaseInsensitively()
    {
        var registry = CreateRegistry();
        await registry.UpsertAsync(new[] { Workspace(key: "11111111-aaaa-1111-1111-111111111111", name: "Law-Prod") });

        (await registry.FindAsync(AzureResourceKind.LogAnalytics, "law-prod")).Should().NotBeNull();
        (await registry.FindAsync(AzureResourceKind.LogAnalytics, "11111111-AAAA-1111-1111-111111111111")).Should().NotBeNull();
        (await registry.FindAsync(AzureResourceKind.LogAnalytics, "nope")).Should().BeNull();
        (await registry.FindAsync(AzureResourceKind.AppInsights, "law-prod")).Should().BeNull();
    }

    [Fact]
    public async Task ListEnabledAsync_ReturnsOnlyEnabledOfThatKind()
    {
        var registry = CreateRegistry();
        await registry.UpsertAsync(new[]
        {
            Workspace(key: "on", name: "on", resourceId: null, enabled: true),
            Workspace(key: "off", name: "off", resourceId: null, enabled: false),
            new AzureResourceEntry { Kind = AzureResourceKind.AppInsights, Key = "ai", Name = "ai", Enabled = true }
        });

        var enabled = await registry.ListEnabledAsync(AzureResourceKind.LogAnalytics);

        enabled.Should().ContainSingle(e => e.Key == "on");
    }

    // ── Legacy migration ──────────────────────────────────────────────────

    [Fact]
    public async Task Migration_LiftsLogAnalyticsKeys_AndDeletesThem()
    {
        var config = CreateConfigMock(new Dictionary<string, string?>
        {
            ["loganalytics.workspace_id"] = "ws-guid",
            ["loganalytics.workspace_name"] = "law-legacy",
            ["loganalytics.resource_id"] = "/subscriptions/sub-9/rg/law-legacy",
            ["loganalytics.subscription_id"] = "sub-9",
            ["loganalytics.registered_at"] = "2026-01-02T03:04:05.0000000Z"
        });

        var registry = CreateRegistry(config);
        var all = await registry.ListAsync(AzureResourceKind.LogAnalytics);

        all.Should().ContainSingle();
        var e = all[0];
        e.Key.Should().Be("ws-guid");
        e.Name.Should().Be("law-legacy");
        e.ResourceId.Should().Be("/subscriptions/sub-9/rg/law-legacy");
        e.SubscriptionId.Should().Be("sub-9");
        e.Enabled.Should().BeTrue();
        e.TenantId.Should().BeNull("the legacy keys never recorded a tenant");

        config.Verify(m => m.DeleteAsync("loganalytics.workspace_id"), Times.Once);
        config.Verify(m => m.DeleteAsync("loganalytics.workspace_name"), Times.Once);
        config.Verify(m => m.DeleteAsync("loganalytics.resource_id"), Times.Once);
        config.Verify(m => m.DeleteAsync("loganalytics.subscription_id"), Times.Once);
        config.Verify(m => m.DeleteAsync("loganalytics.registered_at"), Times.Once);
    }

    [Fact]
    public async Task Migration_UsesWorkspaceIdAsName_WhenNameMissing()
    {
        var config = CreateConfigMock(new Dictionary<string, string?>
        {
            ["loganalytics.workspace_id"] = "ws-guid"
        });

        var all = await CreateRegistry(config).ListAsync(AzureResourceKind.LogAnalytics);

        all.Should().ContainSingle();
        all[0].Name.Should().Be("ws-guid");
        all[0].ResourceId.Should().BeNull();
    }

    [Fact]
    public async Task Migration_LiftsAppInsightsKeys_AndDeletesThem()
    {
        var config = CreateConfigMock(new Dictionary<string, string?>
        {
            ["appinsights.app_id"] = "app-guid",
            ["appinsights.resource_name"] = "ai-legacy",
            ["appinsights.subscription_id"] = "sub-9",
            ["appinsights.registered_at"] = "2026-01-02T03:04:05.0000000Z"
        });

        var all = await CreateRegistry(config).ListAsync(AzureResourceKind.AppInsights);

        all.Should().ContainSingle();
        all[0].Key.Should().Be("app-guid");
        all[0].Name.Should().Be("ai-legacy");
        all[0].SubscriptionId.Should().Be("sub-9");
        all[0].Enabled.Should().BeTrue();
        all[0].TenantId.Should().BeNull();

        config.Verify(m => m.DeleteAsync("appinsights.app_id"), Times.Once);
        config.Verify(m => m.DeleteAsync("appinsights.resource_name"), Times.Once);
        config.Verify(m => m.DeleteAsync("appinsights.subscription_id"), Times.Once);
        config.Verify(m => m.DeleteAsync("appinsights.registered_at"), Times.Once);
    }

    [Fact]
    public async Task Migration_LiftsStorageAccountFromFileShareCredentials_WithoutTouchingTheKey()
    {
        var config = CreateConfigMock();
        var credentialsJson = JsonSerializer.Serialize(new FileShareStoredCredentials
        {
            TenantId = "tenant-fs",
            RefreshToken = "refresh-token-that-must-never-leave-the-store",
            SelectedSubscriptionId = "sub-fs",
            SelectedSubscriptionName = "FileShare Sub",
            SelectedStorageAccountName = "stlegacy",
            SelectedStorageAccountResourceGroup = "rg-fs"
        });
        var secrets = new FakeSecretResolver((FileShareKey, credentialsJson));

        var all = await CreateRegistry(config, secrets).ListAsync(AzureResourceKind.Storage);

        all.Should().ContainSingle();
        var e = all[0];
        e.Key.Should().Be("stlegacy");
        e.Name.Should().Be("stlegacy");
        e.ResourceGroup.Should().Be("rg-fs");
        e.SubscriptionId.Should().Be("sub-fs");
        e.SubscriptionName.Should().Be("FileShare Sub");
        e.TenantId.Should().Be("tenant-fs");
        e.Enabled.Should().BeTrue();

        config.Verify(m => m.DeleteAsync(FileShareKey), Times.Never);
        config.Verify(m => m.SetAsync(FileShareKey, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        (await secrets.RevealAsync(FileShareKey)).Should().Be(credentialsJson, "the credential is read, never rewritten");

        var fileText = await File.ReadAllTextAsync(_registryPath);
        fileText.Should().NotContain("refresh-token", "no credential material may reach the registry file");
    }

    [Fact]
    public async Task Migration_SkipsStorage_WhenCredentialsHaveNoAccountName()
    {
        var credentialsJson = JsonSerializer.Serialize(new FileShareStoredCredentials
        {
            TenantId = "tenant-fs",
            RefreshToken = "rt"
        });
        var secrets = new FakeSecretResolver((FileShareKey, credentialsJson));

        var all = await CreateRegistry(CreateConfigMock(), secrets).ListAsync();

        all.Should().BeEmpty();
    }

    [Fact]
    public async Task Migration_SkipsMalformedSources_ButStillLiftsTheOthers()
    {
        var config = CreateConfigMock(new Dictionary<string, string?>
        {
            ["appinsights.app_id"] = "app-guid"
        });
        var secrets = new FakeSecretResolver((FileShareKey, "{ this is not json"));

        var all = await CreateRegistry(config, secrets).ListAsync();

        all.Should().ContainSingle(e => e.Kind == AzureResourceKind.AppInsights);
        File.Exists(_registryPath).Should().BeTrue();
    }

    [Fact]
    public async Task Migration_LiftsAllThreeSourcesTogether()
    {
        var config = CreateConfigMock(new Dictionary<string, string?>
        {
            ["loganalytics.workspace_id"] = "ws-guid",
            ["appinsights.app_id"] = "app-guid"
        });
        var secrets = new FakeSecretResolver((FileShareKey, JsonSerializer.Serialize(new FileShareStoredCredentials
        {
            TenantId = "t", RefreshToken = "rt", SelectedStorageAccountName = "stacct"
        })));

        var all = await CreateRegistry(config, secrets).ListAsync();

        all.Select(e => e.Kind).Should().BeEquivalentTo(new[]
        {
            AzureResourceKind.LogAnalytics, AzureResourceKind.AppInsights, AzureResourceKind.Storage
        });
        all.Should().OnlyContain(e => e.Enabled);
    }

    [Fact]
    public async Task Migration_DoesNotRun_WhenFileAlreadyExists()
    {
        await File.WriteAllTextAsync(_registryPath, """{ "resources": [], "lastModified": null }""");
        var config = CreateConfigMock(new Dictionary<string, string?>
        {
            ["loganalytics.workspace_id"] = "ws-guid",
            ["appinsights.app_id"] = "app-guid"
        });

        var all = await CreateRegistry(config).ListAsync();

        all.Should().BeEmpty("an existing file means migration already happened");
        config.Verify(m => m.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Migration_RunsOnlyOnce_AcrossInstances()
    {
        var config = CreateConfigMock(new Dictionary<string, string?>
        {
            ["loganalytics.workspace_id"] = "ws-guid"
        });

        await CreateRegistry(config).ListAsync();
        // Re-seed the legacy key as if something wrote it back; a second instance must not lift it again.
        await config.Object.SetAsync("loganalytics.workspace_id", "ws-guid-2", global: true);
        await CreateRegistry(config).ListAsync();

        var all = await CreateRegistry(config).ListAsync(AzureResourceKind.LogAnalytics);
        all.Should().ContainSingle(e => e.Key == "ws-guid");
        config.Verify(m => m.DeleteAsync("loganalytics.workspace_id"), Times.Once);
    }

    [Fact]
    public async Task Migration_WritesEmptyFile_WhenNothingToMigrate()
    {
        var registry = CreateRegistry();

        await registry.ListAsync();

        File.Exists(_registryPath).Should().BeTrue();
        var json = await File.ReadAllTextAsync(_registryPath);
        json.Should().Contain("\"resources\"");
    }
}
