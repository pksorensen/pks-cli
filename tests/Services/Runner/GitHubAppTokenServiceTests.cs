using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for GitHubAppTokenService — the RS256 JWT, the installation lookup, and the
/// narrowing of the minted token.
///
/// Every test here generates its own throwaway RSA key. No real App key is involved, and none
/// should ever be: the key is the one thing in this design that must not travel.
/// </summary>
public class GitHubAppTokenServiceTests
{
    private const string AppId = "123456";
    private const string Slug = "si14-x";

    /// <summary>Records what was asked and answers from a script, so the request can be asserted on.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = _respond(request);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>A hand-wound clock. Avoids a package reference for two assertions.</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void SetUtcNow(DateTimeOffset now) => _now = now;
    }

    private static (string pem, RSA key) NewKey()
    {
        var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa);
    }

    private static (GitHubAppTokenService svc, StubHandler handler) Build(
        Func<HttpRequestMessage, (HttpStatusCode, string)> respond,
        string? pem = null,
        TimeProvider? time = null)
    {
        pem ??= NewKey().pem;
        var handler = new StubHandler(respond);
        var svc = new GitHubAppTokenService(
            new GitHubAppConfig(AppId, Slug, pem), new HttpClient(handler), timeProvider: time);
        return (svc, handler);
    }

    private static (HttpStatusCode, string) Script(HttpRequestMessage r)
    {
        if (r.RequestUri!.AbsolutePath.EndsWith("/installation"))
            return (HttpStatusCode.OK, """{"id": 42}""");
        if (r.RequestUri.AbsolutePath.Contains("/access_tokens"))
            return (HttpStatusCode.Created, """{"token":"ghs_minted","expires_at":"2030-01-01T00:00:00Z"}""");
        return (HttpStatusCode.NotFound, "{}");
    }

    [Fact]
    public void Constructor_RejectsAKeyThatIsNotAPem()
    {
        var act = () => new GitHubAppTokenService(new GitHubAppConfig(AppId, Slug, "not a key at all"));
        act.Should().Throw<ArgumentException>().WithMessage("*could not be parsed*");
    }

    [Fact]
    public void Constructor_RequiresASlug()
    {
        var act = () => new GitHubAppTokenService(new GitHubAppConfig(AppId, "", NewKey().pem));
        act.Should().Throw<ArgumentException>().WithMessage("*slug is required*");
    }

    [Fact]
    public void Constructor_AcceptsPkcs8AsWellAsPkcs1()
    {
        using var rsa = RSA.Create(2048);
        var act = () => new GitHubAppTokenService(new GitHubAppConfig(AppId, Slug, rsa.ExportPkcs8PrivateKeyPem()));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Jwt_IsRs256_SignedByTheAppKey_AndIssuedByTheAppId()
    {
        var (pem, key) = NewKey();
        var (svc, handler) = Build(Script, pem);
        using var _ = svc;

        await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects");

        var jwt = handler.Requests[0].Headers.Authorization!.Parameter!;
        var parts = jwt.Split('.');
        parts.Should().HaveCount(3);

        var header = JsonDocument.Parse(Decode(parts[0])).RootElement;
        header.GetProperty("alg").GetString().Should().Be("RS256");

        var payload = JsonDocument.Parse(Decode(parts[1])).RootElement;
        payload.GetProperty("iss").GetString().Should().Be(AppId);

        // GitHub rejects a JWT that lives longer than 10 minutes, and one issued in its own future.
        var iat = payload.GetProperty("iat").GetInt64();
        var exp = payload.GetProperty("exp").GetInt64();
        (exp - iat).Should().BeLessThanOrEqualTo(600);
        iat.Should().BeLessThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // And it really is signed by the App's key — a JWT we cannot verify here is one GitHub would reject.
        key.VerifyData(
            Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"),
            Decode(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).Should().BeTrue();
    }

    [Fact]
    public async Task FindInstallationId_ReturnsNull_WhenTheAppIsNotInstalled()
    {
        var (svc, _) = Build(_ => (HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        using var _s = svc;

        (await svc.FindInstallationIdAsync("pksorensen", "commuteconnects")).Should().BeNull();
    }

    [Fact]
    public async Task GetInstallationToken_ThrowsWithTheInstallUrl_WhenNotInstalled()
    {
        var (svc, _) = Build(_ => (HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        using var _s = svc;

        var act = async () => await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects");

        var ex = (await act.Should().ThrowAsync<GitHubAppNotInstalledException>()).Which;
        ex.InstallUrl.Should().Be("https://github.com/apps/si14-x/installations/new");
        ex.Message.Should().Contain("pksorensen/commuteconnects");
    }

    [Fact]
    public async Task MintedToken_IsNarrowedToOneRepoAndTwoPermissions()
    {
        var (svc, handler) = Build(Script);
        using var _ = svc;

        var token = await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects");
        token.Token.Should().Be("ghs_minted");

        var mint = handler.Requests.Single(r => r.RequestUri!.AbsolutePath.Contains("/access_tokens"));
        mint.RequestUri!.AbsolutePath.Should().Be("/app/installations/42/access_tokens");

        var body = JsonDocument.Parse(handler.Bodies[handler.Requests.IndexOf(mint)]).RootElement;
        body.GetProperty("repositories").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal("commuteconnects");
        body.GetProperty("permissions").GetProperty("contents").GetString().Should().Be("write");
        body.GetProperty("permissions").GetProperty("pull_requests").GetString().Should().Be("write");
        body.GetProperty("permissions").EnumerateObject().Should().HaveCount(2);
    }

    [Fact]
    public async Task Requests_CarryTheHeadersGitHubRequires()
    {
        var (svc, handler) = Build(Script);
        using var _ = svc;

        await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects");

        foreach (var request in handler.Requests)
        {
            request.Headers.UserAgent.ToString().Should().NotBeEmpty();
            request.Headers.Accept.ToString().Should().Contain("application/vnd.github+json");
            request.Headers.GetValues("X-GitHub-Api-Version").Should().ContainSingle();
        }
    }

    [Fact]
    public async Task ASecondCallForTheSameRepoReusesTheToken()
    {
        var (svc, handler) = Build(Script);
        using var _ = svc;

        var first = await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects");
        var second = await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects");

        second.Token.Should().Be(first.Token);
        handler.Requests.Count(r => r.RequestUri!.AbsolutePath.Contains("/access_tokens")).Should().Be(1);
    }

    [Fact]
    public async Task DifferentReposGetDifferentTokens()
    {
        var (svc, handler) = Build(Script);
        using var _ = svc;

        await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects");
        await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects-landing");

        handler.Requests.Count(r => r.RequestUri!.AbsolutePath.Contains("/access_tokens")).Should().Be(2);
        handler.Bodies.Where(b => b.Contains("repositories"))
            .Should().HaveCount(2)
            .And.Subject.Last().Should().Contain("commuteconnects-landing");
    }

    [Fact]
    public async Task ATokenCloseToExpiryIsMintedAgain()
    {
        // Expires 20:00; the refresh margin is 5 minutes, so it is live at 19:50 and spent at 19:56.
        var time = new TestClock(new DateTimeOffset(2026, 1, 1, 19, 0, 0, TimeSpan.Zero));
        var (svc, handler) = Build(
            r => r.RequestUri!.AbsolutePath.EndsWith("/installation")
                ? (HttpStatusCode.OK, """{"id": 42}""")
                : (HttpStatusCode.Created, """{"token":"ghs_minted","expires_at":"2026-01-01T20:00:00Z"}"""),
            time: time);
        using var _ = svc;

        await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects");

        time.SetUtcNow(new DateTimeOffset(2026, 1, 1, 19, 50, 0, TimeSpan.Zero));
        await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects");
        handler.Requests.Count(r => r.RequestUri!.AbsolutePath.Contains("/access_tokens"))
            .Should().Be(1, "a token with 10 minutes left is still usable");

        time.SetUtcNow(new DateTimeOffset(2026, 1, 1, 19, 56, 0, TimeSpan.Zero));
        await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects");
        handler.Requests.Count(r => r.RequestUri!.AbsolutePath.Contains("/access_tokens"))
            .Should().Be(2, "inside the refresh margin the token is treated as spent");
    }

    [Fact]
    public async Task AnApiFailureSurfacesTheStatusAndBody()
    {
        var (svc, _) = Build(_ => (HttpStatusCode.Unauthorized, """{"message":"A JWT could not be decoded"}"""));
        using var _s = svc;

        var act = async () => await svc.GetInstallationTokenAsync("pksorensen", "commuteconnects");
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*401*").WithMessage("*could not be decoded*");
    }

    [Fact]
    public void BotCommitEmail_FollowsTheSlug()
    {
        using var svc = new GitHubAppTokenService(
            new GitHubAppConfig(AppId, Slug, NewKey().pem), new HttpClient(new StubHandler(Script)));

        svc.BotCommitEmail(987654).Should().Be("987654+si14-x[bot]@users.noreply.github.com");
    }

    private static byte[] Decode(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}
