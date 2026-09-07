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
/// /git-credential is the one route on this socket that never asked who was calling. These tests
/// cover the observation phase: every request is still served, and the log says whether it was
/// entitled — because we do not yet know how spread the legitimate use is, and refusing before
/// measuring would break the submodule fetches nobody has counted.
///
/// The enforced behaviour is tested here too, so that flipping the switch is a configuration
/// change against tested code rather than an untested code change.
/// </summary>
public class GitCredentialEntitlementTests : TestBase
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

    private static (GitCredentialServer Server, JobTokenService Tokens, List<string> Log) Build()
    {
        var log = new List<string>();
        var tokens = new JobTokenService();

        var appTokens = new Mock<IGitHubAppTokenService>();
        appTokens.Setup(a => a.GetInstallationTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstallationToken("ghs_installation", DateTimeOffset.UtcNow.AddHours(1)));

        var server = new GitCredentialServer(
            new Mock<IGitHubAuthenticationService>().Object,
            Guid.NewGuid().ToString("n")[..8],
            onLog: msg => { lock (log) log.Add(msg); },
            tokenService: tokens,
            appTokens: appTokens.Object);

        return (server, tokens, log);
    }

    private static async Task<HttpResponseMessage> AskAsync(
        GitCredentialServer server, string query, string? bearer)
    {
        using var http = ClientFor(server.SocketPath);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost/git-credential?{query}");
        if (bearer != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await http.SendAsync(request);
    }

    private static string Entitlement(List<string> log) =>
        log.Single(l => l.StartsWith("Credential entitlement:", StringComparison.Ordinal));

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task An_unauthenticated_request_is_still_served_and_logged_as_such()
    {
        var (server, _, log) = Build();
        await server.StartAsync();
        try
        {
            using var response = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects", bearer: null);

            response.StatusCode.Should().Be(HttpStatusCode.OK, "phase A observes; it does not refuse");
            Entitlement(log).Should().Contain("auth=none").And.Contain("match=n/a");
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task A_token_we_did_not_sign_reads_as_rejected_not_as_absent()
    {
        // Both will occur in production and they have different fixes: the signing key is
        // per-process, so a container that outlives a runner restart presents a token nobody can
        // verify any more. Counting that as "no token" would hide it.
        var (server, _, log) = Build();
        await server.StartAsync();
        try
        {
            var forged = new JobTokenService().CreateToken("pksorensen", "commuteconnects", "main", "", "", "job1");

            using var response = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects", forged);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            Entitlement(log).Should().Contain("auth=rejected");
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task A_request_inside_the_entitlement_matches()
    {
        var (server, tokens, log) = Build();
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-42");

            using var response = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects.git", token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            Entitlement(log).Should().Contain("auth=ok").And.Contain("job=job-42").And.Contain("match=true");
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task A_submodule_in_another_repo_is_served_and_recorded_as_a_mismatch()
    {
        // This is the case that decides whether enforcement can be turned on at all: a station
        // that clones commuteconnects and then fetches its commuteconnects-landing submodule asks
        // for two repositories under one job. It must not break here, and it must be visible.
        var (server, tokens, log) = Build();
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-42");

            using var response = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects-landing", token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            Entitlement(log).Should()
                .Contain("requested=pksorensen/commuteconnects-landing")
                .And.Contain("entitled=[pksorensen/commuteconnects]")
                .And.Contain("match=false");
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task The_entitlement_log_never_carries_the_token_itself()
    {
        var (server, tokens, log) = Build();
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-42");

            using var response = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects", token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            // The whole point of this log is that someone reads weeks of it. A bearer in it would
            // be a credential sitting in a plaintext file with no expiry anyone remembers.
            string.Join("\n", log).Should().NotContain(token);
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task An_askpass_request_resolves_its_repository_from_the_token_not_the_shared_default()
    {
        // GIT_ASKPASS is handed a prompt, never a path, so it cannot name a repository. The
        // default field it used to fall back to is one slot on a server shared by every
        // concurrent job, so one job's SetDefaultRepository overwrites another's.
        var (server, tokens, log) = Build();
        server.SetDefaultRepository("someone-else", "another-job-repo");
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-42");

            using var response = await AskAsync(server, "host=github.com", token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            Entitlement(log).Should()
                .Contain("requested=pksorensen/commuteconnects")
                .And.NotContain("another-job-repo");
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task With_enforcement_on_a_request_outside_the_entitlement_is_refused()
    {
        var (server, tokens, _) = Build();
        server.EnforceEntitlement = true;
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-42");

            using var inside = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects", token);
            inside.StatusCode.Should().Be(HttpStatusCode.OK);

            using var outside = await AskAsync(server, "host=github.com&repo=pksorensen/some-other-repo", token);
            outside.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using var anonymous = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects", bearer: null);
            anonymous.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await server.DisposeAsync(); }
    }
}
