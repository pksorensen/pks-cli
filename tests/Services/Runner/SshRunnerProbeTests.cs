using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for <see cref="SshRunnerProbe"/>'s pure output parser
/// (docs/remote-runner-targets-plan.md Phase 4, work item 3). <c>ParseProbeOutput</c> is
/// <c>internal</c> and reachable here via the <c>InternalsVisibleTo</c> in <c>src/pks-cli.csproj</c>.
/// </summary>
public class SshRunnerProbeTests
{
    private const string AllPresentOutput =
        "PKS_PROBE_DOCKER=ok\n" +
        "PKS_PROBE_TMUX=tmux 3.4\n" +
        "PKS_PROBE_DOTNET=10.0.100\n" +
        "PKS_PROBE_DNX=ok\n";

    private const string AllMissingOutput =
        "PKS_PROBE_DOCKER=MISSING\n" +
        "PKS_PROBE_TMUX=MISSING\n" +
        "PKS_PROBE_DOTNET=MISSING\n" +
        "PKS_PROBE_DNX=MISSING\n";

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseProbeOutput_AllToolsPresent_ReportsReadyWithVersions()
    {
        var result = SshRunnerProbe.ParseProbeOutput(AllPresentOutput);

        result.DockerAvailable.Should().BeTrue();
        result.TmuxAvailable.Should().BeTrue();
        result.TmuxVersion.Should().Be("tmux 3.4");
        result.DotnetAvailable.Should().BeTrue();
        result.DotnetVersion.Should().Be("10.0.100");
        result.DnxAvailable.Should().BeTrue();
        result.IsReady.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseProbeOutput_AllToolsMissing_ReportsNotReady_WithNullVersions()
    {
        var result = SshRunnerProbe.ParseProbeOutput(AllMissingOutput);

        result.DockerAvailable.Should().BeFalse();
        result.TmuxAvailable.Should().BeFalse();
        result.TmuxVersion.Should().BeNull();
        result.DotnetAvailable.Should().BeFalse();
        result.DotnetVersion.Should().BeNull();
        result.DnxAvailable.Should().BeFalse();
        result.IsReady.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseProbeOutput_DockerMissing_ButTmuxAndDnxPresent_IsNotReady()
    {
        // IsReady requires Docker AND Tmux AND Dnx -- Dotnet is informational only.
        var stdout =
            "PKS_PROBE_DOCKER=MISSING\n" +
            "PKS_PROBE_TMUX=tmux 3.4\n" +
            "PKS_PROBE_DOTNET=MISSING\n" +
            "PKS_PROBE_DNX=ok\n";

        var result = SshRunnerProbe.ParseProbeOutput(stdout);

        result.DockerAvailable.Should().BeFalse();
        result.TmuxAvailable.Should().BeTrue();
        result.DnxAvailable.Should().BeTrue();
        result.DotnetAvailable.Should().BeFalse();
        result.IsReady.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseProbeOutput_DotnetMissingButOthersPresent_StillReady()
    {
        // Dotnet's own version is informational only per SshProbeResult.IsReady's doc comment.
        var stdout =
            "PKS_PROBE_DOCKER=ok\n" +
            "PKS_PROBE_TMUX=tmux 3.4\n" +
            "PKS_PROBE_DOTNET=MISSING\n" +
            "PKS_PROBE_DNX=ok\n";

        var result = SshRunnerProbe.ParseProbeOutput(stdout);

        result.DotnetAvailable.Should().BeFalse();
        result.IsReady.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseProbeOutput_EmptyStdout_ReportsAllUnavailable_NoThrow()
    {
        var result = SshRunnerProbe.ParseProbeOutput(string.Empty);

        result.DockerAvailable.Should().BeFalse();
        result.TmuxAvailable.Should().BeFalse();
        result.DotnetAvailable.Should().BeFalse();
        result.DnxAvailable.Should().BeFalse();
        result.IsReady.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseProbeOutput_MarkersOutOfOrder_WithCrlfLineEndings_StillParses()
    {
        var stdout = "PKS_PROBE_DNX=ok\r\nPKS_PROBE_DOCKER=ok\r\nPKS_PROBE_TMUX=tmux 3.4\r\nPKS_PROBE_DOTNET=10.0.100\r\n";

        var result = SshRunnerProbe.ParseProbeOutput(stdout);

        result.IsReady.Should().BeTrue();
        result.TmuxVersion.Should().Be("tmux 3.4");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildProbeCommand_NeverEmitsDoubleQuotes()
    {
        // ExecuteProcessAsync naively wraps any space-containing argument in an unescaped outer
        // "..." pair -- an embedded double quote in the remote command would corrupt that wrapping.
        var command = SshRunnerProbe.BuildProbeCommand();

        command.Should().NotContain("\"");
    }
}
