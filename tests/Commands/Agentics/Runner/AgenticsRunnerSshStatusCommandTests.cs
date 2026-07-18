using FluentAssertions;
using Moq;
using PKS.Commands.Agentics.Runner;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Agentics.Runner;

/// <summary>
/// Unit tests for the Claude-credential-volume warning wired into
/// <c>pks agentics runner status</c> (docs/remote-runner-targets-plan.md Phase 5, work item 1,
/// second half -- the same warning <c>OfferSshHandoffAsync</c> shows right after a successful
/// handoff, now also surfaced any time an operator checks status later).
/// </summary>
public class AgenticsRunnerSshStatusCommandTests
{
    private static SshTarget MakeTarget() => new()
    {
        Host = "203.0.113.10",
        Username = "runner",
        Port = 22,
        KeyPath = "/tmp/key",
        Label = "my-target",
    };

    private static AgenticsRunnerRegistration MakeRegistration() => new()
    {
        Id = "reg-1",
        Owner = "acme",
        Project = "widgets",
        Profile = new RunnerProfile { SshTargetLabel = "my-target" },
    };

    private static (AgenticsRunnerSshStatusCommand Command, Mock<IAgenticsRunnerSshHandoffService> Handoff, TestConsole Console)
        MakeCommand(SshTarget target, AgenticsRunnerRegistration registration, bool? volumePresent)
    {
        var sshTargets = new Mock<ISshTargetConfigurationService>();
        sshTargets.Setup(s => s.ListTargetsAsync()).ReturnsAsync(new List<SshTarget> { target });

        var runnerConfig = new Mock<IAgenticsRunnerConfigurationService>();
        runnerConfig.Setup(r => r.ListRegistrationsAsync()).ReturnsAsync(new List<AgenticsRunnerRegistration> { registration });

        var handoff = new Mock<IAgenticsRunnerSshHandoffService>();
        handoff.Setup(h => h.BuildTmuxSessionName(registration.Owner, registration.Project)).Returns("pks-acme-widgets");
        handoff.Setup(h => h.CapturePaneAsync(target, "pks-acme-widgets", It.IsAny<CancellationToken>()))
            .ReturnsAsync("some pane output");
        handoff.Setup(h => h.DetectClaudeCredentialVolumeAsync(target, registration.Owner, registration.Project, It.IsAny<CancellationToken>()))
            .ReturnsAsync(volumePresent);

        var console = new TestConsole();
        var command = new AgenticsRunnerSshStatusCommand(sshTargets.Object, runnerConfig.Object, handoff.Object, console);
        return (command, handoff, console);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ExecuteAsync_VolumeMissing_PrintsWarningWithClaudeLoginHint()
    {
        var target = MakeTarget();
        var registration = MakeRegistration();
        var (command, _, console) = MakeCommand(target, registration, volumePresent: false);

        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "status", null);
        var result = await command.ExecuteAsync(ctx, new AgenticsRunnerSshTargetSettings { Target = "my-target" });

        result.Should().Be(0);
        console.Output.Should().Contain("claude-login");
        console.Output.Should().ContainAny("No Claude credentials", "no Claude credentials");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ExecuteAsync_VolumePresent_DoesNotPrintWarning()
    {
        var target = MakeTarget();
        var registration = MakeRegistration();
        var (command, _, console) = MakeCommand(target, registration, volumePresent: true);

        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "status", null);
        var result = await command.ExecuteAsync(ctx, new AgenticsRunnerSshTargetSettings { Target = "my-target" });

        result.Should().Be(0);
        console.Output.Should().NotContain("claude-login");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ExecuteAsync_VolumeCheckInconclusive_DoesNotPrintWarning()
    {
        // Host unreachable for the credential-volume probe specifically (distinct from the pane
        // capture succeeding) should degrade silently rather than falsely claiming "missing".
        var target = MakeTarget();
        var registration = MakeRegistration();
        var (command, _, console) = MakeCommand(target, registration, volumePresent: null);

        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "status", null);
        var result = await command.ExecuteAsync(ctx, new AgenticsRunnerSshTargetSettings { Target = "my-target" });

        result.Should().Be(0);
        console.Output.Should().NotContain("claude-login");
    }
}
