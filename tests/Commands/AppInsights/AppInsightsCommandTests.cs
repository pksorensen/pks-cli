using FluentAssertions;
using Moq;
using PKS.Commands.AppInsights;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.AppInsights;

[Trait("Category", "AppInsights")]
public class AppInsightsCommandTests
{
    private static (Mock<IAppInsightsConfigService>, Mock<IAppInsightsQueryService>, TestConsole) CreateStatusMocks(
        bool isConfigured = true,
        AppInsightsConfig? config = null,
        AppInsightsConnectionResult? connectionResult = null)
    {
        var configMock = new Mock<IAppInsightsConfigService>();
        var queryMock = new Mock<IAppInsightsQueryService>();
        var console = new TestConsole();

        configMock.Setup(m => m.IsConfiguredAsync()).ReturnsAsync(isConfigured);
        configMock.Setup(m => m.GetConfigAsync()).ReturnsAsync(config ?? (isConfigured
            ? new AppInsightsConfig { AppId = "app-123", ResourceName = "My AI Resource", SubscriptionId = "sub-xyz" }
            : null));

        queryMock.Setup(m => m.TestConnectionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(connectionResult ?? new AppInsightsConnectionResult { Success = true, ResourceName = "My AI Resource" });

        return (configMock, queryMock, console);
    }

    private static (Mock<IAppInsightsConfigService>, Mock<IAzureFoundryAuthService>, TestConsole) CreateInitMocks(
        bool isConfigured = false,
        bool isAuthenticated = true,
        string? managementToken = "mgmt-token",
        List<AzureSubscription>? subscriptions = null,
        List<AppInsightsComponent>? components = null,
        FoundryAuthResult? authResult = null,
        Exception? initLoginException = null)
    {
        var configMock = new Mock<IAppInsightsConfigService>();
        var authMock = new Mock<IAzureFoundryAuthService>();
        var console = new TestConsole();

        configMock.Setup(m => m.IsConfiguredAsync()).ReturnsAsync(isConfigured);
        configMock.Setup(m => m.GetConfigAsync()).ReturnsAsync(isConfigured
            ? new AppInsightsConfig { AppId = "app-123", ResourceName = "My AI Resource" }
            : null);
        configMock.Setup(m => m.StoreConfigAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        authMock.Setup(m => m.IsAuthenticatedAsync()).ReturnsAsync(isAuthenticated);
        authMock.Setup(m => m.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(managementToken);
        authMock.Setup(m => m.DiscoverTenantAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("common");
        authMock.Setup(m => m.StoreCredentialsAsync(It.IsAny<FoundryStoredCredentials>()))
            .Returns(Task.CompletedTask);

        if (initLoginException is not null)
            authMock.Setup(m => m.InitiateLoginAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(initLoginException);
        else
            authMock.Setup(m => m.InitiateLoginAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(authResult ?? new FoundryAuthResult { RefreshToken = "refresh-tok" });

        authMock.Setup(m => m.ListSubscriptionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(subscriptions ?? [new AzureSubscription { SubscriptionId = "sub-123", DisplayName = "My Sub" }]);
        authMock.Setup(m => m.ListAppInsightsResourcesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(components ?? [new AppInsightsComponent { Name = "My AppInsights", Properties = new AppInsightsComponentProperties { AppId = "ai-app-id" } }]);

        return (configMock, authMock, console);
    }

    private static CommandContext CreateContext(string commandName = "status")
        => new(Mock.Of<IRemainingArguments>(), commandName, null);

    // -- Status command tests --

    [Fact]
    public void Status_ShowsNotConfigured_WhenNoConfig()
    {
        var (configMock, queryMock, console) = CreateStatusMocks(isConfigured: false);
        var cmd = new AppInsightsStatusCommand(configMock.Object, queryMock.Object, console);

        var result = cmd.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().Contain("not configured");
    }

    [Fact]
    public void Status_ShowsConfigDetails_WhenConfigured()
    {
        var (configMock, queryMock, console) = CreateStatusMocks(isConfigured: true);
        var cmd = new AppInsightsStatusCommand(configMock.Object, queryMock.Object, console);

        var result = cmd.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().Contain("app-123");
    }

    [Fact]
    public void Status_ShowsAzureAdAuth_WhenConfigured()
    {
        var (configMock, queryMock, console) = CreateStatusMocks(isConfigured: true);
        var cmd = new AppInsightsStatusCommand(configMock.Object, queryMock.Object, console);

        cmd.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        console.Output.Should().ContainAny("Azure AD", "foundry", "pks foundry");
    }

    [Fact]
    public void Status_ShowsConnectionStatus_WhenConfigured()
    {
        var (configMock, queryMock, console) = CreateStatusMocks(
            isConfigured: true,
            connectionResult: new AppInsightsConnectionResult { Success = true, ResourceName = "My AI Resource" });
        var cmd = new AppInsightsStatusCommand(configMock.Object, queryMock.Object, console);

        cmd.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        console.Output.Should().ContainAny("connected", "Connected", "My AI Resource");
    }

    // -- Init command tests --

    [Fact]
    public void Init_ShowsAlreadyConfigured_WhenConfiguredAndNoForce()
    {
        var (configMock, authMock, console) = CreateInitMocks(isConfigured: true);
        var cmd = new AppInsightsInitCommand(configMock.Object, authMock.Object, console);
        var settings = new AppInsightsInitCommand.Settings { Force = false };

        var result = cmd.Execute(CreateContext("init"), settings);

        result.Should().Be(0);
        console.Output.Should().ContainAny("already configured", "Already configured", "app-123");
        configMock.Verify(m => m.StoreConfigAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public void Init_TriggersAuthFlow_WhenNotAuthenticated()
    {
        var (configMock, authMock, console) = CreateInitMocks(
            isAuthenticated: false,
            subscriptions: [new AzureSubscription { SubscriptionId = "sub-001", DisplayName = "My Subscription" }],
            components: [new AppInsightsComponent { Name = "My AppInsights", Properties = new AppInsightsComponentProperties { AppId = "ai-app-id-001" } }]);
        var cmd = new AppInsightsInitCommand(configMock.Object, authMock.Object, console);

        var result = cmd.Execute(CreateContext("init"), new AppInsightsInitCommand.Settings { TenantId = "common" });

        result.Should().Be(0);
        authMock.Verify(m => m.InitiateLoginAsync("common", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        authMock.Verify(m => m.StoreCredentialsAsync(It.IsAny<FoundryStoredCredentials>()), Times.Once);
    }

    [Fact]
    public void Init_ReturnsOne_WhenAuthTimesOut()
    {
        var (configMock, authMock, console) = CreateInitMocks(
            isAuthenticated: false,
            initLoginException: new OperationCanceledException("timed out"));
        var cmd = new AppInsightsInitCommand(configMock.Object, authMock.Object, console);

        var result = cmd.Execute(CreateContext("init"), new AppInsightsInitCommand.Settings { TenantId = "common" });

        result.Should().Be(1);
        console.Output.Should().ContainAny("timed out", "Authentication timed out");
    }

    [Fact]
    public void Init_ReturnsOne_WhenNoSubscriptionsFound()
    {
        var (configMock, authMock, console) = CreateInitMocks(subscriptions: []);
        var cmd = new AppInsightsInitCommand(configMock.Object, authMock.Object, console);

        var result = cmd.Execute(CreateContext("init"), new AppInsightsInitCommand.Settings());

        result.Should().Be(1);
        console.Output.Should().ContainAny("No Azure subscriptions", "subscriptions");
    }

    [Fact]
    public void Init_ReturnsOne_WhenNoResourcesFound()
    {
        var (configMock, authMock, console) = CreateInitMocks(components: []);
        var cmd = new AppInsightsInitCommand(configMock.Object, authMock.Object, console);

        var result = cmd.Execute(CreateContext("init"), new AppInsightsInitCommand.Settings());

        result.Should().Be(1);
        console.Output.Should().ContainAny("No Application Insights", "not found");
    }

    [Fact]
    public void Init_ConfiguresSuccessfully_WhenSingleSubscriptionAndSingleResource()
    {
        var (configMock, authMock, console) = CreateInitMocks(
            subscriptions: [new AzureSubscription { SubscriptionId = "sub-001", DisplayName = "My Subscription" }],
            components: [new AppInsightsComponent { Name = "My AppInsights", Properties = new AppInsightsComponentProperties { AppId = "ai-app-id-001" } }]);
        var cmd = new AppInsightsInitCommand(configMock.Object, authMock.Object, console);

        var result = cmd.Execute(CreateContext("init"), new AppInsightsInitCommand.Settings());

        result.Should().Be(0);
        configMock.Verify(m => m.StoreConfigAsync("ai-app-id-001", "My AppInsights", "sub-001"), Times.Once);
        console.Output.Should().ContainAny("Configured", "configured", "My AppInsights");
    }
}
