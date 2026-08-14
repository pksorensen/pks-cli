using PKS.CLI.Tests.Security;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services;

public class OpenRouterServiceTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handle(request);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>
    /// The one that must not be "simplified" back to <c>/models</c>.
    ///
    /// Every other provider in this CLI validates a key by calling the model catalogue and treating
    /// 2xx as proof. OpenRouter's catalogue is public — <c>GET /api/v1/models</c> answers 200 with no
    /// credentials at all — so that check would accept any string, store it, and surface the mistake
    /// as a 401 from an unrelated completions call much later.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ValidateApiKeyAsync_asks_the_authenticated_key_endpoint_not_the_public_catalogue()
    {
        HttpRequestMessage? sent = null;
        var service = new OpenRouterService(
            new HttpClient(new Handler(request =>
            {
                sent = request;
                return Task.FromResult(Json("""{"data":{"label":"voicelab","is_free_tier":true}}"""));
            })),
            Mock.Of<IConfigurationService>(),
            FakeSecretResolver.Empty);

        var info = await service.ValidateApiKeyAsync("secret-value");

        info!.Label.Should().Be("voicelab");
        info.IsFreeTier.Should().BeTrue();
        sent!.RequestUri.Should().Be("https://openrouter.ai/api/v1/key");
        sent.RequestUri!.AbsolutePath.Should().NotContain("models");
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sent.Headers.Authorization.Parameter.Should().Be("secret-value");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ValidateApiKeyAsync_returns_null_when_openrouter_rejects_the_key()
    {
        var service = new OpenRouterService(
            new HttpClient(new Handler(_ =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))),
            Mock.Of<IConfigurationService>(),
            FakeSecretResolver.Empty);

        (await service.ValidateApiKeyAsync("nonsense")).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task StoreCredentialsAsync_persists_globally_under_the_openrouter_key()
    {
        var config = new Mock<IConfigurationService>();
        var service = new OpenRouterService(
            new HttpClient(new Handler(_ => Task.FromResult(Json("{}")))),
            config.Object,
            FakeSecretResolver.Empty);

        await service.StoreCredentialsAsync(new OpenRouterStoredCredentials
        {
            ApiKey = SecretValue.From("secret-value"),
        });

        config.Verify(x => x.SetAsync(
            "openrouter.auth.credentials",
            It.Is<string>(json => JsonSerializer.Deserialize<OpenRouterStoredCredentials>(json, SecretJson.Persistence)!.ApiKey == SecretValue.From("secret-value")),
            true,
            false));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task GetStoredKeyInfoAsync_signs_with_the_stored_credential()
    {
        HttpRequestMessage? sent = null;
        var stored = JsonSerializer.Serialize(
            new OpenRouterStoredCredentials { ApiKey = SecretValue.From("stored-value") },
            SecretJson.Persistence);

        var service = new OpenRouterService(
            new HttpClient(new Handler(request =>
            {
                sent = request;
                return Task.FromResult(Json("""{"data":{"label":"stored","limit":5.0,"limit_remaining":4.5}}"""));
            })),
            Mock.Of<IConfigurationService>(),
            new FakeSecretResolver().With("openrouter.auth.credentials", stored));

        var info = await service.GetStoredKeyInfoAsync();

        info!.Label.Should().Be("stored");
        info.LimitRemaining.Should().Be(4.5);
        sent!.Headers.Authorization!.Parameter.Should().Be("stored-value");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task GetStoredKeyInfoAsync_is_null_when_nothing_is_stored()
    {
        var service = new OpenRouterService(
            new HttpClient(new Handler(_ => throw new InvalidOperationException("must not call out"))),
            Mock.Of<IConfigurationService>(),
            FakeSecretResolver.Empty);

        (await service.GetStoredKeyInfoAsync()).Should().BeNull();
    }
}
