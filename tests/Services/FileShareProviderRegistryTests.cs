using Xunit;
using Moq;
using FluentAssertions;
using PKS.Infrastructure.Services;

namespace PKS.CLI.Tests.Services;

public class FileShareProviderRegistryTests
{
    private static Mock<IFileShareProvider> CreateProviderMock(string key, string name, bool authenticated)
    {
        var mock = new Mock<IFileShareProvider>();
        mock.Setup(p => p.ProviderKey).Returns(key);
        mock.Setup(p => p.ProviderName).Returns(name);
        mock.Setup(p => p.IsAuthenticatedAsync()).ReturnsAsync(authenticated);
        return mock;
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void GetAllProviders_ReturnsAllRegistered()
    {
        var p1 = CreateProviderMock("azure-fileshare", "Azure File Share", false);
        var p2 = CreateProviderMock("s3", "Amazon S3", false);
        var registry = new FileShareProviderRegistry(new[] { p1.Object, p2.Object });

        var result = registry.GetAllProviders().ToList();

        result.Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task GetAuthenticatedProviders_ReturnsOnlyAuthenticated()
    {
        var p1 = CreateProviderMock("azure-fileshare", "Azure File Share", true);
        var p2 = CreateProviderMock("s3", "Amazon S3", false);
        var registry = new FileShareProviderRegistry(new[] { p1.Object, p2.Object });

        var result = (await registry.GetAuthenticatedProvidersAsync()).ToList();

        result.Should().HaveCount(1);
        result[0].ProviderKey.Should().Be("azure-fileshare");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task GetAuthenticatedProviders_ReturnsEmpty_WhenNoneAuthenticated()
    {
        var p1 = CreateProviderMock("azure-fileshare", "Azure File Share", false);
        var registry = new FileShareProviderRegistry(new[] { p1.Object });

        var result = (await registry.GetAuthenticatedProvidersAsync()).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task GetAuthenticatedProviders_ReturnsAll_WhenAllAuthenticated()
    {
        var p1 = CreateProviderMock("azure-fileshare", "Azure File Share", true);
        var p2 = CreateProviderMock("s3", "Amazon S3", true);
        var registry = new FileShareProviderRegistry(new[] { p1.Object, p2.Object });

        var result = (await registry.GetAuthenticatedProvidersAsync()).ToList();

        result.Should().HaveCount(2);
    }
}
