using FluentAssertions;
using Moq;
using PKS.Commands.AppInsights;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.AppInsights;

[Trait("Category", "AppInsights")]
public class AppInsightsCommandTests
{
    private const string Tenant1 = "11111111-aaaa-aaaa-aaaa-111111111111";
    private const string Tenant2 = "22222222-bbbb-bbbb-bbbb-222222222222";

    private static CommandContext CreateContext(string commandName = "status")
        => new(Mock.Of<IRemainingArguments>(), commandName, null);

    private static AzureResourceEntry Entry(string name, string appId, bool enabled, string tenant = Tenant1) => new()
    {
        Kind = AzureResourceKind.AppInsights,
        Name = name,
        Key = appId,
        ResourceId = $"/subscriptions/sub-xyz/resourceGroups/rg/providers/microsoft.insights/components/{name}",
        TenantId = tenant,
        SubscriptionId = "sub-xyz",
        SubscriptionName = "My Sub",
        Enabled = enabled,
    };

    // ── status ───────────────────────────────────────────────────────────────────

    private sealed class StatusFixture
    {
        public Mock<IAzureResourceRegistry> Registry { get; } = new();
        public Mock<IAzureTenantCredentialStore> Tenants { get; } = new();
        public Mock<IAppInsightsQueryService> Query { get; } = new();
        public TestConsole Console { get; } = new TestConsole().Width(200);

        public StatusFixture(IReadOnlyList<AzureResourceEntry>? entries = null, params string[] tenants)
        {
            entries ??= new List<AzureResourceEntry>();
            Registry.Setup(r => r.ListAsync(AzureResourceKind.AppInsights)).ReturnsAsync(entries.ToList());
            Registry.Setup(r => r.ListEnabledAsync(AzureResourceKind.AppInsights)).ReturnsAsync(entries.Where(e => e.Enabled).ToList());
            Tenants.Setup(t => t.ListTenantsAsync()).ReturnsAsync(tenants
                .Select(id => new AzureTenantInfo(id, "Tenant " + id[..8], null, DateTime.UtcNow, DateTime.UtcNow)).ToList());
            Tenants.Setup(t => t.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("token");
            Query.Setup(q => q.TestConnectionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => new AppInsightsConnectionResult { Success = true, ResourceName = "res-" + id });
        }

        public AppInsightsStatusCommand Command => new(Registry.Object, Tenants.Object, Query.Object, Console);
    }

    [Fact]
    public void Status_ShowsNotConfigured_WhenNoEntries()
    {
        var f = new StatusFixture();

        var result = f.Command.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        result.Should().Be(0);
        f.Console.Output.Should().Contain("not configured");
    }

    [Fact]
    public void Status_ListsEnabledAndDisabledEntries()
    {
        var f = new StatusFixture(new[] { Entry("ai-prod", "app-123", enabled: true), Entry("ai-dev", "app-456", enabled: false) }, Tenant1);

        var result = f.Command.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        result.Should().Be(0);
        f.Console.Output.Should().Contain("ai-prod").And.Contain("app-123").And.Contain("ai-dev").And.Contain("app-456");
        f.Console.Output.Should().Contain("✓").And.Contain("✗");
    }

    [Fact]
    public void Status_TestsConnection_ForEachEnabledEntryOnly()
    {
        var f = new StatusFixture(new[] { Entry("ai-prod", "app-123", enabled: true), Entry("ai-dev", "app-456", enabled: false) }, Tenant1);

        f.Command.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        f.Query.Verify(q => q.TestConnectionAsync("app-123", It.IsAny<CancellationToken>()), Times.Once);
        f.Query.Verify(q => q.TestConnectionAsync("app-456", It.IsAny<CancellationToken>()), Times.Never);
        f.Console.Output.Should().ContainAny("connected", "Connected");
    }

