using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Commands.Marketplace;
using PKS.Infrastructure.Services.Claude;
using Spectre.Console.Cli;
using Xunit;

namespace PKS.CLI.Tests.Commands.Claude.Marketplace;

public class ClaudeMarketplaceAddCommandTests : TestBase
{
    private readonly Mock<IClaudeMarketplaceConfigurationService> _configServiceMock;
    private readonly Mock<IClaudeMarketplaceFetcher> _fetcherMock;

    public ClaudeMarketplaceAddCommandTests()
    {
        _configServiceMock = new Mock<IClaudeMarketplaceConfigurationService>();
        _fetcherMock = new Mock<IClaudeMarketplaceFetcher>();
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task Execute_EnableAll_NonInteractive_WritesCorrectConfig()
    {
        // Arrange
        var source = new ClaudeMarketplaceSource { SourceType = "url", Url = "https://example.com/marketplace.json" };
        var marketplaceJson = new MarketplaceJson(
            "test-marketplace",
            "Test Marketplace",
            new List<MarketplacePluginInfo>
            {
                new("plugin-a", "1.0.0", "Plugin A"),
                new("plugin-b", "2.0.0", "Plugin B")
            });

        _fetcherMock.Setup(f => f.ParseSource("https://example.com/marketplace.json")).Returns(source);
        _fetcherMock.Setup(f => f.FetchAsync(source)).ReturnsAsync(marketplaceJson);

        ClaudeMarketplace? savedMarketplace = null;
        _configServiceMock.Setup(s => s.AddOrUpdateMarketplaceAsync(It.IsAny<ClaudeMarketplace>()))
            .Callback<ClaudeMarketplace>(m => savedMarketplace = m)
            .ReturnsAsync((ClaudeMarketplace m) => m);

        var command = new MarketplaceAddCommand(
            _configServiceMock.Object,
            _fetcherMock.Object,
            TestConsole);

        var settings = new MarketplaceAddCommand.Settings
        {
            Source = "https://example.com/marketplace.json",
            NonInteractive = true,
            EnableAll = true
        };

        // Act
        var result = await command.ExecuteAsync(new CommandContext(Mock.Of<IRemainingArguments>(), "add", null), settings);

        // Assert
        result.Should().Be(0);
        savedMarketplace.Should().NotBeNull();
        savedMarketplace!.Id.Should().Be("test-marketplace");
        savedMarketplace.Plugins.Should().HaveCount(2);
        savedMarketplace.Plugins.Should().AllSatisfy(p => p.Enabled.Should().BeTrue());
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task Execute_DuplicateAdd_UpdatesNotDuplicates()
    {
        // Arrange
        var source = new ClaudeMarketplaceSource { SourceType = "url", Url = "https://example.com/marketplace.json" };
        var marketplaceJson = new MarketplaceJson(
            "test-marketplace",
            "Test Marketplace",
            new List<MarketplacePluginInfo> { new("plugin-a", "1.0.0", "Plugin A") });

        _fetcherMock.Setup(f => f.ParseSource(It.IsAny<string>())).Returns(source);
        _fetcherMock.Setup(f => f.FetchAsync(It.IsAny<ClaudeMarketplaceSource>())).ReturnsAsync(marketplaceJson);

        var callCount = 0;
        _configServiceMock.Setup(s => s.AddOrUpdateMarketplaceAsync(It.IsAny<ClaudeMarketplace>()))
            .Callback(() => callCount++)
            .ReturnsAsync((ClaudeMarketplace m) => m);

        var command = new MarketplaceAddCommand(
            _configServiceMock.Object,
            _fetcherMock.Object,
            TestConsole);

        var settings = new MarketplaceAddCommand.Settings
        {
            Source = "https://example.com/marketplace.json",
            NonInteractive = true,
            EnableAll = false
        };

        // Act - execute twice
        await command.ExecuteAsync(new CommandContext(Mock.Of<IRemainingArguments>(), "add", null), settings);
        await command.ExecuteAsync(new CommandContext(Mock.Of<IRemainingArguments>(), "add", null), settings);

        // Assert - AddOrUpdate called twice (not duplicating), upsert semantics handled by service
        callCount.Should().Be(2);
        _configServiceMock.Verify(s => s.AddOrUpdateMarketplaceAsync(It.IsAny<ClaudeMarketplace>()), Times.Exactly(2));
    }
}
