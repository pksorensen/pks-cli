using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// The launcher decides how a runner process is started on a machine. The regression it exists to
/// prevent is concrete: the SSH handoff hardcoded <c>dnx pks-cli</c>, and a box whose dotnet install
/// is broken (no host/fxr) but which has the self-contained <c>pks</c> binary in /usr/local/bin
/// could therefore never host a runner, with a failure that surfaces as a dead tmux session.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class RunnerLauncherTests
{
    private static SshProbeResult Probe(bool pks = false, bool dnx = false, bool npx = false) =>
        new(DockerAvailable: true, TmuxAvailable: true, TmuxVersion: "tmux 3.3a",
            DotnetAvailable: dnx, DotnetVersion: dnx ? "10.0.100" : null,
            DnxAvailable: dnx, PksAvailable: pks, NpxAvailable: npx);

    [Fact]
    public void ResolveRemote_prefers_the_installed_binary()
    {
        var launcher = RunnerLauncher.ResolveRemote(Probe(pks: true, dnx: true, npx: true));

        launcher!.Kind.Should().Be(RunnerLauncherKind.Pks);
        launcher.BuildCommandLine("agentics runner run").Should().Be("pks agentics runner run");
    }

    [Fact]
    public void ResolveRemote_falls_back_to_dnx_when_pks_is_not_installed()
    {
        var launcher = RunnerLauncher.ResolveRemote(Probe(dnx: true, npx: true));

        launcher!.Kind.Should().Be(RunnerLauncherKind.Dnx);
        launcher.BuildCommandLine("agentics runner run").Should().Be("dnx pks-cli -- agentics runner run");
    }

    [Fact]
    public void ResolveRemote_falls_back_to_npx_when_dotnet_is_unusable()
    {
        var launcher = RunnerLauncher.ResolveRemote(Probe(npx: true));

        launcher!.Kind.Should().Be(RunnerLauncherKind.Npx);
        // @latest is load-bearing: npx resolves a bare package name against ~/.npm/_npx and will
        // reuse a stale cached version indefinitely without consulting the registry.
        launcher.BuildCommandLine("agentics runner run")
            .Should().Be("npx -y @pks-cli/cli@latest agentics runner run");
    }

    [Fact]
    public void ResolveRemote_returns_null_when_the_target_cannot_run_pks_at_all()
    {
        RunnerLauncher.ResolveRemote(Probe()).Should().BeNull();
    }

    [Fact]
    public void A_target_with_only_the_pks_binary_is_still_ready()
    {
        // Before launcher resolution this probe reported "not ready" purely because dnx was absent,
        // even though the machine could run a runner perfectly well.
        Probe(pks: true).IsReady.Should().BeTrue();
        Probe().IsReady.Should().BeFalse();
    }

    [Fact]
    public void ResolveSelf_points_at_a_real_executable()
    {
        var launcher = RunnerLauncher.ResolveSelf();

        launcher.Prefix.Should().NotBeNullOrWhiteSpace();
        launcher.BuildCommandLine("agentics runner run").Should().EndWith("agentics runner run");
    }

    [Theory]
    [InlineData("/usr/local/bin/pks", "/usr/local/bin/pks")]
    [InlineData("/home/some one/.pks-cli/pks", "'/home/some one/.pks-cli/pks'")]
    [InlineData("/tmp/it's/pks", "'/tmp/it'\\''s/pks'")]
    public void Quote_only_quotes_what_a_posix_shell_would_mangle(string input, string expected)
    {
        RunnerLauncher.Quote(input).Should().Be(expected);
    }
}
