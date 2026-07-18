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

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for the honest-capabilities probe (Defect A fix, docs/remote-runner-targets-plan.md
/// Phase 1): <see cref="RunnerExecutionCapabilityProbe"/>'s own memoization behavior, and
/// <see cref="AgenticsRunnerStartCommand.ComputeCapabilitiesAsync"/>'s gating of
/// devcontainer-spawn-dependent capabilities against it.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class RunnerCapabilityProbeTests
{
    // ── RunnerExecutionCapabilityProbe: memoization ─────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_WithinMemoWindow_DoesNotReprobeDocker()
    {
        var spawner = new Mock<IDevcontainerSpawnerService>();
        spawner.Setup(x => x.CheckDockerAvailabilityAsync())
            .ReturnsAsync(new DockerAvailabilityResult { IsAvailable = true, IsRunning = true, Message = "Docker is running (version 27.1)" });

        var probe = new RunnerExecutionCapabilityProbe(spawner.Object);

        var first = await probe.GetStatusAsync();
        var second = await probe.GetStatusAsync();

        first.DockerAvailable.Should().BeTrue();
        second.DockerAvailable.Should().BeTrue();
        spawner.Verify(x => x.CheckDockerAvailabilityAsync(), Times.Once,
            "a second call within the 60s memo window must not re-ping Docker");
    }

    [Fact]
    public async Task GetStatusAsync_ReflectsDockerUnavailable_WithReason()
    {
        var spawner = new Mock<IDevcontainerSpawnerService>();
        spawner.Setup(x => x.CheckDockerAvailabilityAsync())
            .ReturnsAsync(new DockerAvailabilityResult { IsAvailable = false, IsRunning = false, Message = "Docker is not available: no such file or directory" });

        var probe = new RunnerExecutionCapabilityProbe(spawner.Object);
        var status = await probe.GetStatusAsync();

        status.DockerAvailable.Should().BeFalse();
        status.Reason.Should().Contain("Docker is not available");
    }

    // ── AgenticsRunnerStartCommand.ComputeCapabilitiesAsync: gating ────────────────────────

    private static AgenticsRunnerStartCommand CreateCommand(
        IRunnerExecutionCapabilityProbe probe,
        IGitHubAuthenticationService? githubAuth = null)
    {
        var configService = new Mock<PKS.Infrastructure.Services.Runner.IAgenticsRunnerConfigurationService>();
        var spawnerService = new Mock<IDevcontainerSpawnerService>();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        githubAuth ??= new Mock<IGitHubAuthenticationService>().Object;
        var foundryAuthService = new Mock<IAzureFoundryAuthService>();
        var foundryConfig = new AzureFoundryAuthConfig();
        var chatProviderFactory = new AgentChatProviderFactory(
            new Mock<IConfigurationService>().Object, new HttpClient(), foundryAuthService.Object);
        var console = new Spectre.Console.Testing.TestConsole();

        return new AgenticsRunnerStartCommand(
            configService.Object,
            spawnerService.Object,
            httpClientFactory.Object,
            githubAuth,
            foundryAuthService.Object,
            foundryConfig,
            chatProviderFactory,
            console,
            probe);
    }

    [Fact]
    public async Task ComputeCapabilitiesAsync_ProbeUnavailable_InProcessFalse_ExcludesDockerCapabilities_ButKeepsChatLlm()
    {
        var probe = new Mock<IRunnerExecutionCapabilityProbe>();
        probe.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerExecutionCapabilityStatus(false, "Docker is not available: connection refused"));

        var command = CreateCommand(probe.Object);

        // Non-empty chatLlmBackendUrl short-circuits the AgentChatProviderFactory.ResolveAsync
        // path entirely (see ComputeCapabilitiesAsync), so chat-llm:v1 is asserted independent of
        // Docker availability -- it never touches the spawner.
        var caps = await command.ComputeCapabilitiesAsync(
            inProcess: false, chatLlmBackendUrl: "http://localhost:11434/v1", chatLlmModelId: "gpt-5.5", ct: CancellationToken.None);

        caps.Should().NotContain("alp_operator");
        caps.Should().NotContain("chat-session:v1");
        caps.Should().NotContain("devcontainer-session:v1");
        caps.Should().Contain("chat-llm:v1");

        probe.Verify(x => x.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ComputeCapabilitiesAsync_DockerUnavailable_InProcessTrue_IncludesAlpOperator_AndNeverInvokesProbe()
    {
        var probe = new Mock<IRunnerExecutionCapabilityProbe>();
        probe.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerExecutionCapabilityStatus(false, "Docker is not available: connection refused"));

        var githubAuth = new Mock<IGitHubAuthenticationService>();
        githubAuth.Setup(x => x.IsAuthenticatedAsync(It.IsAny<string?>())).ReturnsAsync(true);

        var command = CreateCommand(probe.Object, githubAuth.Object);

        var caps = await command.ComputeCapabilitiesAsync(
            inProcess: true, chatLlmBackendUrl: "http://localhost:11434/v1", chatLlmModelId: "gpt-5.5", ct: CancellationToken.None);

        // --inprocess runs jobs in a git worktree with no Docker at all (ExecuteInProcessAsync),
        // so alp_operator is retained even though DockerAvailable is (would be) false.
        caps.Should().Contain("alp_operator");
        // chat-session:v1 / devcontainer-session:v1 both run inside a spawned devcontainer, so
        // --inprocess does NOT retain them (decision D1) -- gated on dockerAvailable alone.
        caps.Should().NotContain("chat-session:v1");
        caps.Should().NotContain("devcontainer-session:v1");

        // --inprocess must skip the Docker probe entirely -- pinging Docker is meaningless when
        // spawn mode is off by flag, and RunnerExecutionCapabilityProbe wraps a Docker CLI call
        // that could be slow/hang on a machine with no Docker installed at all.
        probe.Verify(x => x.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ComputeCapabilitiesAsync_DockerAvailable_IncludesAllThreeSpawnCapabilities()
    {
        var probe = new Mock<IRunnerExecutionCapabilityProbe>();
        probe.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerExecutionCapabilityStatus(true, "Docker is running (version 27.1)"));

        var command = CreateCommand(probe.Object);

        var caps = await command.ComputeCapabilitiesAsync(
            inProcess: false, chatLlmBackendUrl: "http://localhost:11434/v1", chatLlmModelId: "gpt-5.5", ct: CancellationToken.None);

        caps.Should().Contain("alp_operator");
        caps.Should().Contain("chat-session:v1");
        caps.Should().Contain("devcontainer-session:v1");
    }

    // ── spawnEnabled: the structural half of the spawn gate ────────────────────────────────

    [Fact]
    public async Task ComputeCapabilitiesAsync_DockerRecoveredButSpawnDisabled_StillExcludesSpawnCapabilities()
    {
        // Regression: a runner that started while dockerd was down never constructed its
        // GitCredentialServer and never ran the GitHub device-code preflight, so
        // ExecuteSpawnModeAsync can never succeed for this process's lifetime. If the live probe
        // (which memoizes for only 60s) were allowed to re-add alp_operator on its own, the server
        // would hand this runner every `needs:['alp_operator']` station job and the pre-claim check
        // in PollAndDispatchOnceAsync would decline 100% of them -- leaving them queued forever with
        // both server-side safety nets blind, because an "online runner advertising alp_operator"
        // is exactly what they look for.
        var probe = new Mock<IRunnerExecutionCapabilityProbe>();
        probe.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerExecutionCapabilityStatus(true, "Docker is running (version 27.1)"));

        var command = CreateCommand(probe.Object);

        var caps = await command.ComputeCapabilitiesAsync(
            inProcess: false, chatLlmBackendUrl: "http://localhost:11434/v1", chatLlmModelId: "gpt-5.5",
            ct: CancellationToken.None, capabilityOverride: null, spawnEnabled: false);

        caps.Should().NotContain("alp_operator");
        caps.Should().NotContain("chat-session:v1");
        caps.Should().NotContain("devcontainer-session:v1");
        // Non-spawn work is unaffected -- this runner still serves chat-llm/git jobs.
        caps.Should().Contain("chat-llm:v1");

        // No point pinging Docker when the answer cannot change what we advertise.
        probe.Verify(x => x.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── git credentials must be probed, never assumed ──────────────────────────────────────

    [Fact]
    public async Task ComputeCapabilitiesAsync_SpawnMode_NoGitHubToken_ExcludesGitCapabilities()
    {
        // Regression: git:push / git-distribute used to be hardcoded on for every non-inprocess
        // run, justified by a startup preflight that no longer runs on a Docker-less machine.
        // ExecuteGitPushJobAsync CLAIMS the job before resolving credentials, so advertising these
        // without a token means claim-then-fail.
        var probe = new Mock<IRunnerExecutionCapabilityProbe>();
        probe.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerExecutionCapabilityStatus(false, "Docker is not available"));

        var githubAuth = new Mock<IGitHubAuthenticationService>();
        githubAuth.Setup(x => x.IsAuthenticatedAsync(It.IsAny<string?>())).ReturnsAsync(false);

        var command = CreateCommand(probe.Object, githubAuth.Object);

        // ComputeCapabilitiesAsync falls back to a working GIT_ASKPASS helper when the stored token
        // is missing. This devcontainer injects one, so clear it to keep the assertion hermetic.
        var savedAskpass = Environment.GetEnvironmentVariable("GIT_ASKPASS");
        Environment.SetEnvironmentVariable("GIT_ASKPASS", null);
        try
        {
            var caps = await command.ComputeCapabilitiesAsync(
                inProcess: false, chatLlmBackendUrl: "http://localhost:11434/v1", chatLlmModelId: "gpt-5.5",
                ct: CancellationToken.None, capabilityOverride: null, spawnEnabled: false);

            caps.Should().NotContain("git:push");
            caps.Should().NotContain("git-distribute");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_ASKPASS", savedAskpass);
        }
    }

    [Fact]
    public async Task ComputeCapabilitiesAsync_SpawnMode_WithGitHubToken_IncludesGitCapabilities()
    {
        var probe = new Mock<IRunnerExecutionCapabilityProbe>();
        probe.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerExecutionCapabilityStatus(true, "Docker is running (version 27.1)"));

        var githubAuth = new Mock<IGitHubAuthenticationService>();
        githubAuth.Setup(x => x.IsAuthenticatedAsync(It.IsAny<string?>())).ReturnsAsync(true);

        var command = CreateCommand(probe.Object, githubAuth.Object);

        var caps = await command.ComputeCapabilitiesAsync(
            inProcess: false, chatLlmBackendUrl: "http://localhost:11434/v1", chatLlmModelId: "gpt-5.5",
            ct: CancellationToken.None);

        caps.Should().Contain("git:push");
        caps.Should().Contain("git-distribute");
    }
}
