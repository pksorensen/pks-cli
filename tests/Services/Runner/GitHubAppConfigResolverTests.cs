using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for GitHubAppConfigResolver.
///
/// The behaviour worth guarding is the asymmetry: no App declared is fine and returns null, but
/// an App declared without a usable key is a hard failure. Silently falling back to the
/// operator's own token there would attribute the App's commits to a person.
/// </summary>
public class GitHubAppConfigResolverTests
{
    private static Func<string, string?> Env(params (string Key, string? Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void NoAppDeclared_IsNotAnError()
    {
        GitHubAppConfigResolver.Resolve(Env()).Should().BeNull();
    }

    [Fact]
    public void AnAppDeclaredWithNoKey_FailsLoudlyRatherThanFallingBack()
    {
        var act = () => GitHubAppConfigResolver.Resolve(Env(
            ("GITHUB_APP_ID", "123456"),
            ("GITHUB_APP_SLUG", "si14-x")));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no private key was provided*")
            .WithMessage("*GITHUB_APP_PRIVATE_KEY_FILE*");
    }

    [Fact]
    public void AnAppDeclaredWithNoSlug_FailsBecauseTheSlugCannotBeDerived()
    {
        var act = () => GitHubAppConfigResolver.Resolve(Env(
            ("GITHUB_APP_ID", "123456"),
            ("GITHUB_APP_PRIVATE_KEY", "-----BEGIN RSA PRIVATE KEY-----\nx\n-----END RSA PRIVATE KEY-----")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*GITHUB_APP_SLUG*");
    }

    [Fact]
    public void AnInlineKeyIsUsedAsIs()
    {
        var pem = "-----BEGIN RSA PRIVATE KEY-----\nabc\n-----END RSA PRIVATE KEY-----";

        var config = GitHubAppConfigResolver.Resolve(Env(
            ("GITHUB_APP_ID", "123456"),
            ("GITHUB_APP_SLUG", "si14-x"),
            ("GITHUB_APP_PRIVATE_KEY", pem)));

        config.Should().NotBeNull();
        config!.AppId.Should().Be("123456");
        config.Slug.Should().Be("si14-x");
        config.PrivateKeyPem.Should().Be(pem);
    }

    [Fact]
    public void EscapedNewlinesAreUnescaped_BecauseThatIsHowAPemSurvivesAnEnvFile()
    {
        var config = GitHubAppConfigResolver.Resolve(Env(
            ("GITHUB_APP_ID", "123456"),
            ("GITHUB_APP_SLUG", "si14-x"),
            ("GITHUB_APP_PRIVATE_KEY", "-----BEGIN RSA PRIVATE KEY-----\\nabc\\n-----END RSA PRIVATE KEY-----")));

        config!.PrivateKeyPem.Should().Be("-----BEGIN RSA PRIVATE KEY-----\nabc\n-----END RSA PRIVATE KEY-----");
    }

    [Fact]
    public void AKeyFileIsRead_AndWinsOverAnInlineKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pks-app-key-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, "from-the-file");
        try
        {
            var config = GitHubAppConfigResolver.Resolve(Env(
                ("GITHUB_APP_ID", "123456"),
                ("GITHUB_APP_SLUG", "si14-x"),
                ("GITHUB_APP_PRIVATE_KEY_FILE", path),
                ("GITHUB_APP_PRIVATE_KEY", "inline-should-lose")));

            config!.PrivateKeyPem.Should().Be("from-the-file");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnUnreadableKeyFileSaysWhichPathFailed()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"pks-absent-{Guid.NewGuid():N}.pem");

        var act = () => GitHubAppConfigResolver.Resolve(Env(
            ("GITHUB_APP_ID", "123456"),
            ("GITHUB_APP_SLUG", "si14-x"),
            ("GITHUB_APP_PRIVATE_KEY_FILE", missing)));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{missing}*");
    }
}
