using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PKS.Infrastructure.Services.Agentics;
using Xunit;

namespace PKS.CLI.Tests.Services.Agentics;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AgentIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pks-agent-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    [Fact]
    public void Jwt_is_es256_signed_by_the_key_and_names_the_thumbprint()
    {
        var id = AgentIdentity.Create("agentics.dk");
        var jwt = id.MintJwt(id.Audience, DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var parts = jwt.Split('.');
        parts.Should().HaveCount(3);

        var header = JsonDocument.Parse(FromB64Url(parts[0])).RootElement;
        header.GetProperty("alg").GetString().Should().Be("ES256");
        var claims = JsonDocument.Parse(FromB64Url(parts[1])).RootElement;
        claims.GetProperty("iss").GetString().Should().Be("agentics-agent");
        claims.GetProperty("sub").GetString().Should().Be(id.Id);
        claims.GetProperty("aud").GetString().Should().Be("agentics.dk");
        (claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64()).Should().Be(300);

        var jwk = id.PublicJwk();
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = FromB64Url(jwk["x"]), Y = FromB64Url(jwk["y"]) },
        });
        verifier.VerifyData(Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"), FromB64Url(parts[2]), HashAlgorithmName.SHA256)
            .Should().BeTrue();
        FromB64Url(parts[2]).Should().HaveCount(64, "JWS wants r||s, not DER");
    }

    [Fact]
    public void Thumbprint_follows_rfc7638_member_order()
    {
        var jwk = new Dictionary<string, string> { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = "AAA", ["y"] = "BBB" };
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"AAA\",\"y\":\"BBB\"}")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        AgentIdentity.Thumbprint(jwk).Should().Be(expected);
    }

    [Fact]
    public void Save_and_load_round_trip_per_server()
    {
        var id = AgentIdentity.Create("https://agentics.dk/");
        id.Name = "Codex på Jakobs PC";
        id.OnBehalfOf = "jakob@example.test";
        var path = id.Save(_dir);
        path.Should().EndWith("agentics.dk.json");
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var loaded = AgentIdentity.Load("agentics.dk", _dir)!;
        loaded.Id.Should().Be(id.Id);
        loaded.Name.Should().Be("Codex på Jakobs PC");
        loaded.OnBehalfOf.Should().Be("jakob@example.test");
        AgentIdentity.Load("other.test", _dir).Should().BeNull();
        AgentIdentity.LoadAll(_dir).Should().ContainSingle();

        File.ReadAllText(path).Should().Contain("PRIVATE KEY").And.NotContain("\"d\"");
    }

    [Theory]
    [InlineData("agentics.dk", "https://agentics.dk")]
    [InlineData("localhost:3000", "http://localhost:3000")]
    [InlineData("https://x.test/some/path", "https://x.test")]
    public void Normalizes_servers(string input, string expected) =>
        AgentIdentity.NormalizeServer(input).Should().Be(expected);

    [Fact]
    public void Detects_the_harness_from_the_environment()
    {
        AgentClientInfo.DetectHarness(k => k == "CODEX_SANDBOX" ? "seatbelt" : null).Should().Be("codex");
        AgentClientInfo.DetectHarness(k => k == "CLAUDECODE" ? "1" : null).Should().Be("claude-code");
        AgentClientInfo.DetectHarness(_ => null).Should().BeNull();
    }
}
