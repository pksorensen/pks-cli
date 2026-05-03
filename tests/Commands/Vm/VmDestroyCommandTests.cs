using FluentAssertions;
using Moq;
using PKS.Commands.Vm;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Vm;

public class VmDestroyCommandTests
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
    [Trait("Category", "VmDestroy")]
    public async Task ExecuteAsync_NoVms_ReturnsZero()
    {
        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord>());

        var console = new TestConsole();
        var cmd = new VmDestroyCommand(
            new Mock<IAzureAuthService>().Object,
            new Mock<IAzureVmService>().Object,
            meta.Object,
            new Mock<ISshTargetConfigurationService>().Object,
            console);

        var result = await cmd.ExecuteAsync(new VmDestroyCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().Contain("No VMs tracked");
    }

    [Fact]
    [Trait("Category", "VmDestroy")]
    public async Task DestroyVmAsync_WhenConfirmed_CallsDestroyAndRemovesRecord()
    {
        var vm = MakeVm();
        var vmSvc = new Mock<IAzureVmService>();
        vmSvc.Setup(x => x.DestroyVmAsync("tok", "sub1", "rg1", "pks-vm-test", It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.RemoveAsync("pks-vm-test")).Returns(Task.CompletedTask);

        var sshTargets = new Mock<ISshTargetConfigurationService>();
        sshTargets.Setup(x => x.RemoveTargetAsync("pks-vm-test")).Returns(Task.CompletedTask);

        var console = new TestConsole();
        // Confirm "Are you sure?" → yes
        console.Input.PushTextWithEnter("y");

        var cmd = new VmDestroyCommand(
            new Mock<IAzureAuthService>().Object,
            vmSvc.Object,
            meta.Object,
            sshTargets.Object,
            console);

        var result = await cmd.DestroyVmAsync(vm, "tok");

        result.Should().Be(0);
        vmSvc.Verify(x => x.DestroyVmAsync("tok", "sub1", "rg1", "pks-vm-test",
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        meta.Verify(x => x.RemoveAsync("pks-vm-test"), Times.Once);
    }

    [Fact]
    [Trait("Category", "VmDestroy")]
    public async Task DestroyVmAsync_WhenDenied_DoesNotCallDestroyService()
    {
        var vm = MakeVm();
        var vmSvc = new Mock<IAzureVmService>();
        var meta = new Mock<IAzureVmMetadataService>();

        var console = new TestConsole();
        // Decline confirmation
        console.Input.PushTextWithEnter("n");

        var cmd = new VmDestroyCommand(
            new Mock<IAzureAuthService>().Object,
            vmSvc.Object,
            meta.Object,
            new Mock<ISshTargetConfigurationService>().Object,
            console);

        var result = await cmd.DestroyVmAsync(vm, "tok");

        result.Should().Be(0);
        vmSvc.Verify(x => x.DestroyVmAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
