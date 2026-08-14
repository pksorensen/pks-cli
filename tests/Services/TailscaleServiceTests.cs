using PKS.CLI.Tests.Security;
using Xunit;
using FluentAssertions;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.CLI.Tests.Services;

public class TailscaleServiceTests
{
    private static TailscaleService Svc() => new(new Moq.Mock<PKS.Infrastructure.IConfigurationService>().Object, FakeSecretResolver.Empty);

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildUpArgs_AllFlags()
    {
        var creds = new TailscaleStoredCredentials
        {
            AuthKey = SecretValue.From("tskey-abc"),
            EnableSsh = true,
            AcceptRoutes = true,
            AdvertiseExitNode = true
        };

        var args = Svc().BuildUpArgs(creds, "si14x-h100").Reveal()!;

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
            AuthKey = SecretValue.From("tskey-abc"),
            EnableSsh = false,
            AcceptRoutes = false,
            AdvertiseExitNode = false
        };

        var args = Svc().BuildUpArgs(creds, "My GPU Box!").Reveal()!;

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
        var creds = new TailscaleStoredCredentials { AuthKey = SecretValue.From("tskey-x"), LoginServer = "https://hs.example.com" };
        Svc().BuildUpArgs(creds, "vm").Reveal()!.Should().Contain("--login-server=https://hs.example.com");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task JoinTailnetAsync_ComposesTheCommandWithoutHandingBackTheAuthKey()
    {
        // The point of this method: `vm tailscale` used to build the argument line itself and
        // interpolate it into a shell command, which put the auth key in a local variable in the
        // command layer where the source-scanning gate cannot see it. Now the command only supplies
        // the runner.
        var creds = new TailscaleStoredCredentials { AuthKey = SecretValue.From("tskey-abc"), EnableSsh = true };
        string? executed = null;

        var result = await Svc().JoinTailnetAsync(creds, "gpu-box", "sudo ", cmd =>
        {
            executed = cmd;
            return Task.FromResult<SshResult?>(new SshResult(0, "", "", false));
        });

        executed.Should().StartWith("sudo tailscale up ").And.Contain("--authkey=tskey-abc").And.Contain("--ssh");
        result!.ExitCode.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task JoinTailnetAsync_PropagatesAFailedStep()
    {
        // Null means "the step never completed" — the caller turns that into a non-zero exit rather
        // than reporting a tailnet join that did not happen.
        var creds = new TailscaleStoredCredentials { AuthKey = SecretValue.From("tskey-abc") };

        var result = await Svc().JoinTailnetAsync(creds, "gpu-box", string.Empty, _ => Task.FromResult<SshResult?>(null));

        result.Should().BeNull();
    }
}
