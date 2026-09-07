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
/// /git-credential is the one route on this socket that never asked who was calling. It refuses
/// now, and these tests cover both sides of that: what a request outside its entitlement gets,
/// and what the log says in the opt-out mode PKS_CREDENTIAL_ENFORCE_ENTITLEMENT=0 still allows.
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

    /// <param name="enforce">
    /// Set on every server here rather than left to the environment: the default is read once,
    /// at construction, from a process-wide variable, and these tests run in parallel with
    /// everything else in the assembly.
    /// </param>
    private static (GitCredentialServer Server, JobTokenService Tokens, List<string> Log) Build(
        bool enforce = true)
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
            appTokens: appTokens.Object)
        {
            EnforceEntitlement = enforce
        };

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
    public async Task With_enforcement_off_an_unauthenticated_request_is_served_and_logged_as_such()
    {
        var (server, _, log) = Build(enforce: false);
        await server.StartAsync();
        try
        {
            using var response = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects", bearer: null);

            response.StatusCode.Should().Be(HttpStatusCode.OK, "the opt-out serves and records");
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
        var (server, _, log) = Build(enforce: false);
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
    public async Task With_enforcement_off_a_submodule_in_another_repo_is_served_and_recorded()
    {
        // The shape enforcement has to get right: a station that clones commuteconnects and then
        // fetches its commuteconnects-landing submodule asks for two repositories under one job.
        // Under enforcement it is a 403 unless the job was minted entitled to both — which is why
        // the entitlement is a list. With the opt-out set it is served and recorded instead, and
        // that is the escape hatch's whole purpose.
        var (server, tokens, log) = Build(enforce: false);
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
    public async Task A_request_outside_the_entitlement_is_refused()
    {
        var (server, tokens, _) = Build();
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

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("maybe", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    public void Enforcement_is_on_unless_someone_explicitly_turns_it_off(string? value, bool expected)
    {
        // Read as a pure function rather than by setting the variable: the default is resolved
        // once, at construction, from a process-wide value, and this assembly's tests run in
        // parallel. A typo enforces — the safe direction for a variable whose job is to weaken.
        GitCredentialServer.EnforcementFrom(value).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task A_server_with_no_way_to_validate_serves_rather_than_refusing_everything()
    {
        // Enforcement without a token service would mean refusing every request, including the
        // ones it cannot even classify. A door with no lock is not the same as a locked door,
        // and turning one into the other by default would break any host built without the
        // plumbing rather than tightening it.
        var log = new List<string>();
        var appTokens = new Mock<IGitHubAppTokenService>();
        appTokens.Setup(a => a.GetInstallationTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InstallationToken("ghs_installation", DateTimeOffset.UtcNow.AddHours(1)));

        var server = new GitCredentialServer(
            new Mock<IGitHubAuthenticationService>().Object,
            Guid.NewGuid().ToString("n")[..8],
            onLog: msg => { lock (log) log.Add(msg); },
            tokenService: null,
            appTokens: appTokens.Object)
        {
            EnforceEntitlement = true
        };

        await server.StartAsync();
        try
        {
            using var response = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects", bearer: null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task A_job_entitled_to_its_submodule_too_is_served_both()
    {
        // The counterpart to the opt-out test above: the fix for the submodule shape is to mint
        // the job entitled to both repositories, not to stop enforcing.
        var (server, tokens, _) = Build();
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-42",
                ["pksorensen/commuteconnects", "pksorensen/commuteconnects-landing"]);

            using var parent = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects", token);
            parent.StatusCode.Should().Be(HttpStatusCode.OK);

            using var submodule = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects-landing", token);
            submodule.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task A_token_minted_for_an_unparseable_clone_url_is_entitled_to_nothing()
    {
        // The ALP path mints with an empty list when the clone URL is self-hosted and does not
        // parse as owner/name. Under enforcement that has to be a refusal: the alternative is
        // handing out a GitHub credential on the strength of a name collision.
        var (server, tokens, _) = Build();
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-42", []);

            using var response = await AskAsync(server, "host=github.com&repo=pksorensen/commuteconnects", token);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally { await server.DisposeAsync(); }
    }
}
