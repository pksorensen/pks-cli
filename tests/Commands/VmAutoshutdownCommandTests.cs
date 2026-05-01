using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using PKS.Commands.Vm;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands;

public class VmAutoshutdownCommandTests
{
    private static Mock<IAzureAuthService> CreateAuthMock(bool authenticated = true)
    {
        var mock = new Mock<IAzureAuthService>();
        mock.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(authenticated);
        if (authenticated)
        {
            mock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("management-token");
        }
        return mock;
    }

    [Fact]
    [Trait("Category", "VmAutoshutdown")]
    public void Execute_WhenNoVmsTracked_ReturnsError()
    {
        var authMock = CreateAuthMock();
        var vmServiceMock = new Mock<IAzureVmService>();
        var metadataMock = new Mock<IAzureVmMetadataService>();
        var console = new TestConsole();

        metadataMock.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord>());

        var command = new VmAutoshutdownCommand(authMock.Object, vmServiceMock.Object, metadataMock.Object, console);
        var settings = new VmAutoshutdownCommand.Settings { VmName = "any-vm" };

        var result = command.Execute(null!, settings);

        result.Should().Be(1);
        console.Output.Should().Contain("No VMs tracked");
        console.Output.Should().Contain("pks vm init");
    }

    [Fact]
    [Trait("Category", "VmAutoshutdown")]
    public void Execute_WithDisableFlag_CallsDisableOnService()
    {
        var authMock = CreateAuthMock();
        var vmServiceMock = new Mock<IAzureVmService>();
        var metadataMock = new Mock<IAzureVmMetadataService>();
        var console = new TestConsole();

        var record = new AzureVmRecord
        {
            VmName = "my-vm",
            SubscriptionId = "sub-1",
            ResourceGroup = "rg-1",
            Location = "eastus",
            PublicIpAddress = "1.2.3.4",
            SshKeyPath = "/tmp/key",
            IdleShutdownMinutes = 60,
            ScheduledShutdownUtc = "22:00"
        };

        metadataMock.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord> { record });
        metadataMock.Setup(x => x.FindAsync("my-vm")).ReturnsAsync(record);
        metadataMock.Setup(x => x.SaveAsync(It.IsAny<AzureVmRecord>())).Returns(Task.CompletedTask);

        vmServiceMock.Setup(x => x.DisableScheduledShutdownAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new VmAutoshutdownCommand(authMock.Object, vmServiceMock.Object, metadataMock.Object, console);
        var settings = new VmAutoshutdownCommand.Settings { VmName = "my-vm", Disable = true };

        var result = command.Execute(null!, settings);

        result.Should().Be(0);
        vmServiceMock.Verify(x => x.DisableScheduledShutdownAsync(
            "management-token", "sub-1", "rg-1", "my-vm", It.IsAny<CancellationToken>()), Times.Once);
        metadataMock.Verify(x => x.SaveAsync(It.Is<AzureVmRecord>(r =>
            r.IdleShutdownMinutes == 0 && r.ScheduledShutdownUtc == null)), Times.Once);
    }

    [Fact]
    [Trait("Category", "VmAutoshutdown")]
    public void Execute_WithScheduledTime_CallsSetScheduledShutdown()
    {
        var authMock = CreateAuthMock();
        var vmServiceMock = new Mock<IAzureVmService>();
        var metadataMock = new Mock<IAzureVmMetadataService>();
        var console = new TestConsole();

        var record = new AzureVmRecord
        {
            VmName = "my-vm",
            SubscriptionId = "sub-1",
            ResourceGroup = "rg-1",
            Location = "eastus",
            PublicIpAddress = "1.2.3.4",
            SshKeyPath = "/tmp/key",
            IdleShutdownMinutes = 60
        };

        metadataMock.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord> { record });
        metadataMock.Setup(x => x.FindAsync("my-vm")).ReturnsAsync(record);
        metadataMock.Setup(x => x.SaveAsync(It.IsAny<AzureVmRecord>())).Returns(Task.CompletedTask);

        vmServiceMock.Setup(x => x.SetScheduledShutdownAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new VmAutoshutdownCommand(authMock.Object, vmServiceMock.Object, metadataMock.Object, console);
        var settings = new VmAutoshutdownCommand.Settings { VmName = "my-vm", ScheduledTime = "22:00" };

        var result = command.Execute(null!, settings);

        result.Should().Be(0);
        vmServiceMock.Verify(x => x.SetScheduledShutdownAsync(
            "management-token", "sub-1", "rg-1", "my-vm",
            "eastus", It.IsAny<string>(), "22:00", It.IsAny<CancellationToken>()), Times.Once);
        metadataMock.Verify(x => x.SaveAsync(It.Is<AzureVmRecord>(r =>
            r.ScheduledShutdownUtc == "22:00")), Times.Once);
    }

    [Fact]
    [Trait("Category", "VmAutoshutdown")]
    public void Execute_WithIdleMinutes_UpdatesRecord()
    {
        var authMock = CreateAuthMock();
        var vmServiceMock = new Mock<IAzureVmService>();
        var metadataMock = new Mock<IAzureVmMetadataService>();
        var console = new TestConsole();

        var record = new AzureVmRecord
        {
            VmName = "my-vm",
            SubscriptionId = "sub-1",
            ResourceGroup = "rg-1",
            Location = "eastus",
            PublicIpAddress = string.Empty,   // No SSH — skip SSH command
            SshKeyPath = string.Empty,
            IdleShutdownMinutes = 60
        };

        metadataMock.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord> { record });
        metadataMock.Setup(x => x.FindAsync("my-vm")).ReturnsAsync(record);
        metadataMock.Setup(x => x.SaveAsync(It.IsAny<AzureVmRecord>())).Returns(Task.CompletedTask);

        var command = new VmAutoshutdownCommand(authMock.Object, vmServiceMock.Object, metadataMock.Object, console);
        var settings = new VmAutoshutdownCommand.Settings { VmName = "my-vm", IdleMinutes = 30 };

        var result = command.Execute(null!, settings);

        result.Should().Be(0);
        metadataMock.Verify(x => x.SaveAsync(It.Is<AzureVmRecord>(r =>
            r.IdleShutdownMinutes == 30)), Times.Once);
    }
}
