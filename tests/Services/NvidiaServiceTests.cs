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

public class NvidiaServiceTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handle(request);
    }

    private static NvidiaService Service(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle,
        IConfigurationService? config = null, ISecretResolver? secrets = null) =>
        new(new HttpClient(new Handler(handle)),
            config ?? Mock.Of<IConfigurationService>(),
            secrets ?? FakeSecretResolver.Empty);

    /// <summary>
    /// NVIDIA's <c>/v1/models</c> is a public catalogue that answers 200 for no bearer and 200 again
    /// for a garbage bearer, so the "call /models and trust 2xx" check every other provider uses
    /// would accept any string here. There is no <c>/key</c> endpoint either — a one-token
    /// completion is the cheapest authenticated question NVIDIA answers.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ValidateApiKeyAsync_probes_chat_completions_not_the_public_catalogue()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var service = Service(async request =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await service.ValidateApiKeyAsync("nvapi-secret");

        result.IsValid.Should().BeTrue();
        sent!.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri.Should().Be("https://integrate.api.nvidia.com/v1/chat/completions");
        sent.RequestUri!.AbsolutePath.Should().NotContain("models");
        sent.Headers.Authorization!.Parameter.Should().Be("nvapi-secret");
        body.Should().Contain("\"max_tokens\":1", "the probe must not buy more inference than it needs");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ValidateApiKeyAsync_treats_401_and_403_as_a_rejected_key(HttpStatusCode status)
    {
        var service = Service(_ => Task.FromResult(new HttpResponseMessage(status)));

        var result = await service.ValidateApiKeyAsync("nvapi-wrong");

        result.Verdict.Should().Be(NvidiaKeyVerdict.Rejected);
        result.StatusCode.Should().Be((int)status);
    }

    /// <summary>
    /// The distinction that stops someone rotating a working credential: a retired probe model or a
    /// bad gateway is not evidence about the key.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ValidateApiKeyAsync_does_not_blame_the_key_for_other_failures(HttpStatusCode status)
    {
        var service = Service(_ => Task.FromResult(new HttpResponseMessage(status)));

        (await service.ValidateApiKeyAsync("nvapi-fine")).Verdict
            .Should().Be(NvidiaKeyVerdict.Inconclusive);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ValidateApiKeyAsync_is_inconclusive_when_nvidia_is_unreachable()
    {
        var service = Service(_ => throw new HttpRequestException("no route to host"));

        (await service.ValidateApiKeyAsync("nvapi-fine")).Verdict
            .Should().Be(NvidiaKeyVerdict.Inconclusive);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task StoreCredentialsAsync_persists_globally_under_the_nvidia_key()
    {
        var config = new Mock<IConfigurationService>();
        var service = Service(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), config.Object);

        await service.StoreCredentialsAsync(new NvidiaStoredCredentials
        {
            ApiKey = SecretValue.From("nvapi-secret"),
        });

        config.Verify(x => x.SetAsync(
            "nvidia.auth.credentials",
            It.Is<string>(json => JsonSerializer.Deserialize<NvidiaStoredCredentials>(json, SecretJson.Persistence)!.ApiKey == SecretValue.From("nvapi-secret")),
            true,
            false));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ValidateStoredKeyAsync_signs_with_the_stored_credential()
    {
        HttpRequestMessage? sent = null;
        var stored = JsonSerializer.Serialize(
            new NvidiaStoredCredentials { ApiKey = SecretValue.From("nvapi-stored") },
            SecretJson.Persistence);

        var service = Service(
            request => { sent = request; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); },
            secrets: new FakeSecretResolver().With("nvidia.auth.credentials", stored));

        (await service.ValidateStoredKeyAsync()).IsValid.Should().BeTrue();
        sent!.Headers.Authorization!.Parameter.Should().Be("nvapi-stored");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ValidateStoredKeyAsync_does_not_call_out_when_nothing_is_stored()
    {
        var service = Service(_ => throw new InvalidOperationException("must not call out"));

        (await service.ValidateStoredKeyAsync()).Verdict.Should().Be(NvidiaKeyVerdict.Rejected);
    }
}
