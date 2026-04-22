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
    private static (Mock<IAppInsightsConfigService>, Mock<IAppInsightsQueryService>, TestConsole) CreateMocks(
        bool isConfigured = true,
        AppInsightsConfig? config = null,
        AppInsightsConnectionResult? connectionResult = null)
    {
        var configMock = new Mock<IAppInsightsConfigService>();
        var queryMock = new Mock<IAppInsightsQueryService>();
        var console = new TestConsole();

        configMock.Setup(m => m.IsConfiguredAsync()).ReturnsAsync(isConfigured);
        configMock.Setup(m => m.GetConfigAsync()).ReturnsAsync(config ?? (isConfigured
            ? new AppInsightsConfig { AppId = "app-123", ApiKey = "key-abc", ResourceName = "My AI Resource" }
            : null));
        configMock.Setup(m => m.StoreConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        queryMock.Setup(m => m.TestConnectionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(connectionResult ?? new AppInsightsConnectionResult { Success = true, ResourceName = "My AI Resource" });

        return (configMock, queryMock, console);
    }

    private static CommandContext CreateContext(string commandName = "status")
        => new(Mock.Of<IRemainingArguments>(), commandName, null);

    // -- Status command tests --

    [Fact]
    public void Status_ShowsNotConfigured_WhenNoConfig()
    {
        var (configMock, queryMock, console) = CreateMocks(isConfigured: false);
        var cmd = new AppInsightsStatusCommand(configMock.Object, queryMock.Object, console);

        var result = cmd.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().Contain("not configured");
    }

    [Fact]
    public void Status_ShowsConfigDetails_WhenConfigured()
    {
        var (configMock, queryMock, console) = CreateMocks(isConfigured: true);
        var cmd = new AppInsightsStatusCommand(configMock.Object, queryMock.Object, console);

        var result = cmd.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().Contain("app-123");
    }

    [Fact]
    public void Status_ShowsMaskedApiKey_WhenConfigured()
    {
        var (configMock, queryMock, console) = CreateMocks(isConfigured: true);
        var cmd = new AppInsightsStatusCommand(configMock.Object, queryMock.Object, console);

        cmd.Execute(CreateContext("status"), new AppInsightsStatusCommand.Settings());

        // Should not expose full key; should show masked version
        console.Output.Should().NotContain("key-abc");
        console.Output.Should().Contain("***");
    }

    [Fact]
    public void Status_ShowsConnectionStatus_WhenConfigured()
    {
        var (configMock, queryMock, console) = CreateMocks(
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
        var (configMock, queryMock, console) = CreateMocks(isConfigured: true);
        var cmd = new AppInsightsInitCommand(configMock.Object, queryMock.Object, console);
        var settings = new AppInsightsInitCommand.Settings { Force = false };

        var result = cmd.Execute(CreateContext("init"), settings);

        result.Should().Be(0);
        console.Output.Should().ContainAny("already configured", "Already configured", "app-123");
        configMock.Verify(m => m.StoreConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Init_ReturnsZero_WhenConnectionSucceeds()
    {
        var (configMock, queryMock, console) = CreateMocks(
            isConfigured: false,
            connectionResult: new AppInsightsConnectionResult { Success = true, ResourceName = "Test Resource" });

        var cmd = new AppInsightsInitCommand(configMock.Object, queryMock.Object, console);
        var settings = new AppInsightsInitCommand.Settings
        {
            // Pre-supply credentials to skip interactive prompts
            AppId = "new-app-id",
            ApiKey = "new-api-key"
        };

        var result = cmd.Execute(CreateContext("init"), settings);

        result.Should().Be(0);
        configMock.Verify(m => m.StoreConfigAsync("new-app-id", "new-api-key", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Init_ReturnsOne_WhenConnectionFails()
    {
        var (configMock, queryMock, console) = CreateMocks(
            isConfigured: false,
            connectionResult: new AppInsightsConnectionResult { Success = false, ErrorMessage = "Invalid credentials" });

        var cmd = new AppInsightsInitCommand(configMock.Object, queryMock.Object, console);
        var settings = new AppInsightsInitCommand.Settings
        {
            AppId = "bad-app-id",
            ApiKey = "bad-api-key"
        };

        var result = cmd.Execute(CreateContext("init"), settings);

        result.Should().Be(1);
        console.Output.Should().ContainAny("Invalid credentials", "failed", "Failed");
    }
}
