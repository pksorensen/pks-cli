using FluentAssertions;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.Net;
using Xunit;

namespace PKS.CLI.Tests.Services.GitHub;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class GitHubApiClientRateLimitTests
{
    [Fact]
    public async Task GetAsync_ExposesPrimaryRateLimitResetToDaemonCallers()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(37);
        using var http = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"message\":\"API rate limit exceeded\"}"),
            };
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
            return response;
        }));
        using var client = new GitHubApiClient(
            http,
            new GitHubAuthConfig { ApiBaseUrl = "https://api.github.test/" },
            new GitHubRetryPolicy { MaxRetries = 0, HandleRateLimiting = false });

        var action = () => client.GetAsync<object>("repos/example/project/actions/runs");

        var exception = await action.Should().ThrowAsync<GitHubApiException>();
        exception.Which.IsRateLimit.Should().BeTrue();
        exception.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        exception.Which.RateLimitResetAt.Should().NotBeNull();
        exception.Which.RateLimitResetAt!.Value.Should().BeCloseTo(reset.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
