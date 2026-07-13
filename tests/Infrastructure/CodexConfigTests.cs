using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent.Codex;
using PKS.Infrastructure.Services.Agent.Foundry;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Unit tests for the <c>pks codex</c> plumbing: the idempotent managed-block writer for
/// <c>~/.codex/config.toml</c>, the Foundry responses URL normaliser, and upstream auth selection
/// (api-key for Codex deployments vs bearer for plain GPT-5).
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class CodexConfigTests
{
    // ---- managed-block writer ----

    [Fact]
    public void UpsertManagedBlock_IntoEmpty_WritesSingleBlock()
    {
        var result = CodexCliConfig.UpsertManagedBlock(null, CodexCliConfig.BuildProxyProviderBlock(8788));

        CountOccurrences(result, CodexCliConfig.BeginMarker).Should().Be(1);
        result.Should().Contain("[model_providers.pks-foundry]");
        result.Should().Contain("127.0.0.1:8788/openai/v1");
    }

    [Fact]
    public void UpsertManagedBlock_IsIdempotent_AndUpdatesPort()
    {
        var first = CodexCliConfig.UpsertManagedBlock(null, CodexCliConfig.BuildProxyProviderBlock(8788));
        var second = CodexCliConfig.UpsertManagedBlock(first, CodexCliConfig.BuildProxyProviderBlock(9999));

        CountOccurrences(second, CodexCliConfig.BeginMarker).Should().Be(1);
        CountOccurrences(second, "[model_providers.pks-foundry]").Should().Be(1);
        second.Should().Contain("127.0.0.1:9999/openai/v1");
        second.Should().NotContain("127.0.0.1:8788/openai/v1");
    }

    [Fact]
    public void UpsertManagedBlock_PreservesSurroundingUserToml()
    {
        var existing = "model = \"o3\"\nmodel_provider = \"openai\"\n\n[tui]\ntheme = \"dark\"\n";

        var result = CodexCliConfig.UpsertManagedBlock(existing, CodexCliConfig.BuildProxyProviderBlock(8788));

        result.Should().Contain("model_provider = \"openai\"");
        result.Should().Contain("[tui]");
        result.Should().Contain("theme = \"dark\"");
        result.Should().Contain("[model_providers.pks-foundry]");
        CountOccurrences(result, CodexCliConfig.BeginMarker).Should().Be(1);
    }

    [Fact]
    public void HasManagedBlockForBaseUrl_MatchesOnlyConfiguredBaseUrl()
    {
        var toml = CodexCliConfig.UpsertManagedBlock(null, CodexCliConfig.BuildProxyProviderBlock(8788));

        CodexCliConfig.HasManagedBlockForBaseUrl(toml, "http://127.0.0.1:8788/openai/v1").Should().BeTrue();
        CodexCliConfig.HasManagedBlockForBaseUrl(toml, "http://127.0.0.1:9999/openai/v1").Should().BeFalse();
        CodexCliConfig.HasManagedBlockForBaseUrl("model = \"o3\"", "http://127.0.0.1:8788/openai/v1").Should().BeFalse();
    }

    [Theory]
    [InlineData("gpt 5.6 sol", "gpt-5.6-sol")]
    [InlineData("  gpt   5.6   sol  ", "gpt-5.6-sol")]
    [InlineData("gpt-5.6-sol", "gpt-5.6-sol")]
    [InlineData("gpt 6 sol", "gpt-5.6-sol")]
    [InlineData("gpt-6-sol", "gpt-5.6-sol")]
    [InlineData("", null)]
    public void NormalizeDeploymentName_CollapsesHumanSpacing(string input, string? expected)
    {
        CodexCliConfig.NormalizeDeploymentName(input).Should().Be(expected);
    }

    // ---- responses URL normalisation ----

    [Theory]
    [InlineData("https://r.cognitiveservices.azure.com", "https://r.cognitiveservices.azure.com/openai/v1/responses")]
    [InlineData("https://r.cognitiveservices.azure.com/", "https://r.cognitiveservices.azure.com/openai/v1/responses")]
    [InlineData("https://r.openai.azure.com/openai", "https://r.openai.azure.com/openai/v1/responses")]
    [InlineData("https://r.openai.azure.com/openai/v1", "https://r.openai.azure.com/openai/v1/responses")]
    public void BuildResponsesUrl_NormalisesToV1ResponsesPath(string endpoint, string expected)
    {
        FoundryResponsesEndpoint.BuildResponsesUrl(endpoint).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://r.cognitiveservices.azure.com", "https://r.cognitiveservices.azure.com/openai/v1")]
    [InlineData("https://r.cognitiveservices.azure.com/", "https://r.cognitiveservices.azure.com/openai/v1")]
    [InlineData("https://r.openai.azure.com/openai", "https://r.openai.azure.com/openai/v1")]
    [InlineData("https://r.openai.azure.com/openai/v1", "https://r.openai.azure.com/openai/v1")]
    public void BuildOpenAiV1BaseUrl_NormalisesToV1BaseUrl(string endpoint, string expected)
    {
        CodexCliConfig.BuildOpenAiV1BaseUrl(endpoint).Should().Be(expected);
    }

    // ---- upstream auth selection ----

    [Fact]
    public async Task ApplyUpstreamAuth_PrefersApiKey_WhenPresent()
    {
        var auth = new Mock<IAzureFoundryAuthService>(MockBehavior.Strict);
        var creds = new FoundryStoredCredentials { ApiKey = "secret-key" };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://upstream/responses");

        await FoundryResponsesEndpoint.ApplyUpstreamAuthAsync(req, creds, auth.Object, "scope", default);

        req.Headers.GetValues("api-key").Should().ContainSingle().Which.Should().Be("secret-key");
        req.Headers.Authorization.Should().BeNull();
        auth.Verify(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyUpstreamAuth_FallsBackToBearer_WhenNoApiKey()
    {
        var auth = new Mock<IAzureFoundryAuthService>();
        auth.Setup(x => x.GetAccessTokenAsync("scope", It.IsAny<CancellationToken>())).ReturnsAsync("aad-token");
        var creds = new FoundryStoredCredentials { ApiKey = null };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://upstream/responses");

        await FoundryResponsesEndpoint.ApplyUpstreamAuthAsync(req, creds, auth.Object, "scope", default);

        req.Headers.Authorization.Should().NotBeNull();
        req.Headers.Authorization!.Scheme.Should().Be("Bearer");
        req.Headers.Authorization!.Parameter.Should().Be("aad-token");
        req.Headers.Contains("api-key").Should().BeFalse();
    }

    [Fact]
    public async Task ApplyUpstreamAuth_UsesBearer_WhenForcedEvenWithApiKey()
    {
        var auth = new Mock<IAzureFoundryAuthService>();
        auth.Setup(x => x.GetAccessTokenAsync("scope", It.IsAny<CancellationToken>())).ReturnsAsync("aad-token");
        var creds = new FoundryStoredCredentials { ApiKey = "secret-key" };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://upstream/responses");

        await FoundryResponsesEndpoint.ApplyUpstreamAuthAsync(req, creds, auth.Object, "scope", default, forceBearer: true);

        req.Headers.Authorization.Should().NotBeNull();
        req.Headers.Authorization!.Scheme.Should().Be("Bearer");
        req.Headers.Authorization!.Parameter.Should().Be("aad-token");
        req.Headers.Contains("api-key").Should().BeFalse();
    }

    [Fact]
    public void FilterFoundryIncompatibleAdditionalTools_RemovesOnlyCollaborationGroup()
    {
        var json = """
        {
          "model": "gpt-5.6-sol",
          "input": [
            {
              "type": "additional_tools",
              "tools": [
                { "name": "js_repl", "description": "Run JavaScript" },
                { "name": "collaboration", "description": "Tools for spawning and managing sub-agents." },
                { "name": "browser", "namespace": "browser_use" }
              ]
            },
            {
              "type": "message",
              "role": "user",
              "content": [{ "type": "input_text", "text": "test" }]
            }
          ]
        }
        """;

        var filtered = FoundryResponsesPassthrough.FilterFoundryIncompatibleAdditionalTools(
            Encoding.UTF8.GetBytes(json),
            out var summary);

        summary.Should().Contain("Removed 1 `collaboration` additional_tools entry");
        using var doc = JsonDocument.Parse(filtered);
        var tools = doc.RootElement.GetProperty("input")[0].GetProperty("tools").EnumerateArray().ToArray();
        tools.Select(t => t.GetProperty("name").GetString()).Should().Equal("js_repl", "browser");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
