using FluentAssertions;
using PKS.Infrastructure.Services;
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

    // --- origin scrubbing ---------------------------------------------------
    //
    // A GitHub App installation token lives one hour. git skips the credential helper entirely
    // while the remote URL still carries a password, so an authenticated `origin` left in
    // .git/config would pin a job to a token that dies mid-run. These guard the reset.

    [Theory]
    [InlineData("https://x-access-token:ghs_secret@github.com/o/r.git", "https://github.com/o/r.git")]
    [InlineData("http://x-access-token:ghs_secret@host:3000/o/r.git", "http://host:3000/o/r.git")]
    [InlineData("https://user:pw@github.com/o/r", "https://github.com/o/r")]
    public void ACredentialIsRemovedFromTheOriginUrl(string input, string expected)
    {
        GitCloneUrl.WithoutCredentials(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://github.com/o/r.git")]
    [InlineData("https://github.com/o/r")]
    public void AUrlWithoutCredentialsIsUntouched(string input)
    {
        GitCloneUrl.WithoutCredentials(input).Should().Be(input);
    }

    [Fact]
    public void AnAtInThePathIsNotMistakenForCredentials()
    {
        // Only userinfo — before the first slash — is a credential. Stripping greedily here would
        // corrupt the URL rather than clean it.
        const string url = "https://github.com/o/r@v1.git";

        GitCloneUrl.WithoutCredentials(url).Should().Be(url);
    }

    [Fact]
    public void TheCloneRunsTheScrubOnBothSuccessPaths()
    {
        // The empty-repository fallback is a second, easily-forgotten success path: a job that
        // starts from an empty repo pushes for the first time at the END of its run, which is
        // exactly when an unscrubbed hour-old token has expired.
        var script = ReadCloneScript();

        script.Should().Contain("remote set-url origin");
        System.Text.RegularExpressions.Regex.Matches(script, @"^\s*scrub$",
            System.Text.RegularExpressions.RegexOptions.Multiline)
            .Count.Should().Be(2, "both the normal clone and the empty-repository fallback must reset origin");
    }

    private static string ReadCloneScript()
    {
        var field = typeof(DevcontainerSpawnerService).GetField(
            "CloneScript",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field.Should().NotBeNull("CloneScript is the contract under test");
        return (string)field!.GetRawConstantValue()!;
    }
}
