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

    [Fact]
    public void A_token_that_names_no_entitlement_is_entitled_to_its_own_repository()
    {
        // The GitHub Actions path passes six arguments and always will; its job is scoped to the
        // registration's repo and nothing else, so the list it never sets has to mean that.
        var sut = CreateService();

        var claims = sut.ValidateToken(sut.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-1"));

        claims!.Repos.Should().Equal("pksorensen/commuteconnects");
        claims.IsEntitledTo("pksorensen", "commuteconnects").Should().BeTrue();
        claims.IsEntitledTo("pksorensen", "commuteconnects-landing").Should().BeFalse();
    }

    [Fact]
    public void An_entitlement_list_survives_the_round_trip()
    {
        var sut = CreateService();
        var token = sut.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-1",
            ["pksorensen/commuteconnects", "pksorensen/commuteconnects-landing"]);

        var claims = sut.ValidateToken(token);

        claims!.Repos.Should().Equal("pksorensen/commuteconnects", "pksorensen/commuteconnects-landing");
        claims.IsEntitledTo("pksorensen", "commuteconnects-landing").Should().BeTrue();
    }

    [Fact]
    public void An_empty_entitlement_list_means_nothing_not_everything()
    {
        // The ALP path passes an empty list when it cannot work out which repositories the job
        // legitimately needs — a self-hosted clone URL, which does not parse as owner/name. If
        // that collapsed back to the default, a Gitea-backed project called
        // "pksorensen/commuteconnects" would be logged as entitled to the *GitHub* repository of
        // the same name, and Phase B would later hand it a token on the strength of it.
        var sut = CreateService();

        var claims = sut.ValidateToken(
            sut.CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-1", []));

        claims!.Repos.Should().BeEmpty();
        claims.IsEntitledTo("pksorensen", "commuteconnects").Should().BeFalse();
    }

    [Fact]
    public void Entitlement_is_case_insensitive_the_way_GitHub_is()
    {
        // A clone URL's casing is whatever the person who wrote it typed, and GitHub does not
        // care. Refusing on case would be a false mismatch in the observation log and, later, a
        // false 403.
        var sut = CreateService();

        var claims = sut.ValidateToken(sut.CreateToken("PKSorensen", "CommuteConnects", "main", "", "", "job-1"));

        claims!.IsEntitledTo("pksorensen", "commuteconnects").Should().BeTrue();
    }
}
