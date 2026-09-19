using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Runner;
using PKS.Infrastructure.Services.TypeSafe;
using Xunit;

namespace PKS.CLI.Tests.Services;

/// <summary>
/// Tests the two TypeSafe routes on the credential socket.
///
/// The property that matters is the same as for Expo: a job whose repository was not allowed
/// with `pks typesafe allow` must not be able to spend the key, even with a valid job token. The
/// second property is specific to the proxy: the key must not appear in a proxied answer, and a
/// host-key rejection must not read as a job-token rejection.
/// </summary>
public class GitCredentialServerTypeSafeTests : TestBase
{
    private static HttpClient ClientFor(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        return new HttpClient(handler);
    }

    private static Mock<ITypeSafeCredentialService> Service(string? apiKey, bool allowed, TypeSafeEvaluation? evaluation = null)
    {
        var mock = new Mock<ITypeSafeCredentialService>();
        mock.Setup(m => m.IsRepoAllowedAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(allowed);
        mock.Setup(m => m.RevealApiKeyAsync()).ReturnsAsync(apiKey);
        mock.Setup(m => m.GetDefaultModelAsync()).ReturnsAsync("jev-latest");
        mock.Setup(m => m.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(evaluation ?? new TypeSafeEvaluation(200, "{\"model\":\"jev-latest\",\"answers\":{\"risk\":{\"type\":\"noul\",\"noul\":0.12}}}"));
        return mock;
    }

    private static (GitCredentialServer server, JobTokenService tokens) Build(ITypeSafeCredentialService? typeSafe)
    {
        var tokens = new JobTokenService();
        var auth = new Mock<IGitHubAuthenticationService>();
        var id = Guid.NewGuid().ToString("n")[..8];
        var server = new GitCredentialServer(auth.Object, id, null, tokens, typeSafe: typeSafe);
        return (server, tokens);
    }

    private static HttpRequestMessage Ask(string token) =>
        new(HttpMethod.Post, "http://localhost/typesafe/systemone")
        {
            Content = new StringContent("{\"state\":\"x\",\"questions\":{\"risk\":{\"type\":\"noul\",\"instructions\":\"risky?\"}}}", Encoding.UTF8, "application/json"),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
        };

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task SystemOne_Unauthorized_WithoutBearer()
    {
        var (server, _) = Build(Service("sk-secret", allowed: true).Object);
        await server.StartAsync();
        try
        {
            using var http = ClientFor(server.SocketPath);
            using var resp = await http.PostAsync("http://localhost/typesafe/systemone", new StringContent("{}", Encoding.UTF8, "application/json"));
            resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task SystemOne_Forbidden_WhenRepoNotAllowed()
    {
        var service = Service("sk-secret", allowed: false);
        var (server, tokens) = Build(service.Object);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("someone", "unrelated-repo", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var resp = await http.SendAsync(Ask(token));
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            // Nothing was forwarded upstream for a refused caller.
            service.Verify(m => m.EvaluateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task SystemOne_ProxiesAnswer_ForAllowedRepo_WithoutLeakingKey()
    {
        var service = Service("sk-secret", allowed: true);
        var (server, tokens) = Build(service.Object);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var resp = await http.SendAsync(Ask(token));
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await resp.Content.ReadAsStringAsync();
            body.Should().NotContain("sk-secret");
            using var doc = JsonDocument.Parse(body);
            doc.RootElement.GetProperty("answers").GetProperty("risk").GetProperty("noul").GetDouble().Should().BeApproximately(0.12, 0.0001);

            // The body the station wrote is what goes upstream, untouched by the socket.
            service.Verify(m => m.EvaluateAsync(It.Is<string>(s => s.Contains("\"risky?\"")), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task SystemOne_PassesUpstreamStatusThrough()
    {
        // A 422 from TypeSafe (malformed question) must reach the station as 422 with the body
        // that names the offending field — not be flattened into a generic error.
        var service = Service("sk-secret", allowed: true, new TypeSafeEvaluation(422, "{\"error\":\"questions.risk.criteria is required\"}"));
        var (server, tokens) = Build(service.Object);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var resp = await http.SendAsync(Ask(token));
            ((int)resp.StatusCode).Should().Be(422);
            (await resp.Content.ReadAsStringAsync()).Should().Contain("criteria is required");
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task Token_ServesKey_ForAllowedRepo()
    {
        var (server, tokens) = Build(Service("sk-secret", allowed: true).Object);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost/typesafe/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            // Guards the SecretValue trap: a masked serialization would ship "***" here.
            doc.RootElement.GetProperty("apiKey").GetString().Should().Be("sk-secret");
            doc.RootElement.GetProperty("model").GetString().Should().Be("jev-latest");
            doc.RootElement.GetProperty("baseUrl").GetString().Should().StartWith("https://");
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task Token_Forbidden_WhenRepoNotAllowed()
    {
        var (server, tokens) = Build(Service("sk-secret", allowed: false).Object);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("someone", "unrelated-repo", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost/typesafe/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await resp.Content.ReadAsStringAsync()).Should().NotContain("sk-secret");
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task Token_NotFound_WhenHostHasNoKey()
    {
        var (server, tokens) = Build(Service(null, allowed: true).Object);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost/typesafe/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task Routes_Unavailable_WhenServerBuiltWithoutService()
    {
        // The ALP runner used to build the server without the Expo service and every job got a
        // 503 — the shape this guards against is a wiring regression, not a policy question.
        var (server, tokens) = Build(null);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var resp = await http.SendAsync(Ask(token));
            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally { await server.DisposeAsync(); }
    }
}