    [Fact]
    public void Status_ShowsConnectionFailure()
    {
        var f = new StatusFixture(new[] { Entry("ai-prod", "app-123", enabled: true) }, Tenant1);
        f.Query.Setup(q => q.TestConnectionAsync("app-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppInsightsConnectionResult { Success = false, ErrorMessage = "403 forbidden" });

        f.Command.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        f.Console.Output.Should().Contain("403 forbidden");
    }

    [Fact]
    public void Status_ShowsSignedInAndSignInNeededTenants()
    {
        var f = new StatusFixture(new[] { Entry("ai-prod", "app-123", enabled: true) }, Tenant1, Tenant2);
        f.Tenants.Setup(t => t.GetAccessTokenAsync(Tenant2, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AzureAuthExpiredException(Tenant2));

        var result = f.Command.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        result.Should().Be(0);
        f.Console.Output.Should().Contain("signed in");
        f.Console.Output.Should().Contain("sign-in needed").And.Contain($"pks appinsights init --reauth {Tenant2}");
        f.Console.Output.Should().NotContain("token", "the probe result is never printed");
    }

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

        public AppInsightsInitCommand Command => new(Flow.Object, Console);
    }

    [Fact]
    public void Init_RunsTheSharedFlow_ForAppInsights()
    {
        var f = new InitFixture();

        var result = f.Command.Execute(CreateContext("init"), new AppInsightsInitCommand.Settings());

        result.Should().Be(0);
        f.Kind.Should().Be(AzureResourceKind.AppInsights);
        f.Options.Should().NotBeNull();
        f.Options!.Reauth.Should().BeFalse();
        f.Options.Tenant.Should().BeNull();
        f.Options.List.Should().BeFalse();
        f.Options.Enable.Should().BeEmpty();
        f.Options.Disable.Should().BeEmpty();
    }

    [Fact]
    public void Init_MapsSettingsToOptions()
    {
        var f = new InitFixture();
        var settings = new AppInsightsInitCommand.Settings
        {
            Tenant = Tenant1,
            Subscription = "sub-001",
            Enable = new[] { "ai-prod", "ai-dev" },
            Disable = new[] { "ai-old" },
            List = true,
        };

        f.Command.Execute(CreateContext("init"), settings);

        f.Options!.Tenant.Should().Be(Tenant1);
        f.Options.Subscription.Should().Be("sub-001");
        f.Options.Enable.Should().Equal("ai-prod", "ai-dev");
        f.Options.Disable.Should().Equal("ai-old");
        f.Options.List.Should().BeTrue();
    }

    [Fact]
    public void Init_ReauthFlag_MapsToReauth_AndOptionalTenantValue()
    {
        var f = new InitFixture();

        f.Command.Execute(CreateContext("init"), new AppInsightsInitCommand.Settings
        {
            Reauth = new FlagValue<string> { IsSet = true, Value = Tenant2 },
        });

        f.Options!.Reauth.Should().BeTrue();
        f.Options.Tenant.Should().Be(Tenant2, "`--reauth <tenant>` is the spelling the expired-token message tells users to run");
    }

    [Fact]
    public void Init_Force_IsPlainInit_AndNeverClearsCredentials()
    {
        var f = new InitFixture();

        f.Command.Execute(CreateContext("init"), new AppInsightsInitCommand.Settings { Force = true });

        f.Options!.Reauth.Should().BeFalse("--force is kept for backward compatibility only");
        // The command must not even be able to reach the Foundry credential store: the old
        // implementation's `--force` called IAzureFoundryAuthService.ClearCredentialsAsync().
        typeof(AppInsightsInitCommand).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Should().NotContain(p => p.ParameterType == typeof(IAzureFoundryAuthService));
    }

    [Fact]
    public void Init_ReturnsTheFlowsExitCode()
    {
        var f = new InitFixture(returnCode: 1);

        var result = f.Command.Execute(CreateContext("init"), new AppInsightsInitCommand.Settings { Enable = new[] { "nope" } });

        result.Should().Be(1);
    }
}
