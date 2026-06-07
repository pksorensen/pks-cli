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

namespace PKS.CLI.Tests.Services;

/// <summary>Tests the /cert/pfx endpoint that vends a materialized signing PFX to in-container `pks sign`.</summary>
public class GitCredentialServerCertTests : TestBase
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

    private (GitCredentialServer server, JobTokenService tokens, string id) Build(out CertStore store)
    {
        var dir = Path.Combine(CreateTempDirectory(), "certs");
        store = new CertStore(dir);
        var tokens = new JobTokenService();
        var auth = new Mock<IGitHubAuthenticationService>();
        var id = Guid.NewGuid().ToString("n")[..8];
        var server = new GitCredentialServer(auth.Object, id, null, tokens, null, null, store);
        return (server, tokens, id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task CertPfx_Unauthorized_WithoutBearer()
    {
        var (server, _, _) = Build(out _);
        await server.StartAsync();
        try
        {
            using var http = ClientFor(server.SocketPath);
            using var resp = await http.GetAsync("http://localhost/cert/pfx");
            resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task CertPfx_ReturnsServeablePfx_WithValidToken()
    {
        var (server, tokens, _) = Build(out var store);
        await store.CreateSelfSignedAsync("CN=Test", "agentics", TimeSpan.FromDays(365));
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("o", "r", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost/cert/pfx");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var pfxB64 = doc.RootElement.GetProperty("pfxBase64").GetString();
            var pwd = doc.RootElement.GetProperty("password").GetString();
            pfxB64.Should().NotBeNullOrEmpty();

            using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadPkcs12(Convert.FromBase64String(pfxB64!), pwd);
            cert.HasPrivateKey.Should().BeTrue();
        }
        finally { await server.DisposeAsync(); }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Medium")]
    public async Task CertPfx_404_WhenNoCert()
    {
        var (server, tokens, _) = Build(out _);
        await server.StartAsync();
        try
        {
            var token = tokens.CreateToken("o", "r", "main", "", "", "job1");
            using var http = ClientFor(server.SocketPath);
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost/cert/pfx");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await server.DisposeAsync(); }
    }
}
