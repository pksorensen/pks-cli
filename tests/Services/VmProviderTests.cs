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

    private static AzureVmRecord HetznerRecord(string id = "42") => new()
    {
        Provider = "hetzner",
        VmName = "pks-hc-1",
        Zone = "fsn1",
        Location = "fsn1",
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
        var hetzner = new HetznerVmProvider(new Mock<IHetznerCloudService>().Object);
        var registry = new VmProviderRegistry(new IVmProvider[] { azure, scaleway, hetzner });

        registry.Resolve("azure").Should().BeSameAs(azure);
        registry.Resolve("scaleway").Should().BeSameAs(scaleway);
        registry.Resolve("hetzner").Should().BeSameAs(hetzner);
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

    // ── HetznerVmProvider ───────────────────────────────────────────────

    [Fact]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    public void Hetzner_ProviderKey_MatchesInitCommandName()
    {
        var provider = new HetznerVmProvider(new Mock<IHetznerCloudService>().Object);
        provider.ProviderKey.Should().Be("hetzner");
        provider.SupportsScheduledShutdown.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    public async Task Hetzner_Start_IssuesPoweron()
    {
        var svc = new Mock<IHetznerCloudService>();
        var provider = new HetznerVmProvider(svc.Object);

        await provider.StartAsync(HetznerRecord());

        svc.Verify(x => x.PerformActionAsync(42, "poweron", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    public async Task Hetzner_Stop_IssuesGracefulShutdown()
    {
        var svc = new Mock<IHetznerCloudService>();
        var provider = new HetznerVmProvider(svc.Object);

        await provider.StopAsync(HetznerRecord());

        svc.Verify(x => x.PerformActionAsync(42, "shutdown", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    [InlineData("running", VmPowerState.Running)]
    [InlineData("initializing", VmPowerState.Starting)]
    [InlineData("starting", VmPowerState.Starting)]
    [InlineData("stopping", VmPowerState.Stopping)]
    [InlineData("off", VmPowerState.Stopped)]
    [InlineData("migrating", VmPowerState.Unknown)]
    public async Task Hetzner_GetStatus_NormalizesState(string raw, string expected)
    {
        var svc = new Mock<IHetznerCloudService>();
        svc.Setup(x => x.GetServerStatusAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(raw);
        var provider = new HetznerVmProvider(svc.Object);

        var state = await provider.GetStatusAsync(HetznerRecord());

        state.Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    public async Task Hetzner_Destroy_DeletesWithoutPoweringOffFirst()
    {
        // Hetzner deletes a running server directly, and billing only stops at delete —
        // so destroy must not waste minutes on a poweroff round-trip first.
        var svc = new Mock<IHetznerCloudService>();
        var provider = new HetznerVmProvider(svc.Object);

        await provider.DestroyAsync(HetznerRecord());

        svc.Verify(x => x.DeleteServerAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        svc.Verify(x => x.PerformActionAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    public async Task Hetzner_Discover_MapsServersToRecords()
    {
        var svc = new Mock<IHetznerCloudService>();
        svc.Setup(x => x.GetStoredCredentialsAsync()).ReturnsAsync(new HetznerStoredCredentials
        {
            Token = PKS.Infrastructure.Services.Security.SecretValue.From("t"),
            DefaultLocation = "fsn1"
        });
        svc.Setup(x => x.ListServersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new System.Collections.Generic.List<HetznerServer>
        {
            new()
            {
                Id = 42, Name = "pks-hc-1", Status = "off",
                PublicNet = new HetznerPublicNet { Ipv4 = new HetznerIpv4 { Ip = "65.21.1.2" } },
                ServerType = new HetznerServerTypeRef { Name = "cx23", Disk = 40 },
                Location = new HetznerLocation { Name = "nbg1" }
            }
        });
        var provider = new HetznerVmProvider(svc.Object);

        var records = await provider.DiscoverAsync();

        records.Should().ContainSingle();
        var r = records[0];
        r.Provider.Should().Be("hetzner");
        r.ServerId.Should().Be("42");
        r.VmName.Should().Be("pks-hc-1");
        r.AdminUsername.Should().Be("root");
        r.Zone.Should().Be("nbg1");
        r.Location.Should().Be("nbg1");
        r.PublicIpAddress.Should().Be("65.21.1.2");
        r.VmSize.Should().Be("cx23");
        r.OsDiskSizeGb.Should().Be(40);
    }

    [Fact]
    [Trait("Category", "VmProvider")]
    [Trait("Speed", "Fast")]
    public async Task Hetzner_GetStatus_WithoutServerId_ReturnsNull()
    {
        var provider = new HetznerVmProvider(new Mock<IHetznerCloudService>().Object);
        var state = await provider.GetStatusAsync(new AzureVmRecord { Provider = "hetzner", VmName = "x" });
        state.Should().BeNull();
    }
}
