using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.AgenticsProxy;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

public class AgenticsProxyTests : IAsyncLifetime
{
    private AgenticsProxy _proxy = null!;
    private AgenticsProxyOptions _options = null!;
    private readonly Mock<IAzureFoundryAuthService> _authMock = new();
    private readonly HttpClient _http = new();

    public async Task InitializeAsync()
    {
        _authMock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync("fake-azure-token");

        _options = new AgenticsProxyOptions
        {
            JobId = "test-job",
            AllowedHosts = new Dictionary<string, HostPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["example.cognitiveservices.azure.com"] = new HostPolicy(),
            },
        };

        _proxy = await AgenticsProxy.StartAsync(_options, _authMock.Object);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _proxy.DisposeAsync();
    }

    private string ProxyUrl => $"http://localhost:{_proxy.Port}";

    private async Task<string> GetCapabilityTokenAsync(string? proxyUrl = null)
    {
        proxyUrl ??= ProxyUrl;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/api/token")
        {
            Content = new StringContent(
                """{"host":"example.cognitiveservices.azure.com"}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BootstrapToken);
        using var resp = await _http.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    // ── Token endpoint ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TokenEndpoint_IssuesToken_ForAllowedHost()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ProxyUrl}/api/token")
        {
            Content = new StringContent(
                """{"host":"example.cognitiveservices.azure.com"}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BootstrapToken);

        using var resp = await _http.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        body.RootElement.GetProperty("host").GetString().Should().Be("example.cognitiveservices.azure.com");
        body.RootElement.GetProperty("expires_in").GetInt32().Should().BePositive();
    }

    [Fact]
    public async Task TokenEndpoint_Returns403_ForUnknownHost()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ProxyUrl}/api/token")
        {
            Content = new StringContent(
                """{"host":"unknown.openai.azure.com"}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BootstrapToken);

        using var resp = await _http.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TokenEndpoint_Returns401_WithWrongBootstrapToken()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ProxyUrl}/api/token")
        {
            Content = new StringContent(
                """{"host":"example.cognitiveservices.azure.com"}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        using var resp = await _http.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TokenEndpoint_Returns401_WithNoBootstrapToken()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ProxyUrl}/api/token")
        {
            Content = new StringContent(
                """{"host":"example.cognitiveservices.azure.com"}""",
                Encoding.UTF8, "application/json"),
        };
        // No Authorization header

        using var resp = await _http.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Proxy endpoint ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Proxy_Returns401_WithInvalidCapabilityToken()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ProxyUrl}/some/path");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "totally-invalid-token");

        using var resp = await _http.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Proxy_Returns401_WithNoToken()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ProxyUrl}/some/path");

        using var resp = await _http.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Proxy_Returns403_WhenPathMatchesDenyList()
    {
        var authMock = new Mock<IAzureFoundryAuthService>();
        authMock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("fake-token");
        var opts = new AgenticsProxyOptions
        {
            JobId = "test-job",
            AllowedHosts = new Dictionary<string, HostPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["example.cognitiveservices.azure.com"] = new HostPolicy
                {
                    DeniedPaths = ["/management/**"],
                },
            },
        };
        await using var proxy = await AgenticsProxy.StartAsync(opts, authMock.Object);
        var proxyUrl = $"http://localhost:{proxy.Port}";

        // Get capability token
        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/api/token")
        {
            Content = new StringContent("""{"host":"example.cognitiveservices.azure.com"}""",
                Encoding.UTF8, "application/json"),
        };
        tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.BootstrapToken);
        using var tokenResp = await _http.SendAsync(tokenReq);
        var capToken = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync())
                                   .RootElement.GetProperty("token").GetString()!;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{proxyUrl}/management/subscriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capToken);
        using var resp = await _http.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Proxy_Returns403_WhenPathNotInAllowList()
    {
        var authMock = new Mock<IAzureFoundryAuthService>();
        authMock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("fake-token");
        var opts = new AgenticsProxyOptions
        {
            JobId = "test-job",
            AllowedHosts = new Dictionary<string, HostPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["example.cognitiveservices.azure.com"] = new HostPolicy
                {
                    AllowedPaths = ["/openai/deployments/tts-hd/**"],
                },
            },
        };
        await using var proxy = await AgenticsProxy.StartAsync(opts, authMock.Object);
        var proxyUrl = $"http://localhost:{proxy.Port}";

        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/api/token")
        {
            Content = new StringContent("""{"host":"example.cognitiveservices.azure.com"}""",
                Encoding.UTF8, "application/json"),
        };
        tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.BootstrapToken);
        using var tokenResp = await _http.SendAsync(tokenReq);
        var capToken = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync())
                                   .RootElement.GetProperty("token").GetString()!;

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{proxyUrl}/openai/deployments/gpt-4/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capToken);
        using var resp = await _http.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Proxy_AllowsAnyPath_WhenAllowListEmpty()
    {
        // Empty AllowedPaths → all paths allowed. Proxy will attempt to reach Azure (fails with
        // a network error or 502), but it must NOT return 403.
        var capToken = await GetCapabilityTokenAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ProxyUrl}/any/path/at/all");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capToken);

        using var resp = await _http.SendAsync(req);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    // ── Live forwarding (runner env-var gated) ─────────────────────────────────

    [Fact]
    public async Task Live_Proxy_ForwardsTtsRequest()
    {
        var proxyUrl   = Environment.GetEnvironmentVariable("AGENTICS_PROXY_URL");
        var proxyToken = Environment.GetEnvironmentVariable("AGENTICS_PROXY_TOKEN");
        if (string.IsNullOrEmpty(proxyUrl) || string.IsNullOrEmpty(proxyToken))
            return; // skip when runner proxy is not available

        using var http = new HttpClient();

        // Step 1: get capability token
        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl.TrimEnd('/')}/api/token")
        {
            Content = new StringContent(
                """{"host":"contextand-cs-foundry.cognitiveservices.azure.com"}""",
                Encoding.UTF8, "application/json"),
        };
        tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", proxyToken);
        using var tokenResp = await http.SendAsync(tokenReq);
        tokenResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var capToken = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync())
                                   .RootElement.GetProperty("token").GetString()!;

        // Step 2: call TTS via proxy
        using var ttsReq = new HttpRequestMessage(HttpMethod.Post,
            $"{proxyUrl.TrimEnd('/')}/openai/deployments/tts-hd/audio/speech?api-version=2025-03-01-preview")
        {
            Content = new StringContent(
                """{"model":"tts-hd","input":"Hello from the proxy test.","voice":"alloy"}""",
                Encoding.UTF8, "application/json"),
        };
        ttsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capToken);
        using var ttsResp = await http.SendAsync(ttsReq);

        ttsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (ttsResp.Content.Headers.ContentType?.MediaType ?? "").Should().Contain("audio");
        (await ttsResp.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
    }
}
