using Xunit;
using Moq;
using FluentAssertions;
using PKS.Commands.Vm;
using PKS.Commands.Azure;
using PKS.Commands.Scaleway;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Testing;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.CLI.Tests.Commands;

public class VmInitCommandTests
{
    // ─────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────

    private static Mock<IAzureAuthService> CreateAzureAuthMock(bool authenticated = false)
    {
        var mock = new Mock<IAzureAuthService>();
        mock.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(authenticated);

        if (authenticated)
        {
            mock.Setup(x => x.GetStoredCredentialsAsync())
                .ReturnsAsync(new AzureStoredCredentials
                {
                    TenantId = "tenant-1",
                    RefreshToken = "rt",
                    SubscriptionId = "sub-1",
                    SubscriptionName = "Test Sub"
                });
        }
        else
        {
            mock.Setup(x => x.GetStoredCredentialsAsync())
                .ReturnsAsync((AzureStoredCredentials?)null);
        }

        return mock;
    }

    private static VmInitCommand CreateCommand(
        Mock<IAzureAuthService> authMock,
        Mock<IAzureVmService> vmServiceMock,
        Mock<ISshTargetConfigurationService> sshServiceMock,
        Mock<IAzureVmMetadataService> metadataMock,
        IAnsiConsole console,
        int azureInitResult = 0)
    {
        var scalewayMock = new Mock<IScalewayService>();
        scalewayMock.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(false);
        var tailscaleMock = new Mock<ITailscaleService>();
        tailscaleMock.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(false);

        var azureInitMock = new Mock<AzureInitCommand>(authMock.Object, console);
        azureInitMock
            .Setup(x => x.Execute(It.IsAny<CommandContext>(), It.IsAny<AzureInitCommand.Settings>()))
            .Returns(azureInitResult);

        var scalewayInitMock = new Mock<ScalewayInitCommand>(scalewayMock.Object, console);
        scalewayInitMock
            .Setup(x => x.Execute(It.IsAny<CommandContext>(), It.IsAny<ScalewayInitCommand.Settings>()))
            .Returns(0);

        return new VmInitCommand(
            authMock.Object,
            vmServiceMock.Object,
            sshServiceMock.Object,
            metadataMock.Object,
            scalewayMock.Object,
            tailscaleMock.Object,
            azureInitMock.Object,
            scalewayInitMock.Object,
            new Mock<PKS.Infrastructure.Services.Security.IActionGuard>().Object,
            console);
    }

    // ═════════════════════════════════════════════
    //  VmInitCommand Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "VmInit")]
    public void Execute_AzureChosen_WhenAuthFails_ReturnsOne()
    {
        // Arrange — not authenticated; chained azure init also fails to authenticate
        var authMock = CreateAzureAuthMock(authenticated: false);
        var vmServiceMock = new Mock<IAzureVmService>();
        var sshServiceMock = new Mock<ISshTargetConfigurationService>();
        var metadataMock = new Mock<IAzureVmMetadataService>();
        var console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("Azure"); // provider selection prompt

        var command = CreateCommand(authMock, vmServiceMock, sshServiceMock, metadataMock, console, azureInitResult: 1);

        // Act
        var result = command.Execute(null!, new VmInitCommand.Settings());

        // Assert
        result.Should().Be(1);
        console.Output.Should().Contain("Azure authentication required");
    }

    [Fact]
    [Trait("Category", "VmInit")]
    public void Execute_AzureChosen_WhenAuthenticated_ProceedsPastAuthCheck()
    {
        // Arrange
        var authMock = CreateAzureAuthMock(authenticated: true);
        var vmServiceMock = new Mock<IAzureVmService>();
        var sshServiceMock = new Mock<ISshTargetConfigurationService>();
        var metadataMock = new Mock<IAzureVmMetadataService>();
        var console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter("Azure"); // provider selection prompt

        authMock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("management-token");
        vmServiceMock.Setup(x => x.ListResourceGroupsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AzureResourceGroup>
            {
                new() { Id = "/sub/rg1", Name = "existing-rg", Location = "eastus" }
            });

        var command = CreateCommand(authMock, vmServiceMock, sshServiceMock, metadataMock, console);

        // Act — flow stops at a later interactive prompt (no input pushed); we only assert
        // that the Azure auth check ran and we proceeded past it.
        try { command.Execute(null!, new VmInitCommand.Settings()); }
        catch { }

        // Assert
        authMock.Verify(x => x.IsAuthenticatedAsync(), Times.Once);
        authMock.Verify(x => x.GetStoredCredentialsAsync(), Times.Once);
    }
}
