using System.Net;
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
/// Unit tests for the client-side pre-claim refusal (Defect A/D2 fix,
/// docs/remote-runner-targets-plan.md Phase 1):
/// <see cref="AgenticsRunnerStartCommand.PollAndDispatchOnceAsync"/> must never reach
/// <c>POST .../runners/generate-jitconfig</c> for an ordinary (jobType == null) station job
/// when devcontainer spawning is unavailable -- the poll endpoint only reads, so simply not
/// claiming leaves the job "queued" for a capable runner instead of claiming-then-failing it.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AgenticsRunnerDegradedStartTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public readonly List<string> RequestedUrls = new();

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return await _handler(request);
        }
    }

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

    [Fact]
    public async Task PollAndDispatchOnceAsync_DockerUnavailable_OrdinaryStationJob_NeverClaimsAndLogsGreyDecline()
    {
        // Arrange: probe reports Docker unavailable (mirrors a Docker-less machine / Docker
        // Desktop stopped).
        var probe = new Mock<IRunnerExecutionCapabilityProbe>();
        probe.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerExecutionCapabilityStatus(false, "Docker is not available: connection refused"));

        // The poll endpoint (POST .../runners/jobs) returns exactly one ordinary station job --
        // jobType == null, needs == [] -- the shape every pre-Phase-2 job carries (see decision
        // D2). It must fall into the spawn-mode branch and be declined, never claimed.
        var registration = MakeRegistration();
        var jobsUrl = $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}/runners/jobs";
        var jitconfigUrl = $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}/runners/generate-jitconfig";

        var handler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString() == jobsUrl)
            {
                var json = """
                {
                  "jobs": [
                    {
                      "id": "job-1",
                      "runId": "run-1",
                      "agentDefinition": { "jobType": null, "needs": [] }
                    }
                  ]
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                });
            }

            // Anything else (in particular generate-jitconfig) must never be hit -- fail loudly
            // if it is, rather than silently answering it.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"unexpected request to {req.RequestUri}"),
            });
        });

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var configService = new Mock<PKS.Infrastructure.Services.Runner.IAgenticsRunnerConfigurationService>();
        var spawnerService = new Mock<IDevcontainerSpawnerService>();
        var githubAuth = new Mock<IGitHubAuthenticationService>();
        var foundryAuthService = new Mock<IAzureFoundryAuthService>();
        var foundryConfig = new AzureFoundryAuthConfig();
        var chatProviderFactory = new AgentChatProviderFactory(
            new Mock<IConfigurationService>().Object, new HttpClient(), foundryAuthService.Object);

        var console = new Spectre.Console.Testing.TestConsole();
        // Non-interactive, per the injectable check (_console.Profile.Capabilities.Interactive) --
        // ClaudeAnthropicCommand.cs is the exemplar for this pattern.
        console.Profile.Capabilities.Interactive = false;

        var command = new AgenticsRunnerStartCommand(
            configService.Object,
            spawnerService.Object,
            httpClientFactory.Object,
            githubAuth.Object,
            foundryAuthService.Object,
            foundryConfig,
            chatProviderFactory,
            console,
            probe.Object);

        var settings = new AgenticsRunnerStartCommand.Settings
        {
            InProcess = false,
            PollingInterval = 10,
        };

        // Act: exercise PollAndDispatchOnceAsync directly (extracted from the poll `while` loop
        // precisely so it's testable without driving the real infinite loop / CancelKeyPress
        // plumbing) with credentialServer == null -- the state ExecuteAsync leaves it in when
        // the startup probe already found spawn mode unavailable.
        var jobsProcessed = await command.PollAndDispatchOnceAsync(
            registration,
            settings,
            credentialServer: null,
            chatLlmBackendUrl: "http://localhost:11434/v1",
            chatLlmBackendKey: null,
            chatLlmModelId: "gpt-5.5",
            chatLlmVerbose: false,
            ct: CancellationToken.None);

        // Assert: no HTTP call ever reached generate-jitconfig -- the poll endpoint only reads
        // (jobs/route.ts), so never calling generate-jitconfig is what leaves the job `queued`
        // for a capable runner instead of claiming and then failing it.
        handler.RequestedUrls.Should().Contain(jobsUrl);
        handler.RequestedUrls.Should().NotContain(jitconfigUrl);

        jobsProcessed.Should().Be(0);

        console.Output.Should().Contain("Declining job job-1");
        console.Output.Should().Contain("devcontainer spawning unavailable");
    }

    [Fact]
    public async Task PollAndDispatchOnceAsync_DockerRecoveredButCredentialServerNull_DoesNotAdvertiseSpawnCapabilities()
    {
        // Regression: capabilities are recomputed every poll behind the probe's 60s memo, so a
        // dockerd that comes back mid-run used to flip alp_operator / chat-session:v1 /
        // devcontainer-session:v1 back ON. But credentialServer is constructed exactly once at
        // startup and only when spawn mode was available THEN -- a runner that started degraded can
        // never actually spawn. Advertising anyway means the server hands it every
        // needs:['alp_operator'] station job and the pre-claim check declines 100% of them, leaving
        // them queued forever with BOTH server-side safety nets blind (noOnlineRunnerWarning and
        // StaleJobMonitor both see an online runner advertising alp_operator).
        var probe = new Mock<IRunnerExecutionCapabilityProbe>();
        probe.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerExecutionCapabilityStatus(true, "Docker is running (version 27.1)"));

        var registration = MakeRegistration();
        var jobsUrl = $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}/runners/jobs";

        var pollBodies = new List<string>();
        var handler = new StubHttpMessageHandler(async req =>
        {
            if (req.RequestUri!.ToString() == jobsUrl && req.Content != null)
                pollBodies.Add(await req.Content.ReadAsStringAsync());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"jobs":[]}""", System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        var foundryAuthService = new Mock<IAzureFoundryAuthService>();
        var chatProviderFactory = new AgentChatProviderFactory(
            new Mock<IConfigurationService>().Object, new HttpClient(), foundryAuthService.Object);

        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Capabilities.Interactive = false;

        var command = new AgenticsRunnerStartCommand(
            new Mock<PKS.Infrastructure.Services.Runner.IAgenticsRunnerConfigurationService>().Object,
            new Mock<IDevcontainerSpawnerService>().Object,
            httpClientFactory.Object,
            new Mock<IGitHubAuthenticationService>().Object,
            foundryAuthService.Object,
            new AzureFoundryAuthConfig(),
            chatProviderFactory,
            console,
            probe.Object);

        var settings = new AgenticsRunnerStartCommand.Settings { InProcess = false, PollingInterval = 10 };

        await command.PollAndDispatchOnceAsync(
            registration,
            settings,
            credentialServer: null, // started degraded -- never constructed, never re-constructed
            chatLlmBackendUrl: "http://localhost:11434/v1",
            chatLlmBackendKey: null,
            chatLlmModelId: "gpt-5.5",
            chatLlmVerbose: false,
            ct: CancellationToken.None);

        pollBodies.Should().ContainSingle();
        pollBodies[0].Should().NotContain("alp_operator");
        pollBodies[0].Should().NotContain("chat-session:v1");
        pollBodies[0].Should().NotContain("devcontainer-session:v1");
        // Non-spawn work is unaffected.
        pollBodies[0].Should().Contain("chat-llm:v1");

        console.Output.Should().NotContain("alp_operator");
    }
}
