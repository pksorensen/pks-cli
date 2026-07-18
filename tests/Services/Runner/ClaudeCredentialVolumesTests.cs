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
}
