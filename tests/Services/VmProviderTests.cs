using Xunit;
using Moq;
using FluentAssertions;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.CLI.Tests.Services;

public class VmProviderTests
{
    private static AzureVmRecord ScalewayRecord(string zone = "fr-par-2", string id = "srv-1") => new()
    {
        Provider = "scaleway",
        VmName = "h100",
        Zone = zone,
        ServerId = id,
        AdminUsername = "root"
    };

    // ── VmProviderRegistry ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    public void Registry_Resolve_ReturnsProviderByKey()
    {
        var azure = new AzureVmProvider(new Mock<IAzureAuthService>().Object, new Mock<IAzureVmService>().Object);
        var scaleway = new ScalewayVmProvider(new Mock<IScalewayService>().Object);
        var registry = new VmProviderRegistry(new IVmProvider[] { azure, scaleway });

        registry.Resolve("azure").Should().BeSameAs(azure);
        registry.Resolve("scaleway").Should().BeSameAs(scaleway);
        // Empty provider on a record defaults to azure
        registry.Resolve(new AzureVmRecord { Provider = "" }).Should().BeSameAs(azure);
    }

    [Fact]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    public void Registry_Resolve_UnknownKey_Throws()
    {
        var registry = new VmProviderRegistry(new IVmProvider[]
        {
            new ScalewayVmProvider(new Mock<IScalewayService>().Object)
        });

        var act = () => registry.Resolve("gcp");
        act.Should().Throw<InvalidOperationException>();
    }

    // ── ScalewayVmProvider ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    public async Task Scaleway_Start_IssuesPoweron()
    {
        var svc = new Mock<IScalewayService>();
        var provider = new ScalewayVmProvider(svc.Object);

        await provider.StartAsync(ScalewayRecord());

        svc.Verify(x => x.PerformActionAsync("fr-par-2", "srv-1", "poweron", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    public async Task Scaleway_Stop_IssuesPoweroff()
    {
        var svc = new Mock<IScalewayService>();
        var provider = new ScalewayVmProvider(svc.Object);

        await provider.StopAsync(ScalewayRecord());

        svc.Verify(x => x.PerformActionAsync("fr-par-2", "srv-1", "poweroff", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    [InlineData("running", VmPowerState.Running)]
    [InlineData("stopped", VmPowerState.Stopped)]
    [InlineData("stopped in place", VmPowerState.Stopped)]
    [InlineData("starting", VmPowerState.Starting)]
    [InlineData("stopping", VmPowerState.Stopping)]
    [InlineData("locked", VmPowerState.Unknown)]
    public async Task Scaleway_GetStatus_NormalizesState(string raw, string expected)
    {
        var svc = new Mock<IScalewayService>();
        svc.Setup(x => x.GetServerStateAsync("fr-par-2", "srv-1", It.IsAny<CancellationToken>())).ReturnsAsync(raw);
        var provider = new ScalewayVmProvider(svc.Object);

        var state = await provider.GetStatusAsync(ScalewayRecord());

        state.Should().Be(expected);
    }
}
