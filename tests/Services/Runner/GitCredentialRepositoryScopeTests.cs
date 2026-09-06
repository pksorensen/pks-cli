using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Tests for the repository scoping that makes GitHub App mode possible: parsing the path git
/// hands the credential helper, and the helper script's obligation to forward it.
/// </summary>
public class GitCredentialRepositoryScopeTests
{
    [Theory]
    [InlineData("pksorensen/commuteconnects", "pksorensen", "commuteconnects")]
    [InlineData("pksorensen/commuteconnects.git", "pksorensen", "commuteconnects")]
    [InlineData("/pksorensen/commuteconnects.git", "pksorensen", "commuteconnects")]
    [InlineData("pksorensen/commuteconnects-landing.GIT", "pksorensen", "commuteconnects-landing")]
    public void ARepositoryPathIsParsed(string input, string owner, string repo)
    {
        var parsed = GitCredentialServer.ParseRepository(input);

        parsed.Should().NotBeNull();
        parsed!.Item1.Should().Be(owner);
        parsed.Item2.Should().Be(repo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pksorensen")]                       // owner alone names no repository
    [InlineData("pksorensen/commute/connects")]      // deeper than owner/name
    public void AnythingThatIsNotOwnerSlashNameIsRefusedRatherThanGuessed(string? input)
    {
        // Guessing here would mint a token for the wrong repository, which is worse than failing.
        GitCredentialServer.ParseRepository(input).Should().BeNull();
    }

    [Fact]
    public void TheCredentialHelperForwardsTheRepositoryGitAsksAbout()
    {
        var script = GitCredentialHelperScript.CredentialHelper;

        // The old helper read stdin only to discard it, so every repo on github.com looked alike.
        script.Should().Contain("path=*)", "git's path is what names the repository");
        script.Should().Contain("host=*)");
        script.Should().Contain("repo=$REPO", "the server scopes the token by this");
        script.Should().Contain("host=$HOST");
    }

    [Fact]
    public void TheCredentialHelperFallsBackToTheUsernameAnAppTokenNeeds()
    {
        GitCredentialHelperScript.CredentialHelper
            .Should().Contain("USER=x-access-token");
    }

    [Fact]
    public void GitIsToldToSendThePath()
    {
        // Without credential.useHttpPath git sends only the host, and the path branch above
        // never fires.
        GitCredentialHelperScript.UseHttpPathConfigArgs
            .Should().Be("config --global credential.useHttpPath true");
    }

    [Fact]
    public void BothScriptsTalkToTheSocketTheContainerMounts()
    {
        GitCredentialHelperScript.CredentialHelper.Should().Contain(GitCredentialHelperScript.SocketPath);
        GitCredentialHelperScript.Askpass.Should().Contain(GitCredentialHelperScript.SocketPath);
        GitCredentialHelperScript.SocketPath.Should().Be("/var/run/pks-creds/creds.sock");
    }

    [Fact]
    public void EncodeRoundTrips()
    {
        var decoded = System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(GitCredentialHelperScript.Encode(GitCredentialHelperScript.Askpass)));

        decoded.Should().Be(GitCredentialHelperScript.Askpass);
    }
}
