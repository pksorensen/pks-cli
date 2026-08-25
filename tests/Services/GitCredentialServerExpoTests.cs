using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Expo;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services;

/// <summary>
/// Tests the /expo/token endpoint that vends the host's Expo robot token to a release job.
///
/// The endpoint replaces `npx expo login -u $EXPO_USERNAME -p $EXPO_PASSWORD` in CI, so the
/// property that actually matters is the scoping: a job whose repository was not registered with
/// `--expo` must not be able to spend the token, even though its per-job JWT is perfectly valid.
/// </summary>
public class GitCredentialServerExpoTests : TestBase
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

    /// <summary>An in-memory stand-in: the endpoint only consults these two members.</summary>
    private static Mock<IExpoCredentialService> ExpoService(string? token, bool allowed)
    {
        var mock = new Mock<IExpoCredentialService>();
        mock.Setup(m => m.IsRepoAllowedAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(allowed);
        mock.Setup(m => m.RevealTokenAsync()).ReturnsAsync(token);
        return mock;
    }

    private static (GitCredentialServer server, JobTokenService tokens) Build(IExpoCredentialService? expo)
    {
        var tokens = new JobTokenService();
        var auth = new Mock<IGitHubAuthenticationService>();
        var id = Guid.NewGuid().ToString("n")[..8];
        var server = new GitCredentialServer(auth.Object, id, null, tokens, null, null, null, expo);
        return (server, tokens);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task ExpoToken_Unauthorized_WithoutBearer()
    {
        var (server, _) = Build(ExpoService("secret-token", allowed: true).Object);
        await server.StartAsync();
        try
        {
            using var http = ClientFor(server.SocketPath);
            using var resp = await http.GetAsync("http://localhost/expo/token");
            resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task ExpoToken_Forbidden_WhenRepoNotRegisteredForExpo()
    {
        // The whole point of the opt-in: a valid job token for some other repo on this box
        // must not be able to spend the Expo credential.
        var (server, tokens) = Build(ExpoService("secret-token", allowed: false).Object);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("someone", "unrelated-repo", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost/expo/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var body = await resp.Content.ReadAsStringAsync();
            body.Should().NotContain("secret-token");
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task ExpoToken_ServesToken_ForRegisteredRepo()
    {
        var (server, tokens) = Build(ExpoService("secret-token", allowed: true).Object);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost/expo/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            // Guards the SecretValue trap: a masked serialization would ship "***" here and the
            // failure would surface much later, as an Expo auth error naming the wrong cause.
            doc.RootElement.GetProperty("token").GetString().Should().Be("secret-token");
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task ExpoToken_NotFound_WhenHostHasNoToken()
    {
        var (server, tokens) = Build(ExpoService(null, allowed: true).Object);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost/expo/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task ExpoToken_ServiceUnavailable_WhenNotConfigured()
    {
        var (server, tokens) = Build(null);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost/expo/token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally { await server.DisposeAsync(); }
    }
}

/// <summary>
/// Tests the repo-scoping rule and the Expo response parsing, without touching the network.
/// </summary>
public class ExpoCredentialServiceTests : TestBase
{
    private sealed class StubResolver : PKS.Infrastructure.Services.Security.ISecretResolver
    {
        private readonly string? _value;
        public StubResolver(string? value) => _value = value;
        public Task<string?> RevealAsync(string key) => Task.FromResult(_value);
    }

    private static ExpoCredentialService ServiceWith(params RunnerRegistration[] registrations)
    {
        var runners = new Mock<IRunnerConfigurationService>();
        runners.Setup(r => r.ListRegistrationsAsync()).ReturnsAsync(registrations.ToList());
        return new ExpoCredentialService(new StubResolver("t"), runners.Object);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsRepoAllowed_False_WhenRegistrationLacksExpoFlag()
    {
        // A registration written before --expo existed deserializes with ExpoEnabled = false,
        // so upgrading pks-cli must not silently grant access to every repo already on the box.
        var svc = ServiceWith(new RunnerRegistration
        {
            Owner = "pksorensen",
            Repository = "commuteconnects",
            Enabled = true,
            ExpoEnabled = false
        });

        (await svc.IsRepoAllowedAsync("pksorensen", "commuteconnects")).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsRepoAllowed_True_WhenRegisteredWithExpo()
    {
        var svc = ServiceWith(new RunnerRegistration
        {
            Owner = "pksorensen",
            Repository = "commuteconnects",
            Enabled = true,
            ExpoEnabled = true
        });

        (await svc.IsRepoAllowedAsync("PKSorensen", "CommuteConnects")).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsRepoAllowed_False_WhenRegistrationDisabled()
    {
        var svc = ServiceWith(new RunnerRegistration
        {
            Owner = "pksorensen",
            Repository = "commuteconnects",
            Enabled = false,
            ExpoEnabled = true
        });

        (await svc.IsRepoAllowedAsync("pksorensen", "commuteconnects")).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task IsRepoAllowed_False_ForUnknownRepo()
    {
        var svc = ServiceWith(new RunnerRegistration
        {
            Owner = "pksorensen",
            Repository = "commuteconnects",
            Enabled = true,
            ExpoEnabled = true
        });

        (await svc.IsRepoAllowedAsync("pksorensen", "some-other-repo")).Should().BeFalse();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("{\"data\":{\"meActor\":{\"__typename\":\"Robot\",\"id\":\"r1\",\"firstName\":\"ci-bot\"}}}", "Robot", "ci-bot")]
    [InlineData("{\"data\":{\"meActor\":{\"__typename\":\"User\",\"id\":\"u1\",\"username\":\"pks\"}}}", "User", "pks")]
    public void ParseActor_ReadsBothActorKinds(string body, string expectedType, string expectedName)
    {
        var actor = ExpoCredentialService.ParseActor(body);
        actor.Should().NotBeNull();
        actor!.Type.Should().Be(expectedType);
        actor.Name.Should().Be(expectedName);
        actor.IsRobot.Should().Be(expectedType == "Robot");
    }

    [Theory]
    [Trait("Category", "Unit")]
    // GraphQL answers 200 with an errors array for a bad token — treating that as success would
    // store a dead token and defer the failure to a release job.
    [InlineData("{\"errors\":[{\"message\":\"Unauthorized\"}],\"data\":null}")]
    [InlineData("{\"data\":{\"meActor\":null}}")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void ParseActor_RejectsNonActorResponses(string body)
        => ExpoCredentialService.ParseActor(body).Should().BeNull();
}
