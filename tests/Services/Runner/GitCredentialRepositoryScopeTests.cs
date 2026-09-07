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

    [Fact]
    public void The_helper_sends_no_authorization_header_when_there_is_nothing_to_send()
    {
        // An empty "Bearer " would be read as a rejected token rather than an absent one, and
        // would poison exactly the count the observation phase exists to produce.
        var script = GitCredentialHelperScript.CredentialHelperFor(null);

        script.Should().Contain("TOKEN=\"${PKS_TOKEN:-}\"", "the env is still consulted");
        script.Should().Contain("[ -n \"$TOKEN\" ] && set -- -H", "an empty token adds no argument");
    }

    [Fact]
    public void The_helper_prefers_the_environment_over_the_baked_token()
    {
        // That is what lets the GitHub Actions path — which already passes -e PKS_TOKEN on
        // docker run — carry the header without minting anything a second time.
        var token = new JobTokenService().CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-42");

        var script = GitCredentialHelperScript.CredentialHelperFor(token);

        script.Should().Contain($"TOKEN=\"${{PKS_TOKEN:-{token}}}\"");
        script.Should().Contain("curl -s \"$@\"", "set -- carries the header as separate argv entries");
    }

    [Fact]
    public void Askpass_carries_the_bearer_too_even_though_it_cannot_name_a_repository()
    {
        // Without this, every askpass fetch would log as unauthenticated and the observation
        // phase would read as "the header is not deployed" when it is.
        var token = new JobTokenService().CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-42");

        GitCredentialHelperScript.AskpassFor(token).Should().Contain(token);
    }

    [Fact]
    public void A_token_that_is_not_ours_is_refused_rather_than_baked_into_a_shell_script()
    {
        var bake = () => GitCredentialHelperScript.CredentialHelperFor("\"; rm -rf / #");

        bake.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Runs the generated script against a stub curl that prints its own argv. Everything else
    /// here asserts on the script's text, which cannot tell the difference between a header that
    /// arrives as one argument and one that arrives split across four — and a split header reads
    /// to the server as an invalid token, not as an absent one.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Speed", "Fast")]
    public void The_generated_helper_passes_the_header_as_one_argument()
    {
        if (OperatingSystem.IsWindows())
            return;

        var token = new JobTokenService().CreateToken("pksorensen", "commuteconnects", "main", "", "", "job-42");
        var dir = Directory.CreateTempSubdirectory("pks-helper-argv").FullName;
        try
        {
            // A curl that records its arguments instead of making a request, one per line. It
            // writes to a file rather than stdout because the helper captures curl's stdout into
            // a command substitution and parses it as the credential response.
            var argv = Path.Combine(dir, "argv.txt");
            var curl = Path.Combine(dir, "curl");
            File.WriteAllText(curl, $"#!/bin/sh\nfor a in \"$@\"; do echo \"ARG=$a\" >> {argv}; done\n");
            Run("/bin/chmod", $"+x {curl}", dir, null);

            var helper = Path.Combine(dir, "helper.sh");
            File.WriteAllText(helper, GitCredentialHelperScript.CredentialHelperFor(token));
            Run("/bin/chmod", $"+x {helper}", dir, null);

            Run("/bin/sh", helper, dir, "protocol=https\nhost=github.com\npath=pksorensen/commuteconnects.git\n\n");

            var recorded = File.ReadAllText(argv);
            recorded.Should().Contain("ARG=-H");
            recorded.Should().Contain($"ARG=Authorization: Bearer {token}",
                "the header must survive as a single argv entry, quotes and space intact");
            recorded.Should().Contain("ARG=http://localhost/git-credential?host=github.com&repo=pksorensen/commuteconnects.git",
                "and the repository git named must still reach the server");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string Run(string fileName, string arguments, string workingDir, string? stdin)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false
        };
        // So the stub curl is the one the script finds.
        psi.Environment["PATH"] = $"{workingDir}:{psi.Environment["PATH"]}";
        psi.Environment.Remove("PKS_TOKEN");

        using var process = System.Diagnostics.Process.Start(psi)!;
        if (stdin != null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);

        return output;
    }
}
