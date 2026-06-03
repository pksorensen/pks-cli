using Xunit;
using FluentAssertions;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;

namespace PKS.CLI.Tests.Services;

public class TailscaleServiceTests
{
    private static TailscaleService Svc() => new(new Moq.Mock<PKS.Infrastructure.IConfigurationService>().Object);

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildUpArgs_AllFlags()
    {
        var creds = new TailscaleStoredCredentials
        {
            AuthKey = "tskey-abc",
            EnableSsh = true,
            AcceptRoutes = true,
            AdvertiseExitNode = true
        };

        var args = Svc().BuildUpArgs(creds, "si14x-h100");

        args.Should().Contain("--authkey=tskey-abc");
        args.Should().Contain("--hostname=si14x-h100");
        args.Should().Contain("--ssh");
        args.Should().Contain("--accept-routes");
        args.Should().Contain("--advertise-exit-node");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildUpArgs_OmitsDisabledFlags_AndSanitizesHostname()
    {
        var creds = new TailscaleStoredCredentials
        {
            AuthKey = "tskey-abc",
            EnableSsh = false,
            AcceptRoutes = false,
            AdvertiseExitNode = false
        };

        var args = Svc().BuildUpArgs(creds, "My GPU Box!");

        args.Should().NotContain("--ssh");
        args.Should().NotContain("--accept-routes");
        args.Should().NotContain("--advertise-exit-node");
        args.Should().Contain("--hostname=my-gpu-box");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildUpArgs_IncludesLoginServerWhenSet()
    {
        var creds = new TailscaleStoredCredentials { AuthKey = "tskey-x", LoginServer = "https://hs.example.com" };
        Svc().BuildUpArgs(creds, "vm").Should().Contain("--login-server=https://hs.example.com");
    }
}
