using System.Net.Http;
using FluentAssertions;
using Moq;
using PKS.Commands.Agentics.Runner;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Commands.Agentics;

/// <summary>
/// Regression tests for the SSH handoff bookkeeping (Phase 4/5, docs/remote-runner-targets-plan.md).
///
/// <see cref="IAgenticsRunnerSshHandoffService.HandoffAsync"/> stamps
/// <see cref="RunnerProfile.SshTargetLabel"/> on a brand-new registration object that is serialized
/// and shipped to the REMOTE box. Nothing wrote it back to the LOCAL config, but
/// <c>SshHandoffCommandHelpers.ResolveAsync</c> matches purely on that field in the local config --
/// so every one of <c>pks agentics runner status|logs|stop|claude-login &lt;target&gt;</c> printed
/// "No project has been handed off to '&lt;target&gt;'", including the exact command the handoff
/// success message tells the operator to run.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AgenticsRunnerSshHandoffPersistenceTests
{
    private static AgenticsRunnerRegistration MakeRegistration() => new()
    {
        Id = "runner-1",
        Name = "test-runner",
        Token = "tok",
        Owner = "acme",
        Project = "widgets",
        Server = "https://agentics.test",
        RegisteredAt = DateTime.UtcNow,
    };

    private static SshTarget MakeTarget() => new()
    {
        Host = "hetzner.example.com",
        Username = "root",
        Label = "hetzner",
    };

    [Fact]
    public async Task OfferSshHandoffAsync_SuccessfulHandoff_PersistsSshTargetLabelOnLocalRegistration()
    {
        var registration = MakeRegistration();
        var target = MakeTarget();

        var handoff = new Mock<IAgenticsRunnerSshHandoffService>();
        handoff.Setup(x => x.ProbeAsync(It.IsAny<SshTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshProbeResult(
                DockerAvailable: true, TmuxAvailable: true, TmuxVersion: "tmux 3.3a",
                DotnetAvailable: true, DotnetVersion: "10.0.100", DnxAvailable: true));
        handoff.Setup(x => x.HandoffAsync(
                It.IsAny<SshTarget>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshHandoffResult(
                Success: true, RunnerName: "hetzner-runner", TmuxSessionName: "pks-acme-widgets",
                Elapsed: TimeSpan.FromSeconds(12), FailureReason: null, RemoteTmuxOutput: null));
        // Phase 5 post-handoff advisory -- must not NRE on a bare mock.
        handoff.Setup(x => x.DetectClaudeCredentialVolumeAsync(
                It.IsAny<SshTarget>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        AgenticsRunnerRegistration? persisted = null;
        var configService = new Mock<IAgenticsRunnerConfigurationService>();
        configService.Setup(x => x.AddRegistrationAsync(It.IsAny<AgenticsRunnerRegistration>()))
            .Callback<AgenticsRunnerRegistration>(r => persisted = r)
            .ReturnsAsync((AgenticsRunnerRegistration r) => r);

        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("y");            // "Hand off this runner...?"
        console.Input.PushKey(ConsoleKey.Enter);          // target selection (single target)
        console.Input.PushTextWithEnter("hetzner-runner"); // runner name prompt

        var command = CreateCommand(configService.Object, handoff.Object, console);

        var handedOff = await command.OfferSshHandoffAsync(registration, new List<SshTarget> { target });

        handedOff.Should().BeTrue();

        // The local registration is what `pks agentics runner status hetzner` reads.
        persisted.Should().NotBeNull("a successful handoff must be recorded in the LOCAL runner config");
        persisted!.Profile.Should().NotBeNull();
        persisted.Profile!.SshTargetLabel.Should().Be("hetzner");
        persisted.Owner.Should().Be("acme");
        persisted.Project.Should().Be("widgets");

        // ...and it must be the same registration object the caller passed in, not a fresh one.
        registration.Profile!.SshTargetLabel.Should().Be("hetzner");
    }

    [Fact]
    public async Task OfferSshHandoffAsync_FailedHandoff_DoesNotPersistSshTargetLabel()
    {
        var registration = MakeRegistration();
        var target = MakeTarget();

        var handoff = new Mock<IAgenticsRunnerSshHandoffService>();
        handoff.Setup(x => x.ProbeAsync(It.IsAny<SshTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshProbeResult(true, true, "tmux 3.3a", true, "10.0.100", true));
        handoff.Setup(x => x.HandoffAsync(
                It.IsAny<SshTarget>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshHandoffResult(
                Success: false, RunnerName: "hetzner-runner", TmuxSessionName: "pks-acme-widgets",
                Elapsed: TimeSpan.FromSeconds(120), FailureReason: "timed out waiting for online",
                RemoteTmuxOutput: "boom"));

        var configService = new Mock<IAgenticsRunnerConfigurationService>();

        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("y");
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("hetzner-runner");

        var command = CreateCommand(configService.Object, handoff.Object, console);

        var handedOff = await command.OfferSshHandoffAsync(registration, new List<SshTarget> { target });

        handedOff.Should().BeFalse();
        registration.Profile?.SshTargetLabel.Should().BeNull();
        configService.Verify(x => x.AddRegistrationAsync(It.IsAny<AgenticsRunnerRegistration>()), Times.Never);
    }

    private static AgenticsRunnerStartCommand CreateCommand(
        IAgenticsRunnerConfigurationService configService,
        IAgenticsRunnerSshHandoffService handoff,
        Spectre.Console.Testing.TestConsole console)
    {
        var foundryAuthService = new Mock<IAzureFoundryAuthService>();
        var chatProviderFactory = new AgentChatProviderFactory(
            new Mock<IConfigurationService>().Object, new HttpClient(), foundryAuthService.Object);

        return new AgenticsRunnerStartCommand(
            configService,
            new Mock<IDevcontainerSpawnerService>().Object,
            new Mock<IHttpClientFactory>().Object,
            new Mock<IGitHubAuthenticationService>().Object,
            foundryAuthService.Object,
            new AzureFoundryAuthConfig(),
            chatProviderFactory,
            console,
            new Mock<IRunnerExecutionCapabilityProbe>().Object,
            new Mock<ISshTargetConfigurationService>().Object,
            handoff);
    }
}
