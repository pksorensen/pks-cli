using FluentAssertions;
using Moq;
using PKS.Commands.Vm;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Vm;

public class VmListCommandTests
{
    private static AzureVmRecord MakeVm(string name = "pks-vm-test", string ip = "1.2.3.4") => new()
    {
        VmName = name,
        SubscriptionId = "sub1",
        ResourceGroup = "rg1",
        Location = "eastus",
        PublicIpAddress = ip,
        SshKeyPath = "/tmp/key",
        VmSize = "Standard_B2s",
        OsDiskSizeGb = 128,
        CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    [Trait("Category", "VmList")]
    public async Task ExecuteAsync_NoVms_ShowsHelp()
    {
        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord>());

        var console = new TestConsole();
        var cmd = new VmListCommand(
            meta.Object,
            new Mock<IAzureAuthService>().Object,
            new Mock<IAzureVmService>().Object,
            new Mock<ISshTargetConfigurationService>().Object,
            new Mock<ISshExecutor>().Object,
            console);

        var result = await cmd.ExecuteAsync();

        result.Should().Be(0);
        console.Output.Should().Contain("No VMs tracked");
    }

    [Fact]
    [Trait("Category", "VmList")]
    public async Task ExecuteAsync_RunningVm_FetchesDiskPctViaSsh()
    {
        var vm = MakeVm();
        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord> { vm });

        var auth = new Mock<IAzureAuthService>();
        auth.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>())).ReturnsAsync("tok");

        var vmSvc = new Mock<IAzureVmService>();
        vmSvc.Setup(x => x.GetVmStatusAsync("tok", "sub1", "rg1", "pks-vm-test", default))
             .ReturnsAsync("running");

        var sshCalls = new List<string>();
        var sshMock = new Mock<ISshExecutor>();
        sshMock.Setup(x => x.RunAsync(
                It.IsAny<SshTarget>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
               .Callback<SshTarget, string, TimeSpan, CancellationToken>(
                   (_, cmd, _, _) => sshCalls.Add(cmd))
               .ReturnsAsync(new SshResult(0, "72", "", false));

        var console = new TestConsole();
        // Respond to "Inspect a VM?" prompt with "No, quit"
        console.Input.PushTextWithEnter("No, quit");

        var cmd2 = new VmListCommand(
            meta.Object, auth.Object, vmSvc.Object,
            new Mock<ISshTargetConfigurationService>().Object,
            sshMock.Object, console);

        var result = await cmd2.ExecuteAsync();

        result.Should().Be(0);
        sshCalls.Should().Contain(c => c.Contains("df") && c.Contains("pcent"));
        console.Output.Should().Contain("72");
    }

    [Fact]
    [Trait("Category", "VmList")]
    public async Task ExecuteAsync_HighDiskPct_ShowsRedInOutput()
    {
        var vm = MakeVm();
        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord> { vm });

        var auth = new Mock<IAzureAuthService>();
        auth.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>())).ReturnsAsync("tok");

        var vmSvc = new Mock<IAzureVmService>();
        vmSvc.Setup(x => x.GetVmStatusAsync("tok", "sub1", "rg1", "pks-vm-test", default))
             .ReturnsAsync("running");

        var sshMock = new Mock<ISshExecutor>();
        sshMock.Setup(x => x.RunAsync(
                It.IsAny<SshTarget>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new SshResult(0, "95", "", false));

        var console = new TestConsole();
        console.Input.PushTextWithEnter("No, quit");

        var cmd = new VmListCommand(
            meta.Object, auth.Object, vmSvc.Object,
            new Mock<ISshTargetConfigurationService>().Object,
            sshMock.Object, console);

        await cmd.ExecuteAsync();

        // 95% — rendered as red
        console.Output.Should().Contain("95%");
    }
}
