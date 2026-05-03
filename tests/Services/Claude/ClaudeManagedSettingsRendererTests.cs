using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Claude;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Services.Claude;

public class ClaudeManagedSettingsRendererTests : TestBase
{
    private readonly ClaudeManagedSettingsRenderer _renderer = new();

    [Fact]
    [Trait("Category", "Claude")]
    public void Render_EmptyConfig_ReturnsEmptyObject()
    {
        // Arrange
        var config = new ClaudeMarketplaceConfiguration();

        // Act
        var json = _renderer.Render(config);

        // Assert
        json.Should().NotBeNullOrEmpty();
        var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Claude")]
    public void Render_OneMarketplaceNoEnabled_OnlyExtraKnownMarketplaces()
    {
        // Arrange
        var config = new ClaudeMarketplaceConfiguration
        {
            Marketplaces = new List<ClaudeMarketplace>
            {
                new()
                {
                    Id = "agentic-live",
                    Label = "context& dev marketplace",
                    Source = new ClaudeMarketplaceSource { SourceType = "url", Url = "https://agentic.example.com/marketplace.json" },
                    Plugins = new List<ClaudeMarketplacePluginSnapshot>
                    {
                        new() { Name = "ctx-onboard", Version = "1.0.0", Enabled = false }
                    }
                }
            }
        };

        // Act
        var json = _renderer.Render(config);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("extraKnownMarketplaces", out var ekm).Should().BeTrue();
        ekm.TryGetProperty("agentic-live", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("enabledPlugins", out _).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Claude")]
    public void Render_OneMarketplaceTwoEnabled_BothSections()
    {
        // Arrange
        var config = new ClaudeMarketplaceConfiguration
        {
            Marketplaces = new List<ClaudeMarketplace>
            {
                new()
                {
                    Id = "agentic-live",
                    Label = "context& dev marketplace",
                    Source = new ClaudeMarketplaceSource { SourceType = "url", Url = "https://agentic.example.com/marketplace.json" },
                    Plugins = new List<ClaudeMarketplacePluginSnapshot>
                    {
                        new() { Name = "ctx-onboard", Enabled = true },
                        new() { Name = "ctx-review", Enabled = true },
                        new() { Name = "ctx-disabled", Enabled = false }
                    }
                }
            }
        };

        // Act
        var json = _renderer.Render(config);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("extraKnownMarketplaces", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("enabledPlugins", out var ep).Should().BeTrue();
        ep.TryGetProperty("ctx-onboard@agentic-live", out var v1).Should().BeTrue();
        v1.GetBoolean().Should().BeTrue();
        ep.TryGetProperty("ctx-review@agentic-live", out var v2).Should().BeTrue();
        v2.GetBoolean().Should().BeTrue();
        ep.TryGetProperty("ctx-disabled@agentic-live", out _).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Claude")]
    public void Render_GoldenFile()
    {
        // Arrange
        var config = new ClaudeMarketplaceConfiguration
        {
            Marketplaces = new List<ClaudeMarketplace>
            {
                new()
                {
                    Id = "agentic-live",
                    Label = "context& dev marketplace",
                    Source = new ClaudeMarketplaceSource { SourceType = "url", Url = "https://agentic.example.com/marketplace.json" },
                    Plugins = new List<ClaudeMarketplacePluginSnapshot>
                    {
                        new() { Name = "ctx-onboard", Version = "1.0.0", Enabled = true }
                    }
                }
            }
        };

        // Act
        var json = _renderer.Render(config);

        // Assert - compare against fixture
        var fixtureDir = Path.Combine(
            Path.GetDirectoryName(typeof(ClaudeManagedSettingsRendererTests).Assembly.Location)!,
            "..", "..", "..", "..", "fixtures", "Claude");
        var fixturePath = Path.Combine(fixtureDir, "expected-managed-settings.json");

        if (File.Exists(fixturePath))
        {
            var expected = JsonDocument.Parse(File.ReadAllText(fixturePath));
            var actual = JsonDocument.Parse(json);

            // Compare structure
            actual.RootElement.TryGetProperty("extraKnownMarketplaces", out var ekm).Should().BeTrue();
            ekm.TryGetProperty("agentic-live", out var mkt).Should().BeTrue();
            mkt.TryGetProperty("source", out var src).Should().BeTrue();
            src.TryGetProperty("source", out var srcType).Should().BeTrue();
            srcType.GetString().Should().Be("url");

            actual.RootElement.TryGetProperty("enabledPlugins", out var ep).Should().BeTrue();
            ep.TryGetProperty("ctx-onboard@agentic-live", out var pluginVal).Should().BeTrue();
            pluginVal.GetBoolean().Should().BeTrue();
        }
    }
}
