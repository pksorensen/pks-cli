using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
        result.Should().Contain("request_max_retries = 4");
        result.Should().Contain("stream_max_retries = 3");
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

    [Fact]
    public void ConfigureKestrel_AllowsLargeCodexSessionRequestsWithinBoundedLimit()
    {
        var options = new KestrelServerOptions();

        FoundryResponsesPassthrough.ConfigureKestrel(options);

        options.Limits.MaxRequestBodySize.Should().Be(256L * 1024 * 1024);
        options.Limits.MaxRequestBodySize.Should().BeGreaterThan(30_000_000);
    }

    [Fact]
    public void AnalyzeSseEvent_NullErrorFailure_IsRetryableBeforeOutput()
    {
        var payload = """
        {"type":"response.failed","response":{"id":"resp_123","status":"failed","error":null}}
        """;

        var result = FoundryResponsesPassthrough.AnalyzeSseEvent("response.failed", payload);

        result.IsFailure.Should().BeTrue();
        result.IsRetryableFailure.Should().BeTrue();
        result.CommitsOutput.Should().BeFalse();
        result.ResponseId.Should().Be("resp_123");
        result.EventType.Should().Be("response.failed");
    }

    [Fact]
    public void AnalyzeSseEvent_InvalidRequestFailure_IsNotRetried()
    {
        var payload = """
        {"type":"response.failed","response":{"id":"resp_123","status":"failed","error":{"code":"invalid_request_error","message":"bad input"}}}
        """;

        var result = FoundryResponsesPassthrough.AnalyzeSseEvent("response.failed", payload);

        result.IsFailure.Should().BeTrue();
        result.IsRetryableFailure.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_request_error");
        result.ErrorMessage.Should().Be("bad input");
    }

    [Fact]
    public void AnalyzeSseEvent_ReasoningItem_DoesNotCommitAttempt()
    {
        var payload = """
        {"type":"response.output_item.added","item":{"id":"rs_123","type":"reasoning","content":[]}}
        """;

        var result = FoundryResponsesPassthrough.AnalyzeSseEvent("response.output_item.added", payload);

        result.CommitsOutput.Should().BeFalse();
        result.IsFailure.Should().BeFalse();
    }

    [Fact]
    public void AnalyzeSseEvent_UnknownModelFailure_IsRetryable()
    {
        var payload = """
        {"type":"response.failed","response":{"id":"resp_123","error":{"code":"unknown","message":"invalid content"}}}
        """;

        var result = FoundryResponsesPassthrough.AnalyzeSseEvent("response.failed", payload);

        result.IsRetryableFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("unknown");
    }

    [Theory]
    [InlineData("response.output_text.delta")]
    [InlineData("response.custom_tool_call_input.delta")]
    public void AnalyzeSseEvent_OutputDelta_CommitsAttempt(string eventType)
    {
        var payload = $$"""{"type":"{{eventType}}","delta":"x"}""";

        var result = FoundryResponsesPassthrough.AnalyzeSseEvent(eventType, payload);

        result.CommitsOutput.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.EventType.Should().Be(eventType);
    }

    [Fact]
    public void AnalyzeSseEvent_PayloadTypeIsCapturedWhenEventHeaderIsMissing()
    {
        const string payload = """{"type":"response.reasoning_summary_text.delta","delta":"x"}""";

        var result = FoundryResponsesPassthrough.AnalyzeSseEvent(null, payload);

        result.EventType.Should().Be("response.reasoning_summary_text.delta");
        result.CommitsOutput.Should().BeTrue();
    }

    [Fact]
    public void ShouldCommitSseEvent_FullResponseBufferingDefersSemanticOutput()
    {
        const string payload = """{"type":"response.custom_tool_call_input.delta","delta":"x"}""";
        var analysis = FoundryResponsesPassthrough.AnalyzeSseEvent(
            "response.custom_tool_call_input.delta", payload);

        FoundryResponsesPassthrough.ShouldCommitSseEvent(analysis, bufferFullResponse: true)
            .Should().BeFalse();
        FoundryResponsesPassthrough.ShouldCommitSseEvent(analysis, bufferFullResponse: false)
            .Should().BeTrue();
    }

    [Fact]
    public void FullResponseBuffering_IsEnabledByDefault()
    {
        FoundryResponsesPassthrough.DefaultBufferFullResponse.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, true, 1, 2, 0, false)]
    [InlineData(true, true, 2, 2, 0, true)]
    [InlineData(false, true, 2, 2, 0, false)]
    [InlineData(true, false, 2, 2, 0, false)]
    [InlineData(true, true, 2, 2, 1, false)]
    public void ShouldBustPromptCache_RequiresThresholdAndAllowsOneRotationPerRequest(
        bool enabled,
        bool hasCacheKey,
        int errors,
        int threshold,
        int rotations,
        bool expected)
    {
        FoundryResponsesPassthrough.ShouldBustPromptCache(
                enabled, hasCacheKey, errors, threshold, rotations)
            .Should().Be(expected);
    }

    [Fact]
    public void NullErrorResponseFailed_TriggersPromptCacheRecoveryAfterThreshold()
    {
        const string payload =
            """{"type":"response.failed","response":{"id":"resp_123","status":"failed","error":null}}""";
        var failure = FoundryResponsesPassthrough.AnalyzeSseEvent("response.failed", payload);

        var consecutiveCacheFailures = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (FoundryResponsesPassthrough.CountsTowardPromptCacheRecovery(
                    failure.IsRetryableFailure, failure.EventType, failure.ErrorCode))
            {
                consecutiveCacheFailures++;
            }
        }

        consecutiveCacheFailures.Should().Be(2);
        FoundryResponsesPassthrough.ShouldBustPromptCache(
                enabled: true,
                hasCacheKey: true,
                consecutiveCacheFailures,
                threshold: 2,
                cacheBustsThisRequest: 0)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(true, "response.failed", null, true)]
    [InlineData(true, "response.failed", "server_error", true)]
    [InlineData(true, "response.failed", "unknown", true)]
    [InlineData(true, null, null, false)]
    [InlineData(false, "response.failed", null, false)]
    [InlineData(true, "response.failed", "rate_limit_exceeded", false)]
    public void CountsTowardPromptCacheRecovery_OnlyCountsCacheLikeResponseFailures(
        bool retryable,
        string? terminalEventType,
        string? errorCode,
        bool expected)
    {
        FoundryResponsesPassthrough.CountsTowardPromptCacheRecovery(
                retryable, terminalEventType, errorCode)
            .Should().Be(expected);
    }

    [Fact]
    public void ReplacePromptCacheKey_ChangesOnlyCacheIdentity()
    {
        var original = Encoding.UTF8.GetBytes(
            """{"model":"gpt-5.6-sol","prompt_cache_key":"old-session","input":[{"type":"message"}]}""");

        var replaced = FoundryResponsesPassthrough.ReplacePromptCacheKey(original, "new-session");
        using var document = JsonDocument.Parse(replaced);

        document.RootElement.GetProperty("model").GetString().Should().Be("gpt-5.6-sol");
        document.RootElement.GetProperty("prompt_cache_key").GetString().Should().Be("new-session");
        document.RootElement.GetProperty("input").GetArrayLength().Should().Be(1);
        FoundryResponsesPassthrough.GetPromptCacheKey(replaced).Should().Be("new-session");
        FoundryResponsesPassthrough.HashCacheKey("new-session").Should().HaveLength(12);
    }

    [Fact]
    public void PromptCacheRecovery_DefaultsAreBounded()
    {
        FoundryResponsesPassthrough.DefaultCacheBustOnServerError.Should().BeTrue();
        FoundryResponsesPassthrough.DefaultCacheBustAfterErrors.Should().Be(2);
        FoundryResponsesPassthrough.DefaultCacheBustMaxRotations.Should().Be(3);
    }

    [Fact]
    public void AnalyzeSseEvent_Completed_IsTerminalButDoesNotRequireRetry()
    {
        var payload = """{"type":"response.completed","response":{"id":"resp_ok","status":"completed"}}""";

        var result = FoundryResponsesPassthrough.AnalyzeSseEvent("response.completed", payload);

        result.IsCompleted.Should().BeTrue();
        result.IsFailure.Should().BeFalse();
        result.ResponseId.Should().Be("resp_ok");
    }

    [Theory]
    [InlineData(1, 2000)]
    [InlineData(2, 4000)]
    [InlineData(3, 8000)]
    [InlineData(4, 16000)]
    [InlineData(5, 30000)]
    public void CalculateRetryDelay_IsExponentialAndCapped(int retryNumber, int expectedMs)
    {
        var delay = FoundryResponsesPassthrough.CalculateRetryDelay(retryNumber, 2_000, 30_000, jitterSample: 0);

        delay.TotalMilliseconds.Should().Be(expectedMs);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
