using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for <see cref="ClaudeCredentialVolumes"/>'s pure naming + remote-detection
/// command/parser (docs/remote-runner-targets-plan.md Phase 5, work item 1). Mirrors the
/// naming rules documented on <c>AgenticsRunnerStartCommand.PatchDevcontainerVolumes</c>,
/// which now delegates here so the two never drift apart.
/// </summary>
public class ClaudeCredentialVolumesTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ResolveVolumeName_ProjectScope_IsDefault_WhenScopeNullOrUnrecognized()
    {
        ClaudeCredentialVolumes.ResolveVolumeName("Acme", "Widgets", taskId: null, scope: null)
            .Should().Be("pks-claude-acme-widgets");

        ClaudeCredentialVolumes.ResolveVolumeName("Acme", "Widgets", taskId: null, scope: "not-a-real-scope")
            .Should().Be("pks-claude-acme-widgets");

        ClaudeCredentialVolumes.ResolveVolumeName("Acme", "Widgets", taskId: null, scope: "project")
            .Should().Be("pks-claude-acme-widgets");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ResolveVolumeName_RunnerScope_IsOwnerOnly()
    {
        ClaudeCredentialVolumes.ResolveVolumeName("Acme", "Widgets", taskId: null, scope: "runner")
            .Should().Be("pks-claude-acme");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ResolveVolumeName_TaskScope_WithTaskId_IncludesTaskSuffix()
    {
        ClaudeCredentialVolumes.ResolveVolumeName("Acme", "Widgets", taskId: "T-42", scope: "task")
            .Should().Be("pks-claude-acme-widgets-task-t-42");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ResolveVolumeName_TaskScope_WithoutTaskId_FallsBackToProjectScope()
    {
        ClaudeCredentialVolumes.ResolveVolumeName("Acme", "Widgets", taskId: null, scope: "task")
            .Should().Be("pks-claude-acme-widgets");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ResolveVolumeName_SanitizesNonAlphanumericCharacters()
    {
        ClaudeCredentialVolumes.ResolveVolumeName("Acme Corp!", "Widgets_2.0", taskId: null, scope: "project")
            .Should().Be("pks-claude-acme-corp--widgets-2-0");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildDetectCommand_NeverEmitsDoubleQuotes()
    {
        // ExecuteProcessAsync/ISshCommandRunner naively wrap any space-containing argument in an
        // unescaped outer "..." pair -- an embedded double quote in the remote command would
        // corrupt that wrapping (same constraint as SshRunnerProbe.BuildProbeCommand).
        var command = ClaudeCredentialVolumes.BuildDetectCommand("pks-claude-acme-widgets");

        command.Should().NotContain("\"");
        command.Should().Contain("pks-claude-acme-widgets");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseDetectOutput_PresentMarker_ReturnsTrue()
    {
        ClaudeCredentialVolumes.ParseDetectOutput("PKS_CLAUDE_VOLUME_PRESENT\n").Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseDetectOutput_MissingMarker_ReturnsFalse()
    {
        ClaudeCredentialVolumes.ParseDetectOutput("PKS_CLAUDE_VOLUME_MISSING\n").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ParseDetectOutput_EmptyOrUnexpectedOutput_ReturnsFalse_NoThrow()
    {
        ClaudeCredentialVolumes.ParseDetectOutput(string.Empty).Should().BeFalse();
        ClaudeCredentialVolumes.ParseDetectOutput("garbage").Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void MountTarget_IsAbsoluteAndUserIndependent()
    {
        // The whole point of the constant: the agent's home depends on the image's USER (root for
        // stock devcontainer images, node for the house ones), so a home-relative target only works
        // for the images whose home happens to match. Anything under /home or /root, or any "~",
        // reintroduces exactly the coupling this replaced.
        ClaudeCredentialVolumes.MountTarget.Should().StartWith("/");
        ClaudeCredentialVolumes.MountTarget.Should().NotContain("~");
        ClaudeCredentialVolumes.MountTarget.Should().NotStartWith("/home/");
        ClaudeCredentialVolumes.MountTarget.Should().NotStartWith("/root");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildMountArg_TargetsMountTarget_AndComposesWithOtherFragments()
    {
        var arg = ClaudeCredentialVolumes.BuildMountArg("pks-claude-acme-widgets");

        // Leading space so it can be concatenated straight after another --mount fragment.
        arg.Should().StartWith(" --mount ");
        arg.Should().Be(
            $" --mount type=volume,source=pks-claude-acme-widgets,target={ClaudeCredentialVolumes.MountTarget}");

        // No spaces inside the value: the devcontainer up command line is assembled as one string
        // and passed through a shell, so an embedded space would split the argument.
        arg.Trim().Split(' ').Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildMountArg_NoVolume_IsEmpty_SoCallersCanConcatenateUnconditionally()
    {
        ClaudeCredentialVolumes.BuildMountArg(null).Should().BeEmpty();
        ClaudeCredentialVolumes.BuildMountArg(string.Empty).Should().BeEmpty();
        ClaudeCredentialVolumes.BuildMountArg("   ").Should().BeEmpty();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    [InlineData("task", "t-42", "pks-claude-acme-widgets-task-t-42")]
    [InlineData("project", "t-42", "pks-claude-acme-widgets")]
    [InlineData("runner", "t-42", "pks-claude-acme")]
    public void BuildMountArg_CarriesWhicheverScopeThePlatformChose(
        string scope, string taskId, string expectedVolume)
    {
        // Guards the seam Ændring 2 depends on: the scope decision stays on the platform
        // (assembly-line setting → project setting → runner default) and the mount only transports
        // the name it was handed. If the mount ever started deriving a name, this drifts.
        var name = ClaudeCredentialVolumes.ResolveVolumeName("Acme", "Widgets", taskId, scope);
        name.Should().Be(expectedVolume);

        ClaudeCredentialVolumes.BuildMountArg(name).Should().Contain($"source={expectedVolume},");
    }
}
