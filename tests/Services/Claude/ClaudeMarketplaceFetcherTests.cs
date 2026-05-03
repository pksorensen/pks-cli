using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Claude;
using System.Net;
using System.Net.Http;
using Xunit;

namespace PKS.CLI.Tests.Services.Claude;

public class ClaudeMarketplaceFetcherTests : TestBase
{
    private ClaudeMarketplaceFetcher CreateFetcher(HttpMessageHandler? handler = null)
    {
        var httpClient = handler != null
            ? new HttpClient(handler)
            : new HttpClient();
        return new ClaudeMarketplaceFetcher(httpClient);
    }

    [Fact]
    [Trait("Category", "Claude")]
    public void ParseSource_HttpsUrl_ReturnsUrlSource()
    {
        // Arrange
        var fetcher = CreateFetcher();
        var input = "https://example.com/marketplace.json";

        // Act
        var source = fetcher.ParseSource(input);

        // Assert
        source.SourceType.Should().Be("url");
        source.Url.Should().Be("https://example.com/marketplace.json");
    }

    [Fact]
    [Trait("Category", "Claude")]
    public void ParseSource_GithubShorthand_ReturnsGithubSource()
    {
        // Arrange
        var fetcher = CreateFetcher();
        var input = "github:pksorensen/pks-marketplace";

        // Act
        var source = fetcher.ParseSource(input);

        // Assert
        source.SourceType.Should().Be("github");
        source.Repo.Should().Be("pksorensen/pks-marketplace");
        source.Ref.Should().Be("main");
    }

    [Fact]
    [Trait("Category", "Claude")]
    public void ParseSource_GithubShorthandWithRef_ReturnsGithubSourceWithRef()
    {
        // Arrange
        var fetcher = CreateFetcher();
        var input = "github:pksorensen/pks-marketplace@v2";

        // Act
        var source = fetcher.ParseSource(input);

        // Assert
        source.SourceType.Should().Be("github");
        source.Repo.Should().Be("pksorensen/pks-marketplace");
        source.Ref.Should().Be("v2");
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task FetchAsync_UrlSource_ParsesMarketplaceJson()
    {
        // Arrange — Anthropic's marketplace.json schema uses "name" (not "id") for the
        // marketplace identifier. This test guards the field name we depend on.
        var marketplaceJson = """
            {
                "name": "agentic-live",
                "label": "context& dev marketplace",
                "plugins": [
                    { "name": "ctx-onboard", "version": "1.0.0", "description": "Onboarding plugin" }
                ]
            }
            """;

        var handler = new FakeHttpMessageHandler(marketplaceJson, HttpStatusCode.OK);
        var fetcher = CreateFetcher(handler);
        var source = new ClaudeMarketplaceSource
        {
            SourceType = "url",
            Url = "https://example.com/marketplace.json"
        };

        // Act
        var result = await fetcher.FetchAsync(source);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("agentic-live");
        result.Label.Should().Be("context& dev marketplace");
        result.Plugins.Should().HaveCount(1);
        result.Plugins[0].Name.Should().Be("ctx-onboard");
        result.Plugins[0].Version.Should().Be("1.0.0");
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task FetchAsync_OnlyIdField_ReturnsEmptyName()
    {
        // Regression guard: a marketplace.json that only has "id" (and no "name") must
        // NOT be silently accepted — Anthropic's schema requires "name". Prior bug:
        // we read "id" and stored an empty marketplace key, breaking enabledPlugins.
        var marketplaceJson = """
            { "id": "agentic-live", "plugins": [] }
            """;

        var handler = new FakeHttpMessageHandler(marketplaceJson, HttpStatusCode.OK);
        var fetcher = CreateFetcher(handler);
        var source = new ClaudeMarketplaceSource { SourceType = "url", Url = "https://example.com/m.json" };

        var result = await fetcher.FetchAsync(source);

        result.Name.Should().BeEmpty();
    }
}

/// <summary>
/// Fake HTTP message handler for testing HTTP calls
/// </summary>
internal class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseContent;
    private readonly HttpStatusCode _statusCode;

    public FakeHttpMessageHandler(string responseContent, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responseContent = responseContent;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseContent)
        });
    }
}
