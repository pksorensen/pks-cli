using Xunit;
using Moq;
using FluentAssertions;
using PKS.Commands.FileShares;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Testing;
using Spectre.Console.Cli;

namespace PKS.CLI.Tests.Commands;

public class FileShareCommandTests
{
    private const string Tenant1 = "11111111-aaaa-aaaa-aaaa-111111111111";
    private const string Tenant2 = "22222222-bbbb-bbbb-bbbb-222222222222";

    private static Mock<IFileShareProvider> CreateProviderMock(
        string key = "azure-fileshare",
        string name = "Azure File Share",
        bool authenticated = false)
    {
        var mock = new Mock<IFileShareProvider>();
        mock.Setup(p => p.ProviderKey).Returns(key);
        mock.Setup(p => p.ProviderName).Returns(name);
        mock.Setup(p => p.IsAuthenticatedAsync()).ReturnsAsync(authenticated);
        mock.Setup(p => p.AuthenticateAsync(It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mock.Setup(p => p.ListResourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StorageResource>());
        return mock;
    }

    private static FileShareProviderRegistry CreateRegistry(params IFileShareProvider[] providers)
        => new FileShareProviderRegistry(providers);

    private static Mock<IAzureResourceRegistry> CreateResourceRegistry(params AzureResourceEntry[] entries)
    {
        var mock = new Mock<IAzureResourceRegistry>();
        mock.Setup(r => r.ListAsync(AzureResourceKind.Storage)).ReturnsAsync(entries.ToList());
        mock.Setup(r => r.ListEnabledAsync(AzureResourceKind.Storage)).ReturnsAsync(entries.Where(e => e.Enabled).ToList());
        return mock;
    }

    private static Mock<IAzureTenantCredentialStore> CreateTenantStore(params string[] tenants)
    {
        var mock = new Mock<IAzureTenantCredentialStore>();
        mock.Setup(t => t.ListTenantsAsync()).ReturnsAsync(tenants
            .Select(id => new AzureTenantInfo(id, "Tenant " + id[..8], null, DateTime.UtcNow, DateTime.UtcNow)).ToList());
        mock.Setup(t => t.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("token");
        return mock;
    }

    private static AzureResourceEntry Storage(string account, bool enabled) => new()
    {
        Kind = AzureResourceKind.Storage,
        Name = account,
        Key = account,
        ResourceId = $"/subscriptions/sub-xyz/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/{account}",
        TenantId = Tenant1,
        SubscriptionId = "sub-xyz",
        SubscriptionName = "My Sub",
        Enabled = enabled,
    };

    private static CommandContext Context(string name) => new(Mock.Of<IRemainingArguments>(), name, null);

    // ═══════════════════════════════════════
    //  FileShareStatusCommand
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "FileShare")]
    public void Status_ShowsNoProviders_WhenRegistryEmpty()
    {
        var console = new TestConsole().Width(200);
        var cmd = new FileShareStatusCommand(CreateRegistry(), CreateResourceRegistry().Object, CreateTenantStore().Object, console);

        var result = cmd.Execute(Context("status"), new FileShareSettings());

        result.Should().Be(0);
        console.Output.Should().Contain("No file share providers registered");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Status_ShowsAuthenticatedProvider()
    {
        var provider = CreateProviderMock(authenticated: true);
        provider.Setup(p => p.ListResourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StorageResource>
            {
                new() { ProviderKey = "azure-fileshare", ProviderName = "Azure File Share", AccountName = "mystorage", ResourceName = "myshare" }
            });
        var console = new TestConsole().Width(200);
        var cmd = new FileShareStatusCommand(CreateRegistry(provider.Object), CreateResourceRegistry().Object, CreateTenantStore().Object, console);

        var result = cmd.Execute(Context("status"), new FileShareSettings());

        result.Should().Be(0);
        console.Output.Should().Contain("Azure File Share");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Status_ListsStorageEntries_EnabledAndDisabled()
    {
        var provider = CreateProviderMock(authenticated: true);
        var console = new TestConsole().Width(200);
        var cmd = new FileShareStatusCommand(
            CreateRegistry(provider.Object),
            CreateResourceRegistry(Storage("stprod", enabled: true), Storage("stdev", enabled: false)).Object,
            CreateTenantStore(Tenant1).Object,
            console);

        var result = cmd.Execute(Context("status"), new FileShareSettings());

        result.Should().Be(0);
        console.Output.Should().Contain("stprod").And.Contain("stdev").And.Contain("✓").And.Contain("✗");
        console.Output.Should().Contain("signed in");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Status_ShowsSignInNeededTenant()
    {
        var provider = CreateProviderMock(authenticated: true);
        var tenants = CreateTenantStore(Tenant1, Tenant2);
        tenants.Setup(t => t.GetAccessTokenAsync(Tenant2, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AzureAuthExpiredException(Tenant2));
        var console = new TestConsole().Width(200);
        var cmd = new FileShareStatusCommand(
            CreateRegistry(provider.Object),
            CreateResourceRegistry(Storage("stprod", enabled: true)).Object,
            tenants.Object,
            console);

        cmd.Execute(Context("status"), new FileShareSettings());

        console.Output.Should().Contain("sign-in needed").And.Contain($"pks fileshare init --reauth {Tenant2}");
    }

    // ═══════════════════════════════════════
    //  FileShareInitCommand
    // ═══════════════════════════════════════

    private sealed class FlowSpy
    {
        public Mock<IAzureResourceInitFlow> Flow { get; } = new();
        public AzureResourceKind? Kind { get; private set; }
        public AzureResourceInitOptions? Options { get; private set; }

        public FlowSpy(int returnCode = 0)
        {
            Flow.Setup(f => f.RunAsync(It.IsAny<AzureResourceKind>(), It.IsAny<AzureResourceInitOptions>(), It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()))
                .Callback<AzureResourceKind, AzureResourceInitOptions, IAnsiConsole, CancellationToken>((k, o, _, _) => { Kind = k; Options = o; })
                .ReturnsAsync(returnCode);
        }
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Init_RunsTheSharedFlow_ForStorage_AndNeverCallsProviderAuthenticate()
    {
        var console = new TestConsole().Width(200);
        var provider = CreateProviderMock(authenticated: true);
        var spy = new FlowSpy();
        var cmd = new FileShareInitCommand(CreateRegistry(provider.Object), spy.Flow.Object, console);

        var result = cmd.Execute(Context("init"), new FileShareInitCommand.Settings());

        result.Should().Be(0);
        spy.Kind.Should().Be(AzureResourceKind.Storage);
        spy.Options!.Reauth.Should().BeFalse();
        provider.Verify(p => p.AuthenticateAsync(It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Never);
        console.Output.Should().NotContain("Already authenticated", "there is no early exit any more");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Init_MapsSettingsToOptions()
    {
        var provider = CreateProviderMock();
        var spy = new FlowSpy();
        var cmd = new FileShareInitCommand(CreateRegistry(provider.Object), spy.Flow.Object, new TestConsole());

        cmd.Execute(Context("init"), new FileShareInitCommand.Settings
        {
            Reauth = new FlagValue<string> { IsSet = true, Value = Tenant2 },
            Subscription = "sub-001",
            Enable = new[] { "stprod" },
            Disable = new[] { "stdev" },
            List = true,
        });

        spy.Options!.Reauth.Should().BeTrue();
        spy.Options.Tenant.Should().Be(Tenant2);
        spy.Options.Subscription.Should().Be("sub-001");
        spy.Options.Enable.Should().Equal("stprod");
        spy.Options.Disable.Should().Equal("stdev");
        spy.Options.List.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Init_Force_IsPlainInit_AndNeverClearsCredentials()
    {
        var provider = CreateProviderMock(authenticated: true);
        var spy = new FlowSpy();
        var cmd = new FileShareInitCommand(CreateRegistry(provider.Object), spy.Flow.Object, new TestConsole());

        var result = cmd.Execute(Context("init"), new FileShareInitCommand.Settings { Force = true });

        result.Should().Be(0);
        spy.Options!.Reauth.Should().BeFalse();
        typeof(FileShareInitCommand).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Should().NotContain(p => p.ParameterType == typeof(IAzureFoundryAuthService));
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Init_ReturnsTheFlowsExitCode()
    {
        var provider = CreateProviderMock();
        var spy = new FlowSpy(returnCode: 1);
        var cmd = new FileShareInitCommand(CreateRegistry(provider.Object), spy.Flow.Object, new TestConsole());

        cmd.Execute(Context("init"), new FileShareInitCommand.Settings()).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Init_Returns1_WhenNoProviders()
    {
        var console = new TestConsole();
        var spy = new FlowSpy();
        var cmd = new FileShareInitCommand(CreateRegistry(), spy.Flow.Object, console);

        var result = cmd.Execute(Context("init"), new FileShareInitCommand.Settings());

        result.Should().Be(1);
        console.Output.Should().Contain("No file share providers");
        spy.Kind.Should().BeNull();
    }
}
