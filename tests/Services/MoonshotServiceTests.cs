using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services;

public class MoonshotServiceTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handle(request);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_calls_models_with_a_bearer_token()
    {
        HttpRequestMessage? sent = null;
        var service = new MoonshotService(
            new HttpClient(new Handler(request =>
            {
                sent = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })),
            Mock.Of<IConfigurationService>());

        var valid = await service.ValidateApiKeyAsync("secret-value");

        valid.Should().BeTrue();
        sent!.RequestUri.Should().Be("https://api.moonshot.ai/v1/models");
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sent.Headers.Authorization.Parameter.Should().Be("secret-value");
    }

    [Fact]
    public async Task StoreCredentialsAsync_persists_globally_under_the_moonshot_key()
    {
        var config = new Mock<IConfigurationService>();
        var service = new MoonshotService(new HttpClient(new Handler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))), config.Object);

        await service.StoreCredentialsAsync(new MoonshotStoredCredentials { ApiKey = "secret-value" });

        config.Verify(x => x.SetAsync(
            "moonshot.auth.credentials",
            It.Is<string>(json => JsonSerializer.Deserialize<MoonshotStoredCredentials>(json)!.ApiKey == "secret-value"),
            true,
            false));
    }
}
