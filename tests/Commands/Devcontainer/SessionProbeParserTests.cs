using FluentAssertions;
using PKS.Infrastructure.Services.Devcontainer;
using Xunit;

namespace PKS.CLI.Tests.Commands.Devcontainer;

/// <summary>
/// Pure parser tests for the line protocol the in-container probe script emits
/// (<c>SESSION\t&lt;alive&gt;\t&lt;json&gt;</c> and <c>TMUX\t&lt;name&gt;\t&lt;windows&gt;\t&lt;created&gt;\t&lt;attached&gt;</c>),
/// and for the host-level BEGIN/END framing that groups one chunk per container.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class SessionProbeParserTests
{
    private const string AliveSession =
        "SESSION\t1\t{\"pid\":2456920,\"sessionId\":\"49c69828-1145-4c75-bfef-79ba7ddd\",\"cwd\":\"/workspaces/agentic-live-www\",\"startedAt\":1792616282506,\"procStart\":\"454578101\",\"version\":\"2.1.306\",\"kind\":\"interactive\",\"entrypoint\":\"cli\",\"name\":\"heypoul-meeting-mode-notch\",\"nameSource\":\"auto\",\"status\":\"busy\",\"updatedAt\":1793181518735,\"statusUpdatedAt\":1793181518735}";

    private const string DeadSession =
        "SESSION\t0\t{\"pid\":1071828,\"sessionId\":\"dead-dead\",\"cwd\":\"/workspaces/x\",\"startedAt\":1790000000000,\"name\":\"old\",\"status\":\"idle\"}";

    [Fact]
    public void Parse_AliveSession_IsReportedWithAllFields()
    {
        var result = SessionProbeParser.Parse(AliveSession + "\n");

        result.Sessions.Should().HaveCount(1);
        var s = result.Sessions[0];
        s.Source.Should().Be("claude");
        s.SessionId.Should().Be("49c69828-1145-4c75-bfef-79ba7ddd");
        s.Pid.Should().Be(2456920);
        s.Name.Should().Be("heypoul-meeting-mode-notch");
        s.Status.Should().Be("busy");
        s.Kind.Should().Be("interactive");
        s.Cwd.Should().Be("/workspaces/agentic-live-www");
        s.StartedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1792616282506));
        s.UpdatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1793181518735));
        result.StaleSessionFiles.Should().Be(0);
    }

    [Fact]
    public void Parse_DeadSession_IsCountedAsStaleAndNotListed()
    {
        var result = SessionProbeParser.Parse(AliveSession + "\n" + DeadSession + "\n" + DeadSession + "\n");

        result.Sessions.Should().HaveCount(1);
        result.Sessions[0].Pid.Should().Be(2456920);
        result.StaleSessionFiles.Should().Be(2);
    }

    [Fact]
    public void Parse_TmuxLines_AreParsed()
    {
        var stdout = "TMUX\tcc-plan-sync\t1\t1792568235\t0\nTMUX\tmargin-local\t3\t1788934380\t2\n";

        var result = SessionProbeParser.Parse(stdout);

        result.Tmux.Should().HaveCount(2);
        result.Tmux[0].Name.Should().Be("cc-plan-sync");
        result.Tmux[0].Windows.Should().Be(1);
        result.Tmux[0].Attached.Should().BeFalse();
        result.Tmux[0].CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1792568235));
        result.Tmux[1].Name.Should().Be("margin-local");
        result.Tmux[1].Windows.Should().Be(3);
        result.Tmux[1].Attached.Should().BeTrue("tmux reports the number of attached clients, anything above zero is attached");
    }

    [Fact]
    public void Parse_GarbageLines_AreIgnored()
    {
        var stdout = "ls: cannot access '/root/.claude/sessions/*.json': No such file or directory\n"
                     + "\n"
                     + "SESSION\tnot-a-number\t{\"pid\":1}\n"
                     + "SESSION\t1\tthis is not json\n"
                     + "TMUX\tonly-name\n"
                     + "RANDOM\tline\n"
                     + AliveSession + "\n"
                     + "TMUX\tok\t1\t1792568235\t0\n";

        var result = SessionProbeParser.Parse(stdout);

        result.Sessions.Should().HaveCount(1);
        result.Tmux.Should().HaveCount(1);
        result.Tmux[0].Name.Should().Be("ok");
    }

    [Fact]
    public void Parse_MissingFields_AreTolerated()
    {
        var stdout = "SESSION\t1\t{\"pid\":42}\nTMUX\tbare\t\t\t\n";

        var result = SessionProbeParser.Parse(stdout);

        result.Sessions.Should().HaveCount(1);
        var s = result.Sessions[0];
        s.Pid.Should().Be(42);
        s.SessionId.Should().BeNull();
        s.Name.Should().BeNull();
        s.Status.Should().BeNull();
        s.StartedAt.Should().BeNull();
        s.UpdatedAt.Should().BeNull();

        result.Tmux.Should().HaveCount(1);
        result.Tmux[0].Name.Should().Be("bare");
        result.Tmux[0].Windows.Should().Be(0);
        result.Tmux[0].CreatedAt.Should().BeNull();
        result.Tmux[0].Attached.Should().BeFalse();
    }

    [Fact]
    public void Parse_EmptyOutput_IsEmptyResult()
    {
        var result = SessionProbeParser.Parse("");

        result.Sessions.Should().BeEmpty();
        result.Tmux.Should().BeEmpty();
        result.StaleSessionFiles.Should().Be(0);
        result.ProbeError.Should().BeNull();
    }

    [Fact]
    public void Parse_WindowsLineEndings_AreTolerated()
    {
        var result = SessionProbeParser.Parse(AliveSession + "\r\n" + "TMUX\tx\t1\t1792568235\t1\r\n");

        result.Sessions.Should().HaveCount(1);
        result.Tmux.Should().HaveCount(1);
        result.Tmux[0].Name.Should().Be("x");
    }

    [Fact]
    public void ParseHostOutput_SplitsPerContainerAndCapturesExitCode()
    {
        var stdout = "BEGIN\tabc123\n"
                     + AliveSession + "\n"
                     + "TMUX\tcc-plan-sync\t1\t1792568235\t0\n"
                     + "END\tabc123\t0\n"
                     + "BEGIN\tdef456\n"
                     + "unable to find user bogus: no matching entries in passwd file\n"
                     + "END\tdef456\t126\n";

        var result = SessionProbeParser.ParseHostOutput(stdout);

        result.Should().ContainKey("abc123");
        result["abc123"].Sessions.Should().HaveCount(1);
        result["abc123"].Tmux.Should().HaveCount(1);
        result["abc123"].ProbeError.Should().BeNull();

        result.Should().ContainKey("def456");
        result["def456"].Sessions.Should().BeEmpty();
        result["def456"].ProbeError.Should().Contain("126").And.Contain("unable to find user");
    }

    [Fact]
    public void ParseHostOutput_MissingEnd_IsReportedAsProbeError()
    {
        var stdout = "BEGIN\tabc123\n" + AliveSession + "\n";

        var result = SessionProbeParser.ParseHostOutput(stdout);

        result["abc123"].Sessions.Should().HaveCount(1, "whatever was printed before the cut-off is still useful");
        result["abc123"].ProbeError.Should().NotBeNull();
    }

    [Theory]
    [InlineData("devcontainer.local_folder=/workspaces/x,devcontainer.metadata=[{\"id\":\"ghcr.io/x\"},{\"remoteUser\":\"node\",\"customizations\":{}}]", "node")]
    [InlineData("devcontainer.metadata=[{\"remoteUser\":\"vscode\"},{\"remoteUser\": \"node\"}]", "node")]
    [InlineData("devcontainer.local_folder=/workspaces/x", null)]
    [InlineData("", null)]
    public void ExtractRemoteUser_ReadsTheLastRemoteUserFromTheMetadataLabel(string labels, string? expected)
    {
        SessionProbeScript.ExtractRemoteUser(labels).Should().Be(expected);
    }

    [Theory]
    [InlineData("pks.project=api,devcontainer.local_folder=/workspaces/www", "c", "vsc-www-abc", "api", "/workspaces/www")]
    [InlineData("devcontainer.local_folder=/workspaces/agentic-live-www,devcontainer.metadata=[]", "c", "img", "agentic-live-www", "/workspaces/agentic-live-www")]
    // VS Code on the hetzner host sets config_file + metadata but no local_folder (observed 2026-10-28).
    [InlineData("devcontainer.config_file=/workspaces/agentic-live-www/.devcontainer/devcontainer.json,devcontainer.metadata=[]", "vigorous_sanderson", "vsc-agentic-live-www-2608d111d6a5f0084e917d13c30d2aa2c3955f455c388875f4362adb10f9492c", "agentic-live-www", "/workspaces/agentic-live-www")]
    [InlineData("devcontainer.config_file=/workspaces/x/.devcontainer.json", "c", "img", "x", "/workspaces/x")]
    [InlineData("devcontainer.metadata=[]", "quizzical_davinci", "vsc-agentic-live-www-2608d111d6a5f0084e917d13c30d2aa2c3955f455c388875f4362adb10f9492c", "agentic-live-www", "")]
    [InlineData("devcontainer.metadata=[]", "quizzical_davinci", "some/other:image", "quizzical_davinci", "")]
    public void DevcontainerLabels_Describe_DerivesProjectAndFolder(string labels, string name, string image, string expectedProject, string expectedFolder)
    {
        var (project, folder) = DevcontainerLabels.Describe(labels, name, image);

        project.Should().Be(expectedProject);
        folder.Should().Be(expectedFolder);
    }

    [Theory]
    [InlineData("2026-10-27 22:36:22 +0100 CET", "2026-10-27T21:36:22Z")]
    [InlineData("2026-10-24 08:59:53 +0200 CEST", "2026-10-24T06:59:53Z")]
    [InlineData("2026-10-20 07:00:00 +0000 UTC", "2026-10-20T07:00:00Z")]
    [InlineData("3 days ago", "3 days ago")]
    [InlineData("", "")]
    public void DockerTimestamps_ToIso_NormalisesDockerPsDates(string input, string expected)
    {
        DockerTimestamps.ToIso(input).Should().Be(expected);
    }

    [Fact]
    public void ContainerProbe_TmuxFormat_PutsTheNameLast()
    {
        // tmux replaces control characters with '_' on a non-tty stdout, so the format must not rely on tabs.
        SessionProbeScript.ContainerProbe.Should().Contain("#{session_windows} #{session_created} #{session_attached} #{session_name}");
        SessionProbeScript.ContainerProbe.Should().NotContain("#{session_name}\t");
    }

    [Fact]
    public void DevcontainerLabels_Filter_IsTheLabelEveryDevcontainerCarries()
    {
        DevcontainerLabels.Filter.Should().Be("label=devcontainer.metadata");
    }

    [Fact]
    public void BuildHostScript_EmitsBeginEndFramingAndUserFlag()
    {
        var script = SessionProbeScript.BuildHostScript(new[]
        {
            new ProbeTarget("abc123", "node"),
            new ProbeTarget("def456", null),
        });

        script.Should().Contain("BEGIN\\tabc123").And.Contain("END\\tabc123");
        script.Should().Contain("-u node abc123");
        script.Should().Contain("BEGIN\\tdef456");
        script.Should().NotContain("-u  def456");
        // The container-side probe is embedded verbatim so no base64 tooling is needed inside the container.
        script.Should().Contain("/home/*/.claude/sessions/*.json");
        script.Should().Contain("/tmp/tmux-*/default");
    }

    [Fact]
    public void BuildShellCommand_ContainsNoDoubleQuotes()
    {
        // ISshCommandRunner wraps any argument containing a space in bare double quotes without
        // escaping, so the command handed to ssh must never contain one itself.
        var cmd = SessionProbeScript.BuildShellCommand("printf '%s\\n' \"hello world\" $HOME `id`");

        cmd.Should().NotContain("\"");
        cmd.Should().NotContain("$");
        cmd.Should().NotContain("`");
        cmd.Should().StartWith("echo ").And.EndWith("| base64 -d | sh");
    }

    [Fact]
    public void BuildHostScript_RejectsUnsafeContainerIdsAndUsers()
    {
        var act = () => SessionProbeScript.BuildHostScript(new[] { new ProbeTarget("abc; rm -rf /", null) });
        act.Should().Throw<ArgumentException>();

        var act2 = () => SessionProbeScript.BuildHostScript(new[] { new ProbeTarget("abc123", "no de") });
        act2.Should().Throw<ArgumentException>();
    }
}
