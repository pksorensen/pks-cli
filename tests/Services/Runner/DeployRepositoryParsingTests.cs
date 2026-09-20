using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// `owner/repo@branch` entries in RunnerRegistration.DeployRepositories — the list that makes a
/// coordination repository deployable when its Coolify app is built from a submodule's repository.
/// </summary>
public class DeployRepositoryParsingTests
{
    [Fact]
    public void BareSlug_HasNoBranch_SoTheJobsBranchIsUsed()
    {
        var (slug, branch) = RunnerContainerService.ParseDeployRepository("KjeldagerIO/commuteconnects-carshare-www");

        slug.Should().Be("KjeldagerIO/commuteconnects-carshare-www");
        branch.Should().BeNull();
    }

    [Fact]
    public void PinnedBranch_IsSplitOff()
    {
        var (slug, branch) = RunnerContainerService.ParseDeployRepository("KjeldagerIO/commuteconnects-carshare-www@main");

        slug.Should().Be("KjeldagerIO/commuteconnects-carshare-www");
        branch.Should().Be("main");
    }

    [Fact]
    public void Whitespace_IsTrimmedFromBothHalves()
    {
        var (slug, branch) = RunnerContainerService.ParseDeployRepository("  owner/repo @ release  ");

        slug.Should().Be("owner/repo");
        branch.Should().Be("release");
    }

    [Fact]
    public void LastAtWins_SoABranchContainingNoAtIsSafe()
    {
        // Defensive rather than expected: no GitHub slug contains '@', but taking the last one
        // means a stray '@' in an owner name cannot swallow the branch.
        var (slug, branch) = RunnerContainerService.ParseDeployRepository("own@er/repo@main");

        slug.Should().Be("own@er/repo");
        branch.Should().Be("main");
    }
}
