using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// The store tests prove the decision; these prove the answer the job actually receives. That gap
/// matters here because the refusal is the whole feature: a deploy that cannot tell which of two
/// Coolify instances it means used to pick one and succeed, so the only visible difference between
/// "deployed" and "deployed to the wrong box" was which box went live. A 409 whose body names every
/// candidate is what turns that into something the workflow log can be read for.
/// </summary>
public class CoolifyAmbiguousDeployTests : TestBase
{
    private static HttpClient ClientFor(string socketPath) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
    });

    private static (GitCredentialServer Server, JobTokenService Tokens, CoolifyTokenStore Store, List<string> Log) Build()
    {
        var log = new List<string>();
        var tokens = new JobTokenService();
        var store = new CoolifyTokenStore();

        var server = new GitCredentialServer(
            new Mock<IGitHubAuthenticationService>().Object,
            Guid.NewGuid().ToString("n")[..8],
            onLog: msg => { lock (log) log.Add(msg); },
            tokenService: tokens,
            tokenStore: store);

        return (server, tokens, store, log);
    }

    private static CoolifyAppMatch App(string name, string uuid, string instanceUrl, string role)
        => new()
        {
            Name = name,
            Uuid = uuid,
            Fqdn = $"{name}.example.test",
            EnvironmentName = "production",
            WebhookUrl = $"{instanceUrl}/api/v1/deploy?uuid={uuid}",
            InstanceUrl = instanceUrl,
            Token = "coolify-token",
            InstanceRole = role
        };

    /// <summary>
    /// The token must not name an app uuid: a uuid names one application outright, which is the
    /// documented way *out* of an ambiguity and would therefore hide the case under test.
    /// </summary>
    private static string TokenFor(JobTokenService tokens, string jobId)
        => tokens.CreateToken("pksorensen", "agentic-live-www", "main", "production", "", jobId);

    private static async Task<HttpResponseMessage> AskAsync(GitCredentialServer server, string query, string bearer)
    {
        using var http = ClientFor(server.SocketPath);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost/coolify/token?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await http.SendAsync(request);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Medium")]
    public async Task TwoInstancesHoldTheSameApp_TheJobGets409WithBothCandidates()
    {
        var (server, tokens, store, log) = Build();
        await using var _ = server;
        await server.StartAsync();

        // The real shape after the Hetzner→arvo migration: one name, two boxes, same environment.
        store.RegisterAll("job-1", new[]
        {
            App("agentics.dk", "sv70ejrj", "https://projects.kjeldager.io", CoolifyInstanceRoles.Primary),
            App("agentics.dk", "ogggg4os", "https://projects.si14agents.com", CoolifyInstanceRoles.Primary)
        });

        var response = await AskAsync(server, "app=agentics.dk&environment=production", TokenFor(tokens, "job-1"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("error").GetString().Should().Be("ambiguous app");

        var candidates = body.GetProperty("candidates").EnumerateArray().ToList();
        candidates.Should().HaveCount(2, "a refusal that names neither candidate is as hard to act on as the wrong deploy");
        candidates.Select(c => c.GetProperty("instance").GetString())
            .Should().BeEquivalentTo(new[] { "https://projects.kjeldager.io", "https://projects.si14agents.com" });

        // The hint has to carry the two ways out, because whoever reads it is mid-release.
        var hint = body.GetProperty("hint").GetString();
        hint.Should().Contain("uuid");
        hint.Should().Contain("standby");

        lock (log)
            log.Should().Contain(l => l.Contains("AMBIGUOUS (409)"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Medium")]
    public async Task MarkingTheOldBoxStandby_ResolvesTheDeployAndRecordsTheSkip()
    {
        var (server, tokens, store, log) = Build();
        await using var _ = server;
        await server.StartAsync();

        store.RegisterAll("job-2", new[]
        {
            App("agentics.dk", "ogggg4os", "https://projects.si14agents.com", CoolifyInstanceRoles.Standby),
            App("agentics.dk", "sv70ejrj", "https://projects.kjeldager.io", CoolifyInstanceRoles.Primary)
        });

        var response = await AskAsync(server, "app=agentics.dk&environment=production", TokenFor(tokens, "job-2"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("webhook_url").GetString()
            .Should().Be("https://projects.kjeldager.io/api/v1/deploy?uuid=sv70ejrj",
                "the standby is outranked regardless of the order it was registered in");

        // A deploy that went to one of two boxes is exactly the event that needs a trace afterwards.
        lock (log)
            log.Should().Contain(l => l.Contains("Tie broken by instance role") && l.Contains("standby"));
    }
}
