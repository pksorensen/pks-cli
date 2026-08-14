using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// The credential server is the one place where masking a credential would be worse than leaking it:
/// its whole job is to hand git the token, and the response is an anonymous object serialized with
/// default options — which masks <see cref="SecretValue"/> by design. Nothing else in the suite would
/// notice a regression here, because a masked token still produces a well-formed 200 response and only
/// fails much later, inside a container, as an authentication error that names the wrong cause.
/// </summary>
public class GitCredentialServerTests
{
    private static HttpClient ClientFor(string socketPath) => new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
    })
    { BaseAddress = new Uri("http://localhost") };

    private static GitCredentialServer ServerWith(GitHubStoredToken? stored, string socketId)
    {
        var auth = new Mock<IGitHubAuthenticationService>();
        auth.Setup(a => a.GetStoredTokenAsync(It.IsAny<string?>())).ReturnsAsync(stored);
        return new GitCredentialServer(auth.Object, socketId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Medium")]
    public async Task ServesThePlaintextToken_NotTheMask()
    {
        await using var server = ServerWith(
            new GitHubStoredToken { AccessToken = SecretValue.From("gho_live_token"), IsValid = true },
            $"test-{Guid.NewGuid():N}");
        await server.StartAsync();

        using var client = ClientFor(server.SocketPath);
        var response = await client.GetAsync("/git-credential?host=github.com");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("password").GetString().Should().Be("gho_live_token");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Medium")]
    public async Task ReportsUnavailableWhenNothingIsStored()
    {
        // Absent is answered as 503, not as an empty password: a blank credential looks to git like a
        // wrong one, and whoever debugs it goes after the wrong problem.
        await using var server = ServerWith(null, $"test-{Guid.NewGuid():N}");
        await server.StartAsync();

        using var client = ClientFor(server.SocketPath);
        var response = await client.GetAsync("/git-credential?host=github.com");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }
}
