using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Security;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Services.Azure;

/// <summary>
/// The one init flow behind <c>pks loganalytics init</c>, <c>pks appinsights init</c> and
/// <c>pks fileshare init</c>. It only opens a browser when it has to (no tenant known, an explicit
/// <c>--reauth</c>, or a tenant whose refresh token is dead and the user says yes), discovers every
/// resource of the kind across the tenants and subscriptions it knows, and lets the user tick the
/// ones to enable in a checkbox list. Unticking keeps the entry and flips <c>Enabled</c> only.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "LogAnalytics")]
[Trait("Category", "AppInsights")]
[Trait("Category", "FileShare")]
public sealed class AzureResourceInitFlowTests : IDisposable
{
    private const string Tenant1 = "11111111-aaaa-aaaa-aaaa-111111111111";
    private const string Tenant2 = "22222222-bbbb-bbbb-bbbb-222222222222";
    private const string Sub1 = "sub-1";
    private const string Sub2 = "sub-2";

    private readonly string _testDirectory;
    private readonly string _registryPath;
    private readonly AzureResourceRegistry _registry;
    private readonly Mock<IAzureTenantCredentialStore> _tenants = new();
    private readonly Mock<IAzureArmDiscovery> _discovery = new();
    private readonly List<AzureTenantInfo> _knownTenants = new();

    public AzureResourceInitFlowTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"pks-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _registryPath = Path.Combine(_testDirectory, "azure-resources.json");
        _registry = new AzureResourceRegistry(CreateConfigMock().Object, FakeSecretResolver.Empty, _registryPath);

        _tenants.Setup(t => t.ListTenantsAsync()).ReturnsAsync(() => _knownTenants.ToList());
        _tenants.Setup(t => t.HasTenantAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => _knownTenants.Any(k => string.Equals(k.TenantId, id, StringComparison.OrdinalIgnoreCase)));
        _tenants.Setup(t => t.LoginAsync(It.IsAny<string?>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? idOrEmail, IAnsiConsole _, CancellationToken _) =>
            {
                var id = idOrEmail is null || idOrEmail.Contains('@') ? Tenant1 : idOrEmail;
                var info = new AzureTenantInfo(id, "Contoso", "user@contoso.com", DateTime.UtcNow, DateTime.UtcNow);
                if (!_knownTenants.Any(k => k.TenantId == id)) _knownTenants.Add(info);
                return info;
            });

