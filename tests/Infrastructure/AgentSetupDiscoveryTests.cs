using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Setup;
using PKS.Infrastructure.Services.Oidc;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Discovery against a server that behaves like a real platform rather than like a mock.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public sealed class AgentSetupDiscoveryTests
{
    [Fact]
    public async Task ASinglePageAppFallback_IsReportedAsOneRatherThanAsAMissingFeature()
    {
        // The case: every route answers 200 with index.html. An unconfigured platform looks
        // IDENTICAL to one that does not support the protocol, and the reader is told the feature
        // is absent when the real fix is one proxy rule. Measured on Home's own dev server.
        var client = ClientOver(_ => Html("<!doctype html><html><body>SPA</body></html>"));

        var result = await client.DiscoverAsync("https://platform.test");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("web page", "the reader needs to know it was not JSON");
        result.Error.Should().Contain("single-page-app", "and what is answering instead");
    }

    [Fact]
    public async Task AnHonest404_SaysTheStatusCode_NotTheSpaStory()
    {
        // The other half: a platform that genuinely does not serve it must not be blamed for a
        // fallback it does not have.
        var client = ClientOver(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.DiscoverAsync("https://platform.test");

        result.Error.Should().Contain("404");
        result.Error.Should().NotContain("web page");
    }

    [Fact]
    public async Task AGoodDocument_IsStillRead_ThroughTheIndex()
    {
        // Guards the refactor: the detailed path must not have broken the happy one.
        var client = ClientOver(uri =>
            uri.AbsolutePath == "/.well-known/agentic-platform"
                ? Json("""
                    {"services":{"https://agentics.dk/rel/agent-setup/v1":
                      {"href":"https://platform.test/api/v1/setup"}}}
                    """)
                : Json("""{"v":1,"tools":["codex"],"profiles":"https://platform.test/api/v1/setup/profiles"}"""));

        var result = await client.DiscoverAsync("https://platform.test");

        result.Ok.Should().BeTrue(result.Error);
        result.Document!.Profiles.Should().Be("https://platform.test/api/v1/setup/profiles");
    }

    [Fact]
    public async Task WithNoIndex_TheWellKnownPathIsTried_BeforeThePreMoveOne()
    {
        // A platform that serves only the well-known document and publishes no index at all is
        // conforming. Before this, discovery asked for /api/v1/setup, got nothing, and told the
        // reader the host does not speak the protocol.
        var asked = new List<string>();
        var client = ClientOver(uri =>
        {
            asked.Add(uri.AbsolutePath);

            return uri.AbsolutePath == "/.well-known/agent-setup"
                ? Json("""{"v":1,"tools":["codex"],"profiles":"https://platform.test/p"}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await client.DiscoverAsync("https://platform.test");

        result.Ok.Should().BeTrue(result.Error);
        asked.Should().NotContain("/api/v1/setup",
            "the well-known path answered, so the pre-move path is never asked for");
    }

    [Fact]
    public async Task WithNoIndex_ThePreMovePathIsStillTried_WhenTheWellKnownOneIs404()
    {
        // The other direction: a platform released before the move serves only /api/v1/setup,
        // and a client that stopped asking for it would break every one of them.
        var client = ClientOver(uri =>
            uri.AbsolutePath == "/api/v1/setup"
                ? Json("""{"v":1,"tools":["codex"],"profiles":"https://platform.test/p"}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.DiscoverAsync("https://platform.test");

        result.Ok.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task WhenNeitherPathAnswers_BothAreNamed()
    {
        // "looked at X" when two were tried sends the reader to check one address and conclude
        // the other was never attempted.
        var client = ClientOver(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await client.DiscoverAsync("https://platform.test");

        result.Error.Should().Contain("/.well-known/agent-setup");
        result.Error.Should().Contain("/api/v1/setup");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html"),
    };

    private static AgentSetupClient ClientOver(Func<Uri, HttpResponseMessage> respond)
        => new(
            new HttpClient(new StubHandler(respond)),
            new OidcDiscovery(new HttpClient(new StubHandler(respond))),
            new StubCredentials(),
            new StubLoopback());

    private sealed class StubHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request.RequestUri!));
    }

    // These three tests never reach sign-in: discovery either fails or stops at the document.
    private sealed class StubLoopback : ILoopbackAuthCodeLogin
    {
        public Task<OidcLoginResult> LoginAsync(LoopbackLoginRequest request, CancellationToken ct = default)
            => Task.FromResult(new OidcLoginResult(null, null, null, 0, "not reached in these tests"));
    }

    private sealed class StubCredentials : IIssuerCredentialStore
    {
        public Task<IssuerCredentials?> LoadAsync(string issuer, CancellationToken ct = default)
            => Task.FromResult<IssuerCredentials?>(null);

        public Task SaveAsync(IssuerCredentials credentials, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<IssuerCredentials>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IssuerCredentials>>([]);

        public Task<bool> DeleteAsync(string issuer, CancellationToken ct = default) => Task.FromResult(false);
    }
}
