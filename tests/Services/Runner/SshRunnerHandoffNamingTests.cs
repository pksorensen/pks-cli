using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for the SSH handoff name-collision refusal
/// (docs/remote-runner-targets-plan.md Phase 4, work item 4). The server's
/// <c>POST .../runners</c> endpoint upserts by name and rotates the token in place, so a collision
/// must be a hard refusal.
/// </summary>
public class SshRunnerHandoffNamingTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void IsCollision_CandidateEqualsLocalHostName_ReturnsTrue()
    {
        var result = SshRunnerHandoffNaming.IsCollision("laptop-01", "laptop-01", Array.Empty<string>());

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void IsCollision_CandidateEqualsLocalHostName_CaseInsensitive_ReturnsTrue()
    {
        var result = SshRunnerHandoffNaming.IsCollision("LAPTOP-01", "laptop-01", Array.Empty<string>());

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void IsCollision_CandidateMatchesExistingServerRunnerName_ReturnsTrue()
    {
        var result = SshRunnerHandoffNaming.IsCollision(
            "gpu-box",
            "laptop-01",
            new[] { "ci-runner", "gpu-box", "other-runner" });

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void IsCollision_CandidateMatchesExistingServerRunnerName_CaseInsensitive_ReturnsTrue()
    {
        var result = SshRunnerHandoffNaming.IsCollision(
            "GPU-Box",
            "laptop-01",
            new[] { "gpu-box" });

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void IsCollision_NoMatch_ReturnsFalse()
    {
        var result = SshRunnerHandoffNaming.IsCollision(
            "gpu-box-remote",
            "laptop-01",
            new[] { "ci-runner", "other-runner" });

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void IsCollision_EmptyExistingNames_NoLocalHostMatch_ReturnsFalse()
    {
        var result = SshRunnerHandoffNaming.IsCollision("gpu-box-remote", "laptop-01", Array.Empty<string>());

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void IsCollision_NullCandidateName_Throws()
    {
        var act = () => SshRunnerHandoffNaming.IsCollision(null!, "laptop-01", Array.Empty<string>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void IsCollision_NullExistingServerRunnerNames_Throws()
    {
        var act = () => SshRunnerHandoffNaming.IsCollision("gpu-box-remote", "laptop-01", null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
