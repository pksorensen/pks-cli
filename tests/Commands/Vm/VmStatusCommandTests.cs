using FluentAssertions;
using Moq;
using PKS.Commands.Vm;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Vm;

public class VmStatusCommandTests
{
    private static AzureVmRecord MakeVm(string name = "pks-vm-test") => new()
    {
        VmName = name,
        SubscriptionId = "sub1",
        ResourceGroup = "rg1",
        Location = "eastus",
        PublicIpAddress = "1.2.3.4",
        SshKeyPath = "/tmp/key",
        VmSize = "Standard_B2s",
        OsDiskSizeGb = 128
    };

    [Fact]
    [Trait("Category", "VmStatus")]
    public async Task Run_NoVms_ShowsHelp_Returns0()
    {
        // Arrange
        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord>());

        var console = new TestConsole();
        var cmd = new VmStatusCommand(
            new Mock<IAzureAuthService>().Object,
            new Mock<IAzureVmService>().Object,
            meta.Object,
            new Mock<ISshExecutor>().Object,
            new Mock<ISshTargetConfigurationService>().Object,
            console);

        // Act
        var result = await cmd.ExecuteAsync(new VmStatusCommand.Settings());

        // Assert
        result.Should().Be(0);
        console.Output.Should().Contain("No VMs tracked");
    }

    [Fact]
    [Trait("Category", "VmStatus")]
    public async Task Run_OneVm_AutoPicks_ShowsDeallocatedStatus()
    {
        // Arrange
        var vm = MakeVm();
        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord> { vm });

        var auth = new Mock<IAzureAuthService>();
        auth.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>())).ReturnsAsync("tok");

        var vmSvc = new Mock<IAzureVmService>();
        vmSvc.Setup(x => x.GetVmStatusAsync("tok", "sub1", "rg1", "pks-vm-test", default))
             .ReturnsAsync("deallocated");

        var console = new TestConsole();
        // No SSH prompts since VM is deallocated, but action menu appears — select "Quit"
        console.Input.PushTextWithEnter("Quit");

        var cmd = new VmStatusCommand(auth.Object, vmSvc.Object, meta.Object,
            new Mock<ISshExecutor>().Object,
            new Mock<ISshTargetConfigurationService>().Object, console);

        // Act
        var result = await cmd.ExecuteAsync(new VmStatusCommand.Settings());

        // Assert
        result.Should().Be(0);
        console.Output.Should().Contain("pks-vm-test");
    }

    [Fact]
    [Trait("Category", "VmStatus")]
    public async Task Run_RunningVm_FreeDiskSpace_RunsPruneCommand()
    {
        // Arrange
        var vm = MakeVm();
        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord> { vm });

        var auth = new Mock<IAzureAuthService>();
        auth.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>())).ReturnsAsync("tok");

        var vmSvc = new Mock<IAzureVmService>();
        vmSvc.Setup(x => x.GetVmStatusAsync("tok", "sub1", "rg1", "pks-vm-test", default))
             .ReturnsAsync("running");

        // SSH returns high disk usage (95%) to make "Free disk space" option appear
        var sshMock = new Mock<ISshExecutor>();
        var sshCommands = new List<string>();
        sshMock.Setup(x => x.RunAsync(
                It.IsAny<SshTarget>(), It.IsAny<string>(),
                It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
               .Callback<SshTarget, string, TimeSpan, CancellationToken>(
                   (_, cmd, _, _) => sshCommands.Add(cmd))
               .ReturnsAsync(new SshResult(0,
                   "__DISK__\n/dev/sda1  128G  122G  6G  95% /\n__MEM__\n              total  used  free\nMem:           8192  2048  6144\n__DOCKER__\nContainers: 2 (1 running) | Images: 5 | Server: 24.0.0\n__SPACE__\n95\n__UPTIME__\nup 2 hours, 14 minutes",
                   "", false));

        var console = new TestConsole();
        // Action menu: pick "Free disk space..." then "Quit"
        console.Input.PushTextWithEnter("Free disk space (docker system prune -af --volumes)");
        console.Input.PushTextWithEnter("Quit");

        var cmd = new VmStatusCommand(auth.Object, vmSvc.Object, meta.Object, sshMock.Object,
            new Mock<ISshTargetConfigurationService>().Object, console);

        // Act
        var result = await cmd.ExecuteAsync(new VmStatusCommand.Settings());

        // Assert
        result.Should().Be(0);
        sshCommands.Should().Contain(c => c.Contains("docker system prune"));
    }
}
