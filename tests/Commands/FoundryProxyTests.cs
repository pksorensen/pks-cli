using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace PKS.CLI.Tests.Commands;

/// <summary>
/// Integration tests for pks foundry proxy.
/// These tests require the proxy to be running externally.
///
/// Usage:
///   Terminal 1: eval $(pks foundry proxy)
///   Terminal 2: FOUNDRY_PROXY_URL=$FOUNDRY_PROXY_URL FOUNDRY_PROXY_TOKEN=$FOUNDRY_PROXY_TOKEN \
///               dotnet test --filter FoundryProxyTests
///
/// Tests are skipped gracefully when env vars are not set.
/// </summary>
public class FoundryProxyTests
{
    private const string TtsPath =
        "/openai/deployments/tts-hd/audio/speech?api-version=2025-03-01-preview";

    private static (string? proxyUrl, string? proxyToken) GetProxyConfig() =>
        (Environment.GetEnvironmentVariable("FOUNDRY_PROXY_URL"),
         Environment.GetEnvironmentVariable("FOUNDRY_PROXY_TOKEN"));

    [Fact]
    public async Task Proxy_ForwardsTtsRequest_ReturnsAudioBytes()
    {
        var (proxyUrl, proxyToken) = GetProxyConfig();
        if (string.IsNullOrEmpty(proxyUrl) || string.IsNullOrEmpty(proxyToken))
            return; // proxy not running — skip

        var url = proxyUrl.TrimEnd('/') + TtsPath;
        using var http = new HttpClient();

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                """{"model":"tts-hd","input":"Hello from the proxy test.","voice":"alloy"}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", proxyToken);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        Assert.Contains("audio", contentType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0, "Expected non-empty audio response body");
    }

    [Fact]
    public async Task Proxy_WithWrongToken_Returns401()
    {
        var (proxyUrl, _) = GetProxyConfig();
        if (string.IsNullOrEmpty(proxyUrl))
            return; // proxy not running — skip

        var url = proxyUrl.TrimEnd('/') + TtsPath;
        using var http = new HttpClient();

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                """{"model":"tts-hd","input":"Should be rejected.","voice":"alloy"}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token-abc123");

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Proxy_WithNoToken_Returns401()
    {
        var (proxyUrl, _) = GetProxyConfig();
        if (string.IsNullOrEmpty(proxyUrl))
            return; // proxy not running — skip

        var url = proxyUrl.TrimEnd('/') + TtsPath;
        using var http = new HttpClient();

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                """{"model":"tts-hd","input":"No token.","voice":"alloy"}""",
                Encoding.UTF8, "application/json"),
        };
        // No Authorization header

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
