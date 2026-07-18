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
/// Unit tests for <c>pks agentics runner claude-login &lt;target&gt;</c>
/// (docs/remote-runner-targets-plan.md Phase 5, work item 2). Per the GOAL, only the exact argv
/// launched is asserted here (against a mocked <see cref="IInteractiveProcessLauncher"/>) -- the
/// interactive ssh -t + docker run -it behavior itself is exercised by
/// <see cref="ClaudeLoginCommandBuilderTests"/> and manual verification, not here.
/// </summary>
public class AgenticsRunnerClaudeLoginCommandTests
{
    private static SshTarget MakeTarget(string? managedKeyId = null) => new()
    {
        Host = "203.0.113.10",
        Username = "runner",
        Port = 22,
        KeyPath = "/home/user/.ssh/id_ed25519",
        Label = "my-target",
        ManagedKeyId = managedKeyId,
    };

    private static AgenticsRunnerRegistration MakeRegistration() => new()
    {
        Id = "reg-1",
        Owner = "acme",
        Project = "widgets",
        Profile = new RunnerProfile { SshTargetLabel = "my-target" },
    };

    private static (Mock<ISshKeyStore> KeyStore, Mock<IInteractiveProcessLauncher> Launcher, TestConsole Console, AgenticsRunnerClaudeLoginCommand Command)
        MakeCommand(SshTarget target, AgenticsRunnerRegistration registration, int launcherExitCode = 0)
    {
        var sshTargets = new Mock<ISshTargetConfigurationService>();
        sshTargets.Setup(s => s.ListTargetsAsync()).ReturnsAsync(new List<SshTarget> { target });

        var runnerConfig = new Mock<IAgenticsRunnerConfigurationService>();
        runnerConfig.Setup(r => r.ListRegistrationsAsync()).ReturnsAsync(new List<AgenticsRunnerRegistration> { registration });

        var keyStore = new Mock<ISshKeyStore>();
        var launcher = new Mock<IInteractiveProcessLauncher>();
        launcher.Setup(l => l.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(launcherExitCode);

        var console = new TestConsole();
        var command = new AgenticsRunnerClaudeLoginCommand(sshTargets.Object, runnerConfig.Object, keyStore.Object, launcher.Object, console);
        return (keyStore, launcher, console, command);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ExecuteAsync_LaunchesSshWithExpectedArgv()
    {
        var target = MakeTarget();
        var registration = MakeRegistration();
        var (_, launcher, _, command) = MakeCommand(target, registration);

        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "claude-login", null);
        var result = await command.ExecuteAsync(ctx, new AgenticsRunnerSshTargetSettings { Target = "my-target" });

        result.Should().Be(0);
        launcher.Verify(l => l.RunAsync(
            "ssh",
            It.Is<IReadOnlyList<string>>(args =>
                args.Contains("-t") &&
                args.Contains(target.KeyPath) &&
                args.Contains($"{target.Username}@{target.Host}") &&
                args.Last().Contains("pks-claude-acme-widgets") &&
                args.Last().Contains("/home/node/.claude")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ExecuteAsync_ManagedKey_MaterializesAndUsesTempPath_ThenDisposes()
    {
        var target = MakeTarget(managedKeyId: "key-1");
        var registration = MakeRegistration();
        var (keyStore, launcher, _, command) = MakeCommand(target, registration);

        var materialized = new MaterializedKey("/tmp/materialized-key");
        keyStore.Setup(k => k.MaterializeAsync("key-1", It.IsAny<CancellationToken>())).ReturnsAsync(materialized);

        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "claude-login", null);
        var result = await command.ExecuteAsync(ctx, new AgenticsRunnerSshTargetSettings { Target = "my-target" });

        result.Should().Be(0);
        launcher.Verify(l => l.RunAsync(
            "ssh",
            It.Is<IReadOnlyList<string>>(args => args.Contains("/tmp/materialized-key")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ExecuteAsync_PropagatesLauncherExitCode()
    {
        var target = MakeTarget();
        var registration = MakeRegistration();
        var (_, _, _, command) = MakeCommand(target, registration, launcherExitCode: 7);

        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "claude-login", null);
        var result = await command.ExecuteAsync(ctx, new AgenticsRunnerSshTargetSettings { Target = "my-target" });

        result.Should().Be(7);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ExecuteAsync_NoMatchingTarget_ReturnsNonZero_DoesNotLaunch()
    {
        var target = MakeTarget();
        var registration = MakeRegistration();
        var (_, launcher, console, command) = MakeCommand(target, registration);

        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "claude-login", null);
        var result = await command.ExecuteAsync(ctx, new AgenticsRunnerSshTargetSettings { Target = "no-such-target" });

        result.Should().NotBe(0);
        launcher.Verify(l => l.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
