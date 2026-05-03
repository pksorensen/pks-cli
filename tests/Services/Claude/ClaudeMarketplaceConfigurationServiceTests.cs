using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Claude;
using Xunit;

namespace PKS.CLI.Tests.Services.Claude;

public class ClaudeMarketplaceConfigurationServiceTests : TestBase
{
    private readonly string _testDirectory;
    private readonly string _configPath;

    public ClaudeMarketplaceConfigurationServiceTests()
    {
        _testDirectory = CreateTempDirectory();
        _configPath = Path.Combine(_testDirectory, "marketplace.json");
    }

    private ClaudeMarketplaceConfigurationService CreateService() => new(_configPath);

    [Fact]
    [Trait("Category", "Claude")]
    public async Task LoadAsync_MissingFile_ReturnsEmpty()
    {
        // Arrange
        var missingPath = Path.Combine(_testDirectory, "does-not-exist", "marketplace.json");
        var service = new ClaudeMarketplaceConfigurationService(missingPath);

        // Act
        var config = await service.LoadAsync();

        // Assert
        config.Should().NotBeNull();
        config.Marketplaces.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task SaveThenLoad_RoundTrips()
    {
        // Arrange
        var service = CreateService();
        var marketplace = new ClaudeMarketplace
        {
            Id = "test-marketplace",
            Label = "Test Marketplace",
            Source = new ClaudeMarketplaceSource { SourceType = "url", Url = "https://example.com/marketplace.json" },
            Plugins = new List<ClaudeMarketplacePluginSnapshot>
            {
                new() { Name = "plugin-a", Version = "1.0.0", Description = "Plugin A", Enabled = true }
            }
        };
        var config = new ClaudeMarketplaceConfiguration
        {
            Marketplaces = new List<ClaudeMarketplace> { marketplace }
        };

        // Act
        await service.SaveAsync(config);
        var service2 = CreateService();
        var loaded = await service2.LoadAsync();

        // Assert
        loaded.Marketplaces.Should().HaveCount(1);
        loaded.Marketplaces[0].Id.Should().Be("test-marketplace");
        loaded.Marketplaces[0].Label.Should().Be("Test Marketplace");
        loaded.Marketplaces[0].Source.Url.Should().Be("https://example.com/marketplace.json");
        loaded.Marketplaces[0].Plugins.Should().HaveCount(1);
        loaded.Marketplaces[0].Plugins[0].Name.Should().Be("plugin-a");
        loaded.Marketplaces[0].Plugins[0].Enabled.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task AddOrUpdate_DuplicateId_Updates()
    {
        // Arrange
        var service = CreateService();
        var marketplace = new ClaudeMarketplace
        {
            Id = "my-marketplace",
            Label = "Original",
            Source = new ClaudeMarketplaceSource { SourceType = "url", Url = "https://original.com/marketplace.json" }
        };
        await service.AddOrUpdateMarketplaceAsync(marketplace);

        var updated = new ClaudeMarketplace
        {
            Id = "my-marketplace",
            Label = "Updated",
            Source = new ClaudeMarketplaceSource { SourceType = "url", Url = "https://updated.com/marketplace.json" }
        };

        // Act
        await service.AddOrUpdateMarketplaceAsync(updated);
        var list = await service.ListMarketplacesAsync();

        // Assert
        list.Should().HaveCount(1);
        list[0].Label.Should().Be("Updated");
        list[0].Source.Url.Should().Be("https://updated.com/marketplace.json");
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task ConcurrentSaves_Serialise()
    {
        // Arrange
        var service = CreateService();

        // Act - run 10 tasks in parallel each calling AddOrUpdateMarketplaceAsync
        var tasks = Enumerable.Range(0, 10).Select(i =>
            service.AddOrUpdateMarketplaceAsync(new ClaudeMarketplace
            {
                Id = $"marketplace-{i}",
                Label = $"Marketplace {i}",
                Source = new ClaudeMarketplaceSource { SourceType = "url", Url = $"https://example{i}.com/marketplace.json" }
            }));

        await Task.WhenAll(tasks);

        // Assert - all 10 marketplaces should be present without data loss
        var list = await service.ListMarketplacesAsync();
        list.Should().HaveCount(10);
    }

    public override void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch { }
        base.Dispose();
    }
}
