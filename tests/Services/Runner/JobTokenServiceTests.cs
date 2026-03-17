using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for JobTokenService — HMAC-SHA256 JWT creation and validation.
/// </summary>
public class JobTokenServiceTests
{
    private static JobTokenService CreateService(TimeSpan? ttl = null)
        => new JobTokenService(ttl);

    [Fact]
    public void CreateToken_ReturnsNonEmptyString()
    {
        var sut = CreateService();
        var token = sut.CreateToken("owner", "repo", "main", "production", "app-uuid-1", "job-42");
        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ValidateToken_Roundtrip_ReturnsOriginalClaims()
    {
        var sut = CreateService();
        var token = sut.CreateToken("myowner", "myrepo", "develop", "staging", "uuid-abc", "job-99");

        var claims = sut.ValidateToken(token);

        claims.Should().NotBeNull();
        claims!.Owner.Should().Be("myowner");
        claims.Repo.Should().Be("myrepo");
        claims.Branch.Should().Be("develop");
        claims.Environment.Should().Be("staging");
        claims.AppUuid.Should().Be("uuid-abc");
        claims.JobId.Should().Be("job-99");
        claims.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task ValidateToken_ExpiredToken_ReturnsNull()
    {
        var sut = CreateService(ttl: TimeSpan.FromMilliseconds(1));
        var token = sut.CreateToken("owner", "repo", "main", "production", "uuid-1", "job-1");

        await Task.Delay(50);
        var claims = sut.ValidateToken(token);

        claims.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_TamperedToken_ReturnsNull()
    {
        var sut = CreateService();
        var token = sut.CreateToken("owner", "repo", "main", "production", "uuid-1", "job-1");
        var tampered = token[..^5] + "XXXXX";

        var claims = sut.ValidateToken(tampered);

        claims.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_WrongKey_ReturnsNull()
    {
        // Two separate instances each generate their own random key
        var creator = CreateService();
        var validator = CreateService();
        var token = creator.CreateToken("owner", "repo", "main", "production", "uuid-1", "job-1");

        var claims = validator.ValidateToken(token);

        claims.Should().BeNull();
    }

    [Fact]
    public void ValidateToken_InvalidFormat_ReturnsNull()
    {
        var sut = CreateService();

        var claims = sut.ValidateToken("this-is-not-a-valid-token");

        claims.Should().BeNull();
    }

    [Fact]
    public void CreateToken_DifferentJobIds_ProduceDifferentTokens()
    {
        var sut = CreateService();

        var token1 = sut.CreateToken("owner", "repo", "main", "production", "uuid-1", "job-1");
        var token2 = sut.CreateToken("owner", "repo", "main", "production", "uuid-1", "job-2");

        token1.Should().NotBe(token2);
    }
}
