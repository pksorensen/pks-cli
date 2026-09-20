using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// The repository-identity test behind every Coolify app lookup.
///
/// Regression origin: on 2026-09-20 a release of pksorensen/commuteconnects deployed
/// pksorensen/commuteconnects-landing to production, because the match was `Contains` and the
/// first repository's slug is a prefix of the second's. Nothing failed — the smoke test found the
/// intended app healthy, because it had simply never been touched.
/// </summary>
public class CoolifyRepoMatchTests
{
    [Theory]
    [InlineData("pksorensen/commuteconnects")]
    [InlineData("PKSorensen/CommuteConnects")]
    [InlineData("https://github.com/pksorensen/commuteconnects")]
    [InlineData("https://github.com/pksorensen/commuteconnects.git")]
    [InlineData("git@github.com:pksorensen/commuteconnects.git")]
    [InlineData("https://github.com/pksorensen/commuteconnects/")]
    public void Matches_EveryFormCoolifyStores(string gitRepository)
    {
        CoolifyLookupService.RepoMatches(gitRepository, "pksorensen/commuteconnects").Should().BeTrue();
    }

    [Theory]
    [InlineData("pksorensen/commuteconnects-landing")]
    [InlineData("https://github.com/pksorensen/commuteconnects-landing.git")]
    [InlineData("git@github.com:pksorensen/commuteconnects-landing.git")]
    public void DoesNotMatch_ARepositoryThatMerelyStartsWithIt(string gitRepository)
    {
        CoolifyLookupService.RepoMatches(gitRepository, "pksorensen/commuteconnects").Should().BeFalse();
    }

    [Fact]
    public void DoesNotMatch_SameRepoNameUnderADifferentOwner()
    {
        // KjeldagerIO/commuteconnects is a real, separate application on the same Coolify host.
        CoolifyLookupService.RepoMatches("KjeldagerIO/commuteconnects", "pksorensen/commuteconnects")
            .Should().BeFalse();
    }

    [Fact]
    public void DoesNotMatch_WhenTheOwnerIsOnlyASuffix()
    {
        // "notpksorensen/commuteconnects" ends with "sorensen/commuteconnects" but not with
        // "/pksorensen/commuteconnects" — the anchoring slash is what makes the suffix arm exact.
        CoolifyLookupService.RepoMatches("notpksorensen/commuteconnects", "pksorensen/commuteconnects")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("", "pksorensen/commuteconnects")]
    [InlineData("   ", "pksorensen/commuteconnects")]
    [InlineData("pksorensen/commuteconnects", "")]
    public void DoesNotMatch_Blanks(string gitRepository, string fullRepo)
    {
        CoolifyLookupService.RepoMatches(gitRepository, fullRepo).Should().BeFalse();
    }
}