        // Default discovery: nothing anywhere. Tests add what they need.
        _discovery.Setup(d => d.ListSubscriptionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AzureSubscription>());
        _discovery.Setup(d => d.ListLogAnalyticsWorkspacesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LogAnalyticsWorkspace>());
        _discovery.Setup(d => d.ListAppInsightsResourcesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AppInsightsComponent>());
        _discovery.Setup(d => d.ListStorageAccountsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StorageAccountInfo>());
        _discovery.Setup(d => d.ListCommunicationServicesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CommunicationServiceResource>());
        _discovery.Setup(d => d.ListAcsPhoneNumbersAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AcsPhoneNumber>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, recursive: true); } catch { /* best effort */ }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────

    private static Mock<IConfigurationService> CreateConfigMock()
    {
        var store = new Dictionary<string, string?>();
        var mock = new Mock<IConfigurationService>();
        mock.Setup(m => m.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => store.TryGetValue(key, out var v) ? v : null);
        mock.Setup(m => m.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<string, string, bool, bool>((key, value, _, _) => store[key] = value)
            .Returns(Task.CompletedTask);
        mock.Setup(m => m.DeleteAsync(It.IsAny<string>()))
            .Callback<string>(key => store.Remove(key))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private void KnowTenant(string tenantId)
        => _knownTenants.Add(new AzureTenantInfo(tenantId, "Tenant " + tenantId[..8], null, DateTime.UtcNow, DateTime.UtcNow));

    private void Subscriptions(string tenantId, params string[] subscriptionIds)
        => _discovery.Setup(d => d.ListSubscriptionsAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscriptionIds.Select(s => new AzureSubscription
            {
                SubscriptionId = s, DisplayName = "Sub " + s, State = "Enabled", TenantId = tenantId,
            }).ToList());

    private static LogAnalyticsWorkspace Workspace(string name, string customerId, string sub = Sub1) => new()
    {
        Id = $"/subscriptions/{sub}/resourceGroups/rg-{name}/providers/Microsoft.OperationalInsights/workspaces/{name}",
        Name = name,
        Location = "westeurope",
        Properties = new LogAnalyticsWorkspaceProperties { CustomerId = customerId },
    };

    private void Workspaces(string tenantId, string sub, params LogAnalyticsWorkspace[] workspaces)
        => _discovery.Setup(d => d.ListLogAnalyticsWorkspacesAsync(tenantId, sub, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workspaces.ToList());

    private AzureResourceInitFlow CreateFlow() => new(_tenants.Object, _discovery.Object, _registry);

    private static TestConsole InteractiveConsole()
    {
        var console = new TestConsole().Width(200);
        console.Interactive();
        return console;
    }

    private static TestConsole NonInteractiveConsole() => new TestConsole().Width(200);

    private async Task<AzureResourceEntry> EntryAsync(string nameOrKey, AzureResourceKind kind = AzureResourceKind.LogAnalytics)
    {
        var entry = await _registry.FindAsync(kind, nameOrKey);
        entry.Should().NotBeNull($"the registry should hold '{nameOrKey}'");
        return entry!;
    }

    // ── auth ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoTenantsInStore_LogsInOnce_WithoutTenantHint()
    {
        var console = NonInteractiveConsole();

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions(), console);

        rc.Should().Be(0);
        _tenants.Verify(t => t.LoginAsync(null, console, It.IsAny<CancellationToken>()), Times.Once);
        _tenants.Verify(t => t.LoginAsync(It.IsAny<string?>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TenantPresent_WithoutReauth_NeverOpensBrowser()
    {
        KnowTenant(Tenant1);

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions(), NonInteractiveConsole());

        rc.Should().Be(0);
        _tenants.Verify(t => t.LoginAsync(It.IsAny<string?>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reauth_LogsIn_EvenWhenTenantPresent()
    {
        KnowTenant(Tenant1);

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions { Reauth = true }, NonInteractiveConsole());

        rc.Should().Be(0);
        _tenants.Verify(t => t.LoginAsync(It.IsAny<string?>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reauth_WithSeveralTenantsAndNoTenantOption_LogsInToEachKnownTenant()
    {
        KnowTenant(Tenant1);
        KnowTenant(Tenant2);

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions { Reauth = true }, NonInteractiveConsole());

        rc.Should().Be(0);
        _tenants.Verify(t => t.LoginAsync(Tenant1, It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Once);
        _tenants.Verify(t => t.LoginAsync(Tenant2, It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reauth_WithTenant_LogsInToThatTenantOnly()
    {
        KnowTenant(Tenant1);
        KnowTenant(Tenant2);

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics,
            new AzureResourceInitOptions { Reauth = true, Tenant = Tenant2 }, NonInteractiveConsole());

        rc.Should().Be(0);
        _tenants.Verify(t => t.LoginAsync(Tenant2, It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Once);
        _tenants.Verify(t => t.LoginAsync(It.IsAny<string?>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TenantOption_ForUnknownTenant_LogsInToIt()
    {
        KnowTenant(Tenant1);

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics,
            new AzureResourceInitOptions { Tenant = Tenant2 }, NonInteractiveConsole());

        rc.Should().Be(0);
        _tenants.Verify(t => t.LoginAsync(Tenant2, It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TenantOption_ForKnownTenant_DoesNotLogIn_AndDiscoversOnlyThatTenant()
    {
        KnowTenant(Tenant1);
        KnowTenant(Tenant2);

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics,
            new AzureResourceInitOptions { Tenant = Tenant2 }, NonInteractiveConsole());

        rc.Should().Be(0);
        _tenants.Verify(t => t.LoginAsync(It.IsAny<string?>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Never);
        _discovery.Verify(d => d.ListSubscriptionsAsync(Tenant2, It.IsAny<CancellationToken>()), Times.Once);
        _discovery.Verify(d => d.ListSubscriptionsAsync(Tenant1, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── discovery + prompt ───────────────────────────────────────────────────────

    [Fact]
    public async Task Discovery_UpsertsEntries_AndPreviouslyDisabledEntriesStayUnchecked()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        var prod = Workspace("law-prod", "aaaaaaaa-0000-0000-0000-000000000001");
        var dev = Workspace("law-dev", "aaaaaaaa-0000-0000-0000-000000000002");
        Workspaces(Tenant1, Sub1, prod, dev);
        await _registry.UpsertAsync(new[]
        {
            new AzureResourceEntry
            {
                Kind = AzureResourceKind.LogAnalytics, Key = prod.Properties.CustomerId, Name = prod.Name,
                ResourceId = prod.Id, TenantId = Tenant1, SubscriptionId = Sub1, Enabled = false,
            },
        });

        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Enter);   // save without touching anything

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions(), console);

        rc.Should().Be(0);
        var all = await _registry.ListAsync(AzureResourceKind.LogAnalytics);
        all.Should().HaveCount(2);
        (await EntryAsync("law-prod")).Enabled.Should().BeFalse("a previously disabled entry stays unchecked");
        (await EntryAsync("law-dev")).Enabled.Should().BeFalse("newly discovered entries start unchecked");
        (await EntryAsync("law-dev")).ResourceGroup.Should().Be("rg-law-dev");
        (await EntryAsync("law-dev")).Key.Should().Be(dev.Properties.CustomerId);
        console.Output.Should().Contain("law-prod").And.Contain("law-dev");
    }

    [Fact]
    public async Task Discovery_KeepsEnabledFlag_OfAlreadyEnabledEntries()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        var prod = Workspace("law-prod", "aaaaaaaa-0000-0000-0000-000000000001");
        Workspaces(Tenant1, Sub1, prod);
        await _registry.UpsertAsync(new[]
        {
            new AzureResourceEntry
            {
                Kind = AzureResourceKind.LogAnalytics, Key = prod.Properties.CustomerId, Name = prod.Name,
                ResourceId = prod.Id, TenantId = Tenant1, SubscriptionId = Sub1, Enabled = true,
            },
        });

        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Enter);

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions(), console);

        rc.Should().Be(0);
        (await EntryAsync("law-prod")).Enabled.Should().BeTrue("the pre-selection mirrors the stored flag and enter keeps it");
    }

    [Fact]
    public async Task Prompt_SpaceThenEnter_FlipsOnlyTheHighlightedEntry()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        var prod = Workspace("law-prod", "aaaaaaaa-0000-0000-0000-000000000001");
        var dev = Workspace("law-dev", "aaaaaaaa-0000-0000-0000-000000000002");
        Workspaces(Tenant1, Sub1, prod, dev);

        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Spacebar);  // tick the first entry
        console.Input.PushKey(ConsoleKey.Enter);     // save

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions(), console);

        rc.Should().Be(0);
        (await EntryAsync("law-prod")).Enabled.Should().BeTrue();
        (await EntryAsync("law-dev")).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Prompt_UntickingAnEnabledEntry_DisablesItButKeepsIt()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        var prod = Workspace("law-prod", "aaaaaaaa-0000-0000-0000-000000000001");
        Workspaces(Tenant1, Sub1, prod);
        await _registry.UpsertAsync(new[]
        {
            new AzureResourceEntry
            {
                Kind = AzureResourceKind.LogAnalytics, Key = prod.Properties.CustomerId, Name = prod.Name,
                ResourceId = prod.Id, TenantId = Tenant1, SubscriptionId = Sub1, Enabled = true,
            },
        });

        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Spacebar);  // untick
        console.Input.PushKey(ConsoleKey.Enter);

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions(), console);

        rc.Should().Be(0);
        var entry = await EntryAsync("law-prod");
        entry.Enabled.Should().BeFalse();
        (await _registry.ListAsync(AzureResourceKind.LogAnalytics)).Should().HaveCount(1, "unchecking never removes the entry");
    }

    [Fact]
    public async Task Prompt_ShowsEntriesThatAreNoLongerDiscoverable_AsNotFound()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        Workspaces(Tenant1, Sub1, Workspace("law-prod", "aaaaaaaa-0000-0000-0000-000000000001"));
        await _registry.UpsertAsync(new[]
        {
            new AzureResourceEntry
            {
                Kind = AzureResourceKind.LogAnalytics, Key = "bbbbbbbb-0000-0000-0000-000000000009", Name = "law-gone",
                ResourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/law-gone",
                TenantId = Tenant1, SubscriptionId = Sub1, Enabled = true,
            },
        });

        var console = InteractiveConsole();
        console.Input.PushKey(ConsoleKey.Enter);

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions(), console);

        rc.Should().Be(0);
        console.Output.Should().Contain("law-gone").And.Contain("not found");
        (await _registry.ListAsync(AzureResourceKind.LogAnalytics)).Should().HaveCount(2, "stale entries are kept");
    }

    [Fact]
    public async Task NonInteractiveConsole_SkipsPrompt_PrintsEnabledTable_AndHint()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        Workspaces(Tenant1, Sub1, Workspace("law-prod", "aaaaaaaa-0000-0000-0000-000000000001"));
        var console = NonInteractiveConsole();   // no input pushed: a prompt would throw

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions(), console);

        rc.Should().Be(0);
        (await _registry.ListAsync(AzureResourceKind.LogAnalytics)).Should().HaveCount(1, "discovery still runs");
        console.Output.Should().Contain("--enable");
    }

    // ── non-interactive shortcuts ────────────────────────────────────────────────

    private async Task SeedTwoEntriesAsync()
    {
        await _registry.UpsertAsync(new[]
        {
            new AzureResourceEntry
            {
                Kind = AzureResourceKind.LogAnalytics, Key = "aaaaaaaa-0000-0000-0000-000000000001", Name = "law-prod",
                ResourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/law-prod",
                TenantId = Tenant1, SubscriptionId = Sub1, SubscriptionName = "Sub sub-1", Enabled = true,
            },
            new AzureResourceEntry
            {
                Kind = AzureResourceKind.LogAnalytics, Key = "aaaaaaaa-0000-0000-0000-000000000002", Name = "law-dev",
                ResourceId = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/law-dev",
                TenantId = Tenant1, SubscriptionId = Sub1, SubscriptionName = "Sub sub-1", Enabled = false,
            },
        });
    }

    [Fact]
    public async Task EnableAndDisable_FlipFlags_WithoutDiscoveryOrPrompt()
    {
        KnowTenant(Tenant1);
        await SeedTwoEntriesAsync();
        var console = InteractiveConsole();   // no input: a prompt would throw

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics,
            new AzureResourceInitOptions { Enable = { "law-dev" }, Disable = { "law-prod" } }, console);

        rc.Should().Be(0);
        (await EntryAsync("law-dev")).Enabled.Should().BeTrue();
        (await EntryAsync("law-prod")).Enabled.Should().BeFalse();
        _discovery.Verify(d => d.ListSubscriptionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        console.Output.Should().Contain("law-dev");
    }

    [Fact]
    public async Task EnableAndDisable_WithNoTenantSignedIn_NeverOpenTheBrowser()
    {
        await SeedTwoEntriesAsync();   // e.g. entries registered by --workspace <guid>

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics,
            new AzureResourceInitOptions { Disable = { "law-prod" } }, NonInteractiveConsole());

        rc.Should().Be(0);
        (await EntryAsync("law-prod")).Enabled.Should().BeFalse();
        _tenants.Verify(t => t.LoginAsync(It.IsAny<string?>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enable_WithReauth_StillSignsIn()
    {
        KnowTenant(Tenant1);
        await SeedTwoEntriesAsync();

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics,
            new AzureResourceInitOptions { Reauth = true, Enable = { "law-dev" } }, NonInteractiveConsole());

        rc.Should().Be(0);
        (await EntryAsync("law-dev")).Enabled.Should().BeTrue();
        _tenants.Verify(t => t.LoginAsync(Tenant1, It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Once);
        _discovery.Verify(d => d.ListSubscriptionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enable_ByKey_Works()
    {
        KnowTenant(Tenant1);
        await SeedTwoEntriesAsync();

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics,
            new AzureResourceInitOptions { Enable = { "aaaaaaaa-0000-0000-0000-000000000002" } }, InteractiveConsole());

        rc.Should().Be(0);
        (await EntryAsync("law-dev")).Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Enable_UnknownName_ReturnsOne_AndListsKnownNames()
    {
        KnowTenant(Tenant1);
        await SeedTwoEntriesAsync();
        var console = InteractiveConsole();

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics,
            new AzureResourceInitOptions { Enable = { "law-nope" } }, console);

        rc.Should().Be(1);
        console.Output.Should().Contain("law-nope").And.Contain("law-prod").And.Contain("law-dev");
    }

    [Fact]
    public async Task List_PrintsAllEntries_WithoutDiscovery()
    {
        KnowTenant(Tenant1);
        await SeedTwoEntriesAsync();
        var console = InteractiveConsole();

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions { List = true }, console);

        rc.Should().Be(0);
        console.Output.Should().Contain("law-prod").And.Contain("law-dev").And.Contain("aaaaaaaa-0000-0000-0000-000000000002");
        _discovery.Verify(d => d.ListSubscriptionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _tenants.Verify(t => t.LoginAsync(It.IsAny<string?>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnableAfterDiscovery_EnablesTheNamedEntry_WithoutPrompt()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        Workspaces(Tenant1, Sub1,
            Workspace("law-prod", "aaaaaaaa-0000-0000-0000-000000000001"),
            Workspace("law-dev", "aaaaaaaa-0000-0000-0000-000000000002"));
        var console = InteractiveConsole();   // no input: a prompt would throw

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics,
            new AzureResourceInitOptions { EnableAfterDiscovery = { "law-dev" } }, console);

        rc.Should().Be(0);
        (await EntryAsync("law-dev")).Enabled.Should().BeTrue();
        (await EntryAsync("law-prod")).Enabled.Should().BeFalse();
    }

    // ── dead tokens and partial failures ─────────────────────────────────────────

    [Fact]
    public async Task DeadTokenTenant_DeclinedSignIn_IsSkipped_OtherTenantStillDiscovered()
    {
        KnowTenant(Tenant1);
        KnowTenant(Tenant2);
        _discovery.Setup(d => d.ListSubscriptionsAsync(Tenant1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AzureAuthExpiredException(Tenant1));
        Subscriptions(Tenant2, Sub2);
        _discovery.Setup(d => d.ListLogAnalyticsWorkspacesAsync(Tenant2, Sub2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LogAnalyticsWorkspace> { Workspace("law-two", "cccccccc-0000-0000-0000-000000000001", Sub2) });

        var console = InteractiveConsole();
        console.Input.PushTextWithEnter("n");       // "Tenant … needs sign-in — sign in now?"
        console.Input.PushKey(ConsoleKey.Enter);    // save the selection

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions(), console);

        rc.Should().Be(0);
        _tenants.Verify(t => t.LoginAsync(It.IsAny<string?>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Never);
        var all = await _registry.ListAsync(AzureResourceKind.LogAnalytics);
        all.Should().ContainSingle(e => e.Name == "law-two");
        console.Output.Should().Contain("needs sign-in");
    }

    [Fact]
    public async Task DeadTokenTenant_AcceptedSignIn_LogsInAndRetriesThatTenant()
    {
        KnowTenant(Tenant1);
        _discovery.SetupSequence(d => d.ListSubscriptionsAsync(Tenant1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AzureAuthExpiredException(Tenant1))
            .ReturnsAsync(new List<AzureSubscription> { new() { SubscriptionId = Sub1, DisplayName = "Sub", TenantId = Tenant1 } });
        Workspaces(Tenant1, Sub1, Workspace("law-prod", "aaaaaaaa-0000-0000-0000-000000000001"));

        var console = InteractiveConsole();
        console.Input.PushTextWithEnter("y");
        console.Input.PushKey(ConsoleKey.Enter);

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions(), console);

        rc.Should().Be(0);
        _tenants.Verify(t => t.LoginAsync(Tenant1, It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Once);
        (await _registry.ListAsync(AzureResourceKind.LogAnalytics)).Should().ContainSingle(e => e.Name == "law-prod");
    }

    [Fact]
    public async Task FailingSubscription_IsWarnedAndSkipped_OthersContinue()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1, Sub2);
        _discovery.Setup(d => d.ListLogAnalyticsWorkspacesAsync(Tenant1, Sub1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("403 Forbidden"));
        Workspaces(Tenant1, Sub2, Workspace("law-two", "cccccccc-0000-0000-0000-000000000001", Sub2));
        var console = NonInteractiveConsole();

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics, new AzureResourceInitOptions(), console);

        rc.Should().Be(0);
        (await _registry.ListAsync(AzureResourceKind.LogAnalytics)).Should().ContainSingle(e => e.Name == "law-two");
        console.Output.Should().Contain(Sub1);
    }

    [Fact]
    public async Task SubscriptionOption_LimitsDiscoveryToThatSubscription()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1, Sub2);
        Workspaces(Tenant1, Sub1, Workspace("law-one", "aaaaaaaa-0000-0000-0000-000000000001"));
        Workspaces(Tenant1, Sub2, Workspace("law-two", "cccccccc-0000-0000-0000-000000000001", Sub2));

        var rc = await CreateFlow().RunAsync(AzureResourceKind.LogAnalytics,
            new AzureResourceInitOptions { Subscription = Sub2 }, NonInteractiveConsole());

        rc.Should().Be(0);
        (await _registry.ListAsync(AzureResourceKind.LogAnalytics)).Should().ContainSingle(e => e.Name == "law-two");
        _discovery.Verify(d => d.ListLogAnalyticsWorkspacesAsync(Tenant1, Sub1, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── other kinds ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AppInsights_MapsAppIdAsKey()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        _discovery.Setup(d => d.ListAppInsightsResourcesAsync(Tenant1, Sub1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AppInsightsComponent>
            {
                new()
                {
                    Id = "/subscriptions/sub-1/resourceGroups/rg-ai/providers/microsoft.insights/components/ai-prod",
                    Name = "ai-prod",
                    Properties = new AppInsightsComponentProperties { AppId = "dddddddd-0000-0000-0000-000000000001" },
                },
            });

        var rc = await CreateFlow().RunAsync(AzureResourceKind.AppInsights, new AzureResourceInitOptions(), NonInteractiveConsole());

        rc.Should().Be(0);
        var entry = await EntryAsync("ai-prod", AzureResourceKind.AppInsights);
        entry.Key.Should().Be("dddddddd-0000-0000-0000-000000000001");
        entry.ResourceGroup.Should().Be("rg-ai");
        entry.TenantId.Should().Be(Tenant1);
        entry.SubscriptionId.Should().Be(Sub1);
        (await _registry.ListAsync(AzureResourceKind.LogAnalytics)).Should().BeEmpty();
    }

    [Fact]
    public async Task Storage_MapsAccountNameAsKeyAndName()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        _discovery.Setup(d => d.ListStorageAccountsAsync(Tenant1, Sub1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StorageAccountInfo>
            {
                new()
                {
                    Id = "/subscriptions/sub-1/resourceGroups/rg-st/providers/Microsoft.Storage/storageAccounts/stprod",
                    Name = "stprod", Kind = "StorageV2",
                },
            });

        var rc = await CreateFlow().RunAsync(AzureResourceKind.Storage, new AzureResourceInitOptions(), NonInteractiveConsole());

        rc.Should().Be(0);
        var entry = await EntryAsync("stprod", AzureResourceKind.Storage);
        entry.Key.Should().Be("stprod");
        entry.Name.Should().Be("stprod");
        entry.ResourceGroup.Should().Be("rg-st");
    }

    // ── Communication Services (SMS senders) ───────────────────────────────────

    private static CommunicationServiceResource AcsResource(string name, string sub = Sub1) => new()
    {
        Id = $"/subscriptions/{sub}/resourceGroups/rg-{name}/providers/Microsoft.Communication/communicationServices/{name}",
        Name = name,
        Location = "global",
        Properties = new CommunicationServiceProperties { HostName = $"{name}.europe.communication.azure.com", DataLocation = "Europe" },
    };

    private static AcsPhoneNumber Number(string e164, string type, string sms) => new()
    {
        Id = e164, PhoneNumber = e164, CountryCode = "DK", PhoneNumberType = type,
        Capabilities = new AcsPhoneNumberCapabilities { Sms = sms, Calling = "none" },
    };

    [Fact]
    public async Task CommunicationServices_OneEntryPerSmsCapableNumber_NoneForVoiceOnly()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        var acs = AcsResource("com-prd");
        _discovery.Setup(d => d.ListCommunicationServicesAsync(Tenant1, Sub1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CommunicationServiceResource> { acs });
        _discovery.Setup(d => d.ListAcsPhoneNumbersAsync(Tenant1, acs.Properties.HostName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AcsPhoneNumber>
            {
                Number("+4566339237", "mobile", "inbound+outbound"),
                Number("+4570000000", "geographic", "none"),
                Number("+4580000000", "tollFree", "outbound"),
            });

        var rc = await CreateFlow().RunAsync(AzureResourceKind.CommunicationServices, new AzureResourceInitOptions(), NonInteractiveConsole());

        rc.Should().Be(0);
        var all = await _registry.ListAsync(AzureResourceKind.CommunicationServices);
        all.Select(e => e.Key).Should().BeEquivalentTo(new[] { "+4566339237", "+4580000000" });

        var mobile = await EntryAsync("+4566339237", AzureResourceKind.CommunicationServices);
        mobile.Name.Should().Be("+45 66 33 92 37 · mobile · com-prd");
        mobile.Endpoint.Should().Be("com-prd.europe.communication.azure.com");
        mobile.ResourceGroup.Should().Be("rg-com-prd");
        mobile.TenantId.Should().Be(Tenant1);
        mobile.Enabled.Should().BeFalse("nothing is enabled until the user ticks it");
        mobile.ResourceId.Should().NotBe((await EntryAsync("+4580000000", AzureResourceKind.CommunicationServices)).ResourceId,
            "two senders on one resource must not collapse into one registry entry");
    }

    [Fact]
    public async Task CommunicationServices_AlphanumericSenderAddedByHand_SurvivesRediscovery()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        var acs = AcsResource("com-prd");
        _discovery.Setup(d => d.ListCommunicationServicesAsync(Tenant1, Sub1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CommunicationServiceResource> { acs });
        _discovery.Setup(d => d.ListAcsPhoneNumbersAsync(Tenant1, acs.Properties.HostName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AcsPhoneNumber> { Number("+4566339237", "mobile", "inbound+outbound") });

        var alpha = AzureResourceInitFlow.SmsSenderEntry(Tenant1, new AzureSubscription { SubscriptionId = Sub1 }, acs, "Agentics", "Agentics · alphanumeric · com-prd");
        alpha.Enabled = true;
        await _registry.UpsertAsync(new[] { alpha });

        var rc = await CreateFlow().RunAsync(AzureResourceKind.CommunicationServices, new AzureResourceInitOptions(), NonInteractiveConsole());

        rc.Should().Be(0);
        var kept = await EntryAsync("Agentics", AzureResourceKind.CommunicationServices);
        kept.Enabled.Should().BeTrue();
        kept.Endpoint.Should().Be(acs.Properties.HostName);
        (await _registry.ListAsync(AzureResourceKind.CommunicationServices)).Should().HaveCount(2);
    }

    [Fact]
    public async Task CommunicationServices_DataPlaneForbidden_WarnsAndKeepsOtherResources()
    {
        KnowTenant(Tenant1);
        Subscriptions(Tenant1, Sub1);
        var noRole = AcsResource("com-norole");
        var ok = AcsResource("com-ok");
        _discovery.Setup(d => d.ListCommunicationServicesAsync(Tenant1, Sub1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CommunicationServiceResource> { noRole, ok });
        _discovery.Setup(d => d.ListAcsPhoneNumbersAsync(Tenant1, noRole.Properties.HostName, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("forbidden", null, System.Net.HttpStatusCode.Forbidden));
        _discovery.Setup(d => d.ListAcsPhoneNumbersAsync(Tenant1, ok.Properties.HostName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AcsPhoneNumber> { Number("+4566339237", "mobile", "outbound") });

        var console = NonInteractiveConsole();
        var rc = await CreateFlow().RunAsync(AzureResourceKind.CommunicationServices, new AzureResourceInitOptions(), console);

        rc.Should().Be(0);
        console.Output.Should().Contain("com-norole").And.Contain("403");
        (await _registry.ListAsync(AzureResourceKind.CommunicationServices)).Should().ContainSingle(e => e.Key == "+4566339237");
    }
}
