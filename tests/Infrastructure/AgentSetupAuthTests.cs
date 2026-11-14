using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Setup;
using PKS.Infrastructure.Services.Oidc;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// The traversal from "a URL the reader typed" to "a browser on a login page", against a server
/// that behaves like a real platform.
/// </summary>
/// <remarks>
/// Both cases here were live failures on 14/11-2026, and neither was visible to the checks that
/// had been run: every URL in the chain answered correctly on its own, and the client still could
/// not get through them. Curling the documents proves the platform; only running the client proves
/// the client.
/// </remarks>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public sealed class AgentSetupAuthTests
{
    const string Origin = "https://platform.test";
    const string Issuer = "https://login.platform.test/realms/r";

    [Fact]
    public async Task TheMetadataUrl_ComesFromTheDocument_NotFromWhereTheDocumentWasServed()
    {
        // The regression: the service document moved to /.well-known/agent-setup while the API
        // stayed on /api/v1/setup, and the client was deriving the RFC 9728 path from the
        // document's own location. It then asked for
        // /.well-known/oauth-protected-resource/.well-known/agent-setup and told the reader a
        // correctly configured platform published no metadata.
        var seen = new List<string>();
        var client = ClientOver(Platform(), seen);

        await client.ProfilesAsync(await client.DiscoverAsync(Origin), "codex", false, _ => { });

        seen.Should().Contain($"{Origin}/.well-known/oauth-protected-resource/api/v1/setup",
            "the document names this URL in `authorization` and the spec says to read the one it names");
        seen.Should().NotContain(s => s.Contains("oauth-protected-resource/.well-known"),
            "deriving the path from the document's location is what broke");
    }

    [Fact]
    public async Task AnAuthorizationOnAnotherOrigin_IsRefusedWithoutBeingFetched()
    {
        // A document naming someone else's metadata is either broken or pointing sign-in at an
        // issuer that does not own this API, and a reader who completed that flow would be typing
        // their password into whatever that issuer served.
        var seen = new List<string>();
        var doc = Platform(authorization: "https://attacker.test/.well-known/oauth-protected-resource/x");
        var client = ClientOver(doc, seen);

        var result = await client.ProfilesAsync(await client.DiscoverAsync(Origin), "codex", false, _ => { });

        result.Error.Should().Contain("different origin");
        seen.Should().NotContain(s => s.Contains("attacker.test"), "refused means not fetched");
    }

    [Fact]
    public async Task NoAuthorizationField_DerivesFromTheApi_NeverFromTheDocumentLocation()
    {
        var seen = new List<string>();
        var client = ClientOver(Platform(authorization: null), seen);

        await client.ProfilesAsync(await client.DiscoverAsync(Origin), "codex", false, _ => { });

        seen.Should().Contain($"{Origin}/.well-known/oauth-protected-resource/api/v1/setup",
            "the profiles URL minus its last segment is the resource, and the document did publish that");
    }

    [Fact]
    public async Task ABareNameAudience_IsNotSentAsAResourceIndicator()
    {
        // RFC 8707 wants an absolute URI. Our own platform published `"audience": "pks-cli"`, and
        // Keycloak answered invalid_target and redirected to the loopback — so the browser never
        // showed a login page and the failure arrived as a dead callback instead of a message.
        var login = new RecordingLoopback();
        var client = ClientOver(Platform(audience: "pks-cli"), [], login);

        await client.ProfilesAsync(await client.DiscoverAsync(Origin), "codex", false, _ => { });

        login.Request!.Resource.Should().BeNull("a name is not a resource indicator, so send none");
    }

    [Fact]
    public async Task AnAbsoluteUriAudience_IsStillSent()
    {
        // The field stays useful: Entra's own form is `api://<guid>`, which IS a URI, and a
        // platform whose API is not its own audience must still be able to say so.
        var login = new RecordingLoopback();
        var client = ClientOver(Platform(audience: $"{Origin}/api/v1/setup"), [], login);

        await client.ProfilesAsync(await client.DiscoverAsync(Origin), "codex", false, _ => { });

        login.Request!.Resource.Should().Be($"{Origin}/api/v1/setup");
    }

    /// <summary>A platform that serves its document somewhere other than its API.</summary>
    static Func<Uri, HttpResponseMessage> Platform(
        string? authorization = $"{Origin}/.well-known/oauth-protected-resource/api/v1/setup",
        string? audience = "pks-cli")
        => uri => uri.AbsolutePath switch
        {
            // Escaped rather than a raw string: three closing braces in a row collide with every
            // raw-string brace depth C# offers.
            "/.well-known/agentic-platform" => Json(
                "{\"services\":{\"https://agentsetupprotocol.com/rel/agent-setup/v1\":"
                + "{\"href\":\"" + Origin + "/.well-known/agent-setup\"}}}"),

            // Concatenated rather than interpolated: the `authorization` field has to be able to
            // be absent, and a conditional inside a raw string literal reads worse than this.
            "/.well-known/agent-setup" => Json(
                "{\"v\":1,\"tools\":[\"codex\"],\"profiles\":\"" + Origin + "/api/v1/setup/profiles\""
                + (authorization is null ? "" : ",\"authorization\":\"" + authorization + "\"")
                + "}"),

            "/.well-known/oauth-protected-resource/api/v1/setup" => Json($$"""
                {"resource":"{{Origin}}/api/v1/setup",
                 "authorization_servers":["{{Issuer}}"],
                 "client_id":"{{Origin}}/oauth/clients/pks-cli.json",
                 "audience":"{{audience}}"}
                """),

            "/realms/r/.well-known/openid-configuration" => Json($$"""
                {"issuer":"{{Issuer}}","token_endpoint":"{{Issuer}}/token",
                 "authorization_endpoint":"{{Issuer}}/auth"}
                """),

            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    static AgentSetupClient ClientOver(Func<Uri, HttpResponseMessage> respond, List<string> seen,
        ILoopbackAuthCodeLogin? login = null)
    {
        HttpClient Http() => new(new RecordingHandler(respond, seen));

        return new AgentSetupClient(Http(), new OidcDiscovery(Http()), new StubCredentials(),
            login ?? new RecordingLoopback());
    }

    sealed class RecordingHandler(Func<Uri, HttpResponseMessage> respond, List<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (seen) seen.Add(request.RequestUri!.ToString());

            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    /// <summary>Keeps the request so a test can assert on what would have hit the browser.</summary>
    sealed class RecordingLoopback : ILoopbackAuthCodeLogin
    {
        public LoopbackLoginRequest? Request { get; private set; }

        public Task<OidcLoginResult> LoginAsync(LoopbackLoginRequest request, CancellationToken ct = default)
        {
            Request = request;

            return Task.FromResult(new OidcLoginResult(null, null, null, 0, "sign-in is not under test"));
        }
    }

    sealed class StubCredentials : IIssuerCredentialStore
    {
        public Task<IssuerCredentials?> LoadAsync(string issuer, CancellationToken ct = default)
            => Task.FromResult<IssuerCredentials?>(null);

        public Task SaveAsync(IssuerCredentials credentials, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<IssuerCredentials>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IssuerCredentials>>([]);

        public Task<bool> DeleteAsync(string issuer, CancellationToken ct = default) => Task.FromResult(false);
    }
}
