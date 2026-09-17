using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Security;
using PKS.Infrastructure;
using PKS.Commands.LogAnalytics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.LogAnalytics;

[Trait("Category", "Unit")]
[Trait("Category", "LogAnalytics")]
public sealed class LogAnalyticsCommandTests : IDisposable
{
    private const string Tenant1 = "11111111-aaaa-aaaa-aaaa-111111111111";
    private const string Tenant2 = "22222222-bbbb-bbbb-bbbb-222222222222";

    private readonly string _testDirectory;
    private readonly AzureResourceRegistry _registry;

    public LogAnalyticsCommandTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"pks-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        var config = new Mock<IConfigurationService>();
        config.Setup(m => m.GetAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        config.Setup(m => m.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        config.Setup(m => m.DeleteAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        _registry = new AzureResourceRegistry(config.Object, FakeSecretResolver.Empty, Path.Combine(_testDirectory, "azure-resources.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, recursive: true); } catch { /* best effort */ }
    }

    private static CommandContext CreateContext(string commandName)
        => new(Mock.Of<IRemainingArguments>(), commandName, null);

    private static AzureResourceEntry Entry(string name, string key, bool enabled, string tenant = Tenant1) => new()
    {
        Kind = AzureResourceKind.LogAnalytics,
        Name = name,
        Key = key,
        ResourceId = $"/subscriptions/sub-xyz/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/{name}",
        TenantId = tenant,
        SubscriptionId = "sub-xyz",
        SubscriptionName = "My Sub",
        Enabled = enabled,
    };

    // ── init ─────────────────────────────────────────────────────────────────────

    private sealed class InitFixture
    {
        public Mock<IAzureResourceInitFlow> Flow { get; } = new();
        public TestConsole Console { get; } = new TestConsole().Width(200);
        public AzureResourceKind? Kind { get; private set; }
        public AzureResourceInitOptions? Options { get; private set; }

        public InitFixture(int returnCode = 0)
        {
            Flow.Setup(f => f.RunAsync(It.IsAny<AzureResourceKind>(), It.IsAny<AzureResourceInitOptions>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()))
                .Callback<AzureResourceKind, AzureResourceInitOptions, IAnsiConsole, CancellationToken>((k, o, _, _) => { Kind = k; Options = o; })
                .ReturnsAsync(returnCode);
        }
    }

    private LogAnalyticsInitCommand InitCommand(InitFixture f) => new(f.Flow.Object, _registry, f.Console);

    [Fact]
    public void Init_RunsTheSharedFlow_ForLogAnalytics()
    {
        var f = new InitFixture();

        var result = InitCommand(f).Execute(CreateContext("init"), new LogAnalyticsInitCommand.Settings());

        result.Should().Be(0);
        f.Kind.Should().Be(AzureResourceKind.LogAnalytics);
        f.Options!.Reauth.Should().BeFalse();
        f.Options.EnableAfterDiscovery.Should().BeEmpty();
    }

    [Fact]
    public void Init_MapsSettingsToOptions()
    {
        var f = new InitFixture();

        InitCommand(f).Execute(CreateContext("init"), new LogAnalyticsInitCommand.Settings
        {
            Tenant = "user@contoso.com",
            Subscription = "sub-001",
            Enable = new[] { "law-prod" },
            Disable = new[] { "law-dev" },
            List = true,
            Reauth = new FlagValue<string> { IsSet = true },
        });

        f.Options!.Tenant.Should().Be("user@contoso.com");
        f.Options.Subscription.Should().Be("sub-001");
        f.Options.Enable.Should().Equal("law-prod");
        f.Options.Disable.Should().Equal("law-dev");
        f.Options.List.Should().BeTrue();
        f.Options.Reauth.Should().BeTrue();
    }

    [Fact]
    public void Init_ReauthWithValue_SetsTenant()
    {
        var f = new InitFixture();

        InitCommand(f).Execute(CreateContext("init"), new LogAnalyticsInitCommand.Settings
        {
            Reauth = new FlagValue<string> { IsSet = true, Value = Tenant2 },
        });

        f.Options!.Reauth.Should().BeTrue();
        f.Options.Tenant.Should().Be(Tenant2);
    }

    [Fact]
    public void Init_Force_IsPlainInit_AndNeverClearsCredentials()
    {
        var f = new InitFixture();

        InitCommand(f).Execute(CreateContext("init"), new LogAnalyticsInitCommand.Settings { Force = true });

        f.Options!.Reauth.Should().BeFalse();
        typeof(LogAnalyticsInitCommand).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Should().NotContain(p => p.ParameterType == typeof(IAzureFoundryAuthService));
    }

    [Fact]
    public async Task Init_WorkspaceGuid_RegistersEnabledEntryDirectly_WithoutDiscovery()
    {
        var f = new InitFixture();
        var guid = "aaaaaaaa-0000-0000-0000-000000000001";

        var result = InitCommand(f).Execute(CreateContext("init"), new LogAnalyticsInitCommand.Settings { Workspace = guid, Subscription = "sub-001" });

        result.Should().Be(0);
        f.Flow.Verify(x => x.RunAsync(It.IsAny<AzureResourceKind>(), It.IsAny<AzureResourceInitOptions>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Never);
        var entry = await _registry.FindAsync(AzureResourceKind.LogAnalytics, guid);
        entry.Should().NotBeNull();
        entry!.Enabled.Should().BeTrue();
        entry.Key.Should().Be(guid);
        entry.Name.Should().Be(guid);
        entry.TenantId.Should().BeNull();
        entry.SubscriptionId.Should().Be("sub-001");
        f.Console.Output.Should().Contain(guid);
    }

    [Fact]
    public async Task Init_WorkspaceGuid_ReenablesAnExistingDisabledEntry()
    {
        var f = new InitFixture();
        var guid = "aaaaaaaa-0000-0000-0000-000000000001";
        await _registry.UpsertAsync(new[] { new AzureResourceEntry { Kind = AzureResourceKind.LogAnalytics, Key = guid, Name = guid, Enabled = false } });

        InitCommand(f).Execute(CreateContext("init"), new LogAnalyticsInitCommand.Settings { Workspace = guid });

        (await _registry.FindAsync(AzureResourceKind.LogAnalytics, guid))!.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Init_WorkspaceName_IsEnabledAfterDiscovery()
    {
        var f = new InitFixture();

        InitCommand(f).Execute(CreateContext("init"), new LogAnalyticsInitCommand.Settings { Workspace = "law-prod" });

        f.Kind.Should().Be(AzureResourceKind.LogAnalytics);
        f.Options!.EnableAfterDiscovery.Should().Equal("law-prod");
        f.Options.Enable.Should().BeEmpty();
    }

    // ── status ───────────────────────────────────────────────────────────────────

    private sealed class StatusFixture
    {
        public Mock<IAzureResourceRegistry> Registry { get; } = new();
        public Mock<IAzureTenantCredentialStore> Tenants { get; } = new();
        public Mock<ILogAnalyticsQueryService> Query { get; } = new();
        public TestConsole Console { get; } = new TestConsole().Width(200);

        public StatusFixture(IReadOnlyList<AzureResourceEntry>? entries = null, params string[] tenants)
        {
            entries ??= new List<AzureResourceEntry>();
            Registry.Setup(r => r.ListAsync(AzureResourceKind.LogAnalytics)).ReturnsAsync(entries.ToList());
            Registry.Setup(r => r.ListEnabledAsync(AzureResourceKind.LogAnalytics)).ReturnsAsync(entries.Where(e => e.Enabled).ToList());
            Tenants.Setup(t => t.ListTenantsAsync()).ReturnsAsync(tenants
                .Select(id => new AzureTenantInfo(id, "Tenant " + id[..8], null, DateTime.UtcNow, DateTime.UtcNow)).ToList());
            Tenants.Setup(t => t.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("token");
            Query.Setup(q => q.TestConnectionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => new LogAnalyticsConnectionResult { Success = true, WorkspaceName = "ws-" + id });
        }

        public LogAnalyticsStatusCommand Command => new(Registry.Object, Tenants.Object, Query.Object, Console);
    }

    [Fact]
    public void Status_ShowsNotConfigured_WhenNoEntries()
    {
        var f = new StatusFixture();

        var result = f.Command.Execute(CreateContext("status"), new LogAnalyticsStatusCommand.Settings());

        result.Should().Be(0);
        f.Console.Output.Should().Contain("not configured");
    }

    [Fact]
    public void Status_ListsEnabledAndDisabledEntries_AndTestsOnlyEnabled()
    {
        var f = new StatusFixture(new[]
        {
            Entry("law-prod", "aaaaaaaa-0000-0000-0000-000000000001", enabled: true),
            Entry("law-dev", "aaaaaaaa-0000-0000-0000-000000000002", enabled: false),
        }, Tenant1);

        var result = f.Command.Execute(CreateContext("status"), new LogAnalyticsStatusCommand.Settings());

        result.Should().Be(0);
        f.Console.Output.Should().Contain("law-prod").And.Contain("law-dev").And.Contain("✓").And.Contain("✗");
        f.Query.Verify(q => q.TestConnectionAsync("aaaaaaaa-0000-0000-0000-000000000001", It.IsAny<CancellationToken>()), Times.Once);
        f.Query.Verify(q => q.TestConnectionAsync("aaaaaaaa-0000-0000-0000-000000000002", It.IsAny<CancellationToken>()), Times.Never);
        f.Console.Output.Should().ContainAny("connected", "Connected");
    }

    [Fact]
    public void Status_ShowsSignInNeededTenant()
    {
        var f = new StatusFixture(new[] { Entry("law-prod", "aaaaaaaa-0000-0000-0000-000000000001", enabled: true) }, Tenant1, Tenant2);
        f.Tenants.Setup(t => t.GetAccessTokenAsync(Tenant2, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AzureAuthExpiredException(Tenant2));

        f.Command.Execute(CreateContext("status"), new LogAnalyticsStatusCommand.Settings());

        f.Console.Output.Should().Contain("signed in");
        f.Console.Output.Should().Contain("sign-in needed").And.Contain($"pks loganalytics init --reauth {Tenant2}");
        f.Tenants.Verify(t => t.GetAccessTokenAsync(It.IsAny<string>(), "https://management.azure.com/.default", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
