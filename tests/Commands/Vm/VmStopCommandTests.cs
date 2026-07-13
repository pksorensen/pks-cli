using FluentAssertions;
using Moq;
using PKS.Commands.Vm;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Vm;

public class VmStopCommandTests
{
    private static AzureVmRecord MakeVm(string name = "pks-vm-test") => new()
    {
        Provider = "azure",
        VmName = name,
        SubscriptionId = "sub1",
        ResourceGroup = "rg1",
        Location = "eastus",
        PublicIpAddress = "1.2.3.4",
        SshKeyPath = "/tmp/key",
        VmSize = "Standard_B2s",
        OsDiskSizeGb = 128
    };

    private static VmProviderRegistry RegistryWith(Mock<IAzureAuthService> auth, Mock<IAzureVmService> vmSvc)
        => new(new IVmProvider[] { new AzureVmProvider(auth.Object, vmSvc.Object) });

    [Fact]
    [Trait("Category", "VmStop")]
    public async Task ExecuteAsync_NoVms_ReturnsZero()
    {
        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord>());

        var console = new TestConsole().Interactive();
        var cmd = new VmStopCommand(
            RegistryWith(new Mock<IAzureAuthService>(), new Mock<IAzureVmService>()),
            meta.Object,
            console);

        var result = await cmd.ExecuteAsync(new VmStopCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().Contain("No VMs tracked");
    }

    [Fact]
    [Trait("Category", "VmStop")]
    public async Task ExecuteAsync_VmFound_CallsStopAndReturnsZero()
    {
        var vm = MakeVm();
        var auth = new Mock<IAzureAuthService>();
        auth.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>())).ReturnsAsync("tok");
        var vmSvc = new Mock<IAzureVmService>();
        vmSvc.Setup(x => x.DeallocateVmAsync("tok", "sub1", "rg1", "pks-vm-test"))
             .Returns(Task.CompletedTask);

        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord> { vm });

        var console = new TestConsole().Interactive();
        var cmd = new VmStopCommand(RegistryWith(auth, vmSvc), meta.Object, console);

        var result = await cmd.ExecuteAsync(new VmStopCommand.Settings { VmName = "pks-vm-test" });

        result.Should().Be(0);
        vmSvc.Verify(x => x.DeallocateVmAsync("tok", "sub1", "rg1", "pks-vm-test"), Times.Once);
        console.Output.Should().Contain("stopped");
    }

    [Fact]
    [Trait("Category", "VmStop")]
    public async Task ExecuteAsync_VmNotFound_ReturnsOneWithError()
    {
        var vm = MakeVm();
        var auth = new Mock<IAzureAuthService>();
        var vmSvc = new Mock<IAzureVmService>();

        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord> { vm });

        var console = new TestConsole().Interactive();
        var cmd = new VmStopCommand(RegistryWith(auth, vmSvc), meta.Object, console);

        var result = await cmd.ExecuteAsync(new VmStopCommand.Settings { VmName = "does-not-exist" });

        result.Should().Be(1);
        console.Output.Should().Contain("not found");
        vmSvc.Verify(x => x.DeallocateVmAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "VmStop")]
    public async Task ExecuteAsync_NotAuthenticated_ReturnsOneWithError()
    {
        var vm = MakeVm();
        var auth = new Mock<IAzureAuthService>();
        auth.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(false);
        var vmSvc = new Mock<IAzureVmService>();

        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord> { vm });

        var console = new TestConsole().Interactive();
        var cmd = new VmStopCommand(RegistryWith(auth, vmSvc), meta.Object, console);

        var result = await cmd.ExecuteAsync(new VmStopCommand.Settings { VmName = "pks-vm-test" });

        result.Should().Be(1);
        console.Output.Should().Contain("Not authenticated");
        vmSvc.Verify(x => x.DeallocateVmAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "VmStop")]
    public async Task ExecuteAsync_StopThrows_ReturnsOneWithErrorInsteadOfCrashing()
    {
        var vm = MakeVm();
        var auth = new Mock<IAzureAuthService>();
        auth.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>())).ReturnsAsync("tok");
        var vmSvc = new Mock<IAzureVmService>();
        vmSvc.Setup(x => x.DeallocateVmAsync("tok", "sub1", "rg1", "pks-vm-test"))
             .ThrowsAsync(new InvalidOperationException("VM already stopped"));

        var meta = new Mock<IAzureVmMetadataService>();
        meta.Setup(x => x.ListAsync()).ReturnsAsync(new List<AzureVmRecord> { vm });

        var console = new TestConsole().Interactive();
        var cmd = new VmStopCommand(RegistryWith(auth, vmSvc), meta.Object, console);

        var result = await cmd.ExecuteAsync(new VmStopCommand.Settings { VmName = "pks-vm-test" });

        result.Should().Be(1);
        console.Output.Should().Contain("Stop failed");
        console.Output.Should().Contain("VM already stopped");
    }
}
