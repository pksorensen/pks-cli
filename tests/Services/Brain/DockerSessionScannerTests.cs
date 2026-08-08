using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Brain.Asf;
using Xunit;

namespace PKS.CLI.Tests.Services.Brain;

/// The docker scanner's two pure seams: which volume names are worth mounting,
/// and how the in-container probe's output is read back. Everything else in the
/// scanner is `docker` process invocation, which is covered by the manual check in
/// `pks brain sources --docker` rather than by mocking a daemon.
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class DockerSessionScannerTests
{
    [Theory]
    [InlineData("claude-code-config-workspaces-repo", AsfSource.Claude)]
    [InlineData("CLAUDE-CODE-CONFIG-Upper", AsfSource.Claude)]
    [InlineData("myproject_dotclaude", AsfSource.Claude)]
    [InlineData("devcontainer-codex-home", AsfSource.Codex)]
    [InlineData("opencode-data", AsfSource.OpenCode)]
    public void ToolHintFor_recognises_agent_config_volumes(string volume, string expected)
    {
        DockerSessionScanner.ToolHintFor(volume).Should().Be(expected);
    }

    [Theory]
    [InlineData("postgres-data")]
    [InlineData("node_modules_cache")]
    [InlineData("")]
    public void ToolHintFor_ignores_everything_else(string volume)
    {
        DockerSessionScanner.ToolHintFor(volume).Should().BeNull();
    }

    [Fact]
    public void ParseProbeOutput_reads_one_file_per_line()
    {
        const string stdout = """
            claude-code-config-a|claude|-workspaces-repo|52428800|1754500000
            claude-code-config-b|claude|-workspaces-udbud-001|4096|1750000000
            """;

        var files = DockerSessionScanner.ParseProbeOutput(stdout).ToList();

        files.Should().HaveCount(2);
        files[0].VolumeName.Should().Be("claude-code-config-a");
        files[0].Tool.Should().Be(AsfSource.Claude);
        files[0].ProjectDir.Should().Be("-workspaces-repo");
        files[0].Bytes.Should().Be(52_428_800);
        files[0].Modified.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_754_500_000));
    }

    [Fact]
    public void ParseProbeOutput_skips_noise_and_unusable_rows()
    {
        // `docker run` writes image-pull chatter to stdout, and a file that vanished
        // between find and stat yields a row with no size or mtime.
        const string stdout = """
            Unable to find image 'alpine:3' locally
            vol-a|claude|-workspaces-repo||
            vol-b|claude|-workspaces-repo|not-a-number|1750000000
            vol-c|claude|-workspaces-repo|10|0
            vol-d|codex|2026-08-07|10|1750000000
            """;

        var files = DockerSessionScanner.ParseProbeOutput(stdout).ToList();

        files.Should().ContainSingle().Which.VolumeName.Should().Be("vol-d");
    }

    [Fact]
    public void ByProject_rolls_volumes_up_into_the_shape_worth_printing()
    {
        // The real distribution: a devcontainer mints one config volume per
        // container, so the same project shows up across many single-session
        // volumes. Per project is the finding; per volume is noise.
        var files = new[]
        {
            new DockerSessionFile("vol-a", AsfSource.Claude, "-workspaces-repo", 100, Epoch(1_740_000_000)),
            new DockerSessionFile("vol-a", AsfSource.Claude, "-workspaces-repo", 200, Epoch(1_754_000_000)),
            new DockerSessionFile("vol-b", AsfSource.Claude, "-workspaces-repo", 300, Epoch(1_750_000_000)),
            new DockerSessionFile("vol-c", AsfSource.Claude, "-workspaces-udbud-001", 50, Epoch(1_745_000_000)),
            new DockerSessionFile("vol-c", AsfSource.Codex, "-workspaces-udbud-001", 50, Epoch(1_745_000_000)),
        };

        var groups = DockerScan.ByProject(files);

        groups.Should().HaveCount(3);

        var biggest = groups[0];
        biggest.Tool.Should().Be(AsfSource.Claude);
        biggest.ProjectDir.Should().Be("-workspaces-repo");
        biggest.Sessions.Should().Be(3);
        biggest.Volumes.Should().Be(2);
        biggest.Bytes.Should().Be(600);
        biggest.Oldest.Should().Be(Epoch(1_740_000_000));
        biggest.Newest.Should().Be(Epoch(1_754_000_000));

        // Same project, two tools ⇒ two rows. The tool is what a later ingest has
        // to dispatch on, so it must never be flattened away.
        groups.Skip(1).Select(g => g.Tool).Should().BeEquivalentTo([AsfSource.Claude, AsfSource.Codex]);

        DockerScan.Volumes(files).Should().Equal("vol-a", "vol-b", "vol-c");
    }

    private static DateTimeOffset Epoch(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds);
}
