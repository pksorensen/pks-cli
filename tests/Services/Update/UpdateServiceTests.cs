using FluentAssertions;
using Moq;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Update;
using Xunit;

namespace PKS.CLI.Tests.Services.Update;

public class UpdateServiceTests
{
    [Theory]
    [Trait("Category", "Update")]
    [Trait("Speed", "Fast")]
    [InlineData("6.15.0", "6.14.0", true)]
    [InlineData("6.14.1", "6.14.0", true)]
    [InlineData("6.14.0", "6.14.0", false)]
    [InlineData("6.13.9", "6.14.0", false)]
    [InlineData("6.15.0-preview.3", "6.14.0", true)]
    [InlineData("6.14.0-preview.2", "6.14.0", false)] // prerelease precedes the stable of same version
    [InlineData("not-a-version", "6.14.0", false)]
    public void IsNewerThan_ComparesSemver(string candidate, string current, bool expected)
    {
        UpdateService.IsNewerThan(candidate, current).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Update")]
    [Trait("Speed", "Fast")]
    public async Task Channel_RoundTripsThroughConfig()
    {
        var store = new Dictionary<string, string>();
        var config = new Mock<IConfigurationService>();
        config.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<string, string, bool, bool>((k, v, _, _) => store[k] = v).Returns(Task.CompletedTask);
        config.Setup(c => c.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string k) => store.TryGetValue(k, out var v) ? v : null);

        var svc = new UpdateService(new Mock<INuGetTemplateDiscoveryService>().Object, config.Object, new Mock<IInstallMethodDetector>().Object);

        (await svc.GetChannelAsync()).Should().BeNull();
        await svc.SetChannelAsync(UpdateChannel.Daily);
        (await svc.GetChannelAsync()).Should().Be(UpdateChannel.Daily);
    }

    [Fact]
    [Trait("Category", "Update")]
    [Trait("Speed", "Fast")]
    public async Task GetLatestVersion_UsesPrereleaseForDailyChannel()
    {
        var nuget = new Mock<INuGetTemplateDiscoveryService>();
        nuget.Setup(n => n.GetLatestVersionAsync("pks-cli", true, It.IsAny<CancellationToken>())).ReturnsAsync("6.15.0-preview.7");
        nuget.Setup(n => n.GetLatestVersionAsync("pks-cli", false, It.IsAny<CancellationToken>())).ReturnsAsync("6.14.0");

        var svc = new UpdateService(nuget.Object, new Mock<IConfigurationService>().Object, new Mock<IInstallMethodDetector>().Object);

        (await svc.GetLatestVersionAsync(UpdateChannel.Daily)).Should().Be("6.15.0-preview.7");
        (await svc.GetLatestVersionAsync(UpdateChannel.Stable)).Should().Be("6.14.0");
    }
}
