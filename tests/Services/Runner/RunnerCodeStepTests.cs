using PKS.CLI.Tests.Security;
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
/// code_step jobs (AgenticsRunnerRunCommand.CodeStep.cs): capability gating, the
/// <c>codeStepPayload</c> wire contract, the exit-code → conclusion/logs mapping, and the
/// AGENTICS_* export lines shared with start.sh.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class RunnerCodeStepTests
{
    private static AgenticsRunnerRunCommand CreateCommand(bool dockerAvailable)
    {
        var probe = new Mock<IRunnerExecutionCapabilityProbe>();
        probe.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerExecutionCapabilityStatus(dockerAvailable, dockerAvailable ? "ok" : "Docker is not available"));
        var githubAuth = new Mock<IGitHubAuthenticationService>();
        githubAuth.Setup(x => x.IsAuthenticatedAsync(It.IsAny<string?>())).ReturnsAsync(true);
        var foundryAuthService = new Mock<IAzureFoundryAuthService>();
        var chatProviderFactory = new AgentChatProviderFactory(
            new Mock<IConfigurationService>().Object, new HttpClient(), FakeSecretResolver.Empty, foundryAuthService.Object);

        return new AgenticsRunnerRunCommand(
            new Mock<IAgenticsRunnerConfigurationService>().Object,
            new Mock<IDevcontainerSpawnerService>().Object,
            new Mock<IHttpClientFactory>().Object,
            githubAuth.Object,
            foundryAuthService.Object,
            new AzureFoundryAuthConfig(),
            chatProviderFactory,
            new Spectre.Console.Testing.TestConsole(),
            probe.Object)
        {
            McpTaskDefaultConfigPathOverride = Path.Combine(Path.GetTempPath(), $"no-mcp-{Guid.NewGuid():N}.json"),
        };
    }

    private static Task<List<string>> Caps(AgenticsRunnerRunCommand command, bool inProcess, List<string>? profile = null, bool spawnEnabled = true)
        => command.ComputeCapabilitiesAsync(inProcess, "http://localhost:11434/v1", "gpt-5.5", CancellationToken.None,
            capabilityOverride: profile, spawnEnabled: spawnEnabled);

    // ── capability gating ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CodeStep_IsAdvertised_ExactlyWhenAlpOperatorIs_InSpawnMode(bool dockerAvailable, bool spawnEnabled)
    {
        var caps = await Caps(CreateCommand(dockerAvailable), inProcess: false, spawnEnabled: spawnEnabled);

        caps.Contains("code-step:v1").Should().Be(caps.Contains("alp_operator"));
        caps.Contains("code-step:v1").Should().Be(dockerAvailable && spawnEnabled);
    }

    [Fact]
    public async Task CodeStep_IsNotAdvertised_InProcess_EvenThoughAlpOperatorIs()
    {
        // --inprocess has no job container to run the step in.
        var caps = await Caps(CreateCommand(dockerAvailable: true), inProcess: true);

        caps.Should().Contain("alp_operator");
        caps.Should().NotContain("code-step:v1");
    }

    [Fact]
    public async Task CodeStep_SurvivesAProfileSavedBeforeItExisted_ThatListsAlpOperator()
    {
        var caps = await Caps(CreateCommand(dockerAvailable: true), inProcess: false,
            profile: new List<string> { "alp_operator", "git:push" });

        caps.Should().Contain("alp_operator").And.Contain("code-step:v1");
        caps.Should().NotContain("chat-session:v1");
    }

    [Fact]
    public async Task CodeStep_IsNarrowedAway_ByAProfileWithoutAlpOperatorOrCodeStep()
    {
        var caps = await Caps(CreateCommand(dockerAvailable: true), inProcess: false,
            profile: new List<string> { "chat-llm:v1" });

        caps.Should().NotContain("code-step:v1");
    }

    [Fact]
    public async Task CodeStep_ListedExplicitlyInProfile_IsKept()
    {
        var caps = await Caps(CreateCommand(dockerAvailable: true), inProcess: false,
            profile: new List<string> { "code-step:v1" });

        caps.Should().Equal("code-step:v1");
    }

    // ── wire contract ────────────────────────────────────────────────────────────────────

    [Fact]
    public void AgentDefinition_WithCodeStepPayload_Deserializes()
    {
        const string json = """
            {
              "jobType": "code_step",
              "repository": "https://github.com/acme/line.git",
              "branch": "main",
              "taskId": "t1", "assemblyLineId": "al1", "columnId": "c1",
              "needs": ["code-step:v1"],
              "codeStepPayload": { "command": "./watch.sh --once", "timeoutMs": 90000 }
            }
            """;

        var (jobType, payload) = AgenticsRunnerRunCommand.ParseCodeStepAgentDefinition(json);

        jobType.Should().Be("code_step");
        payload.Should().NotBeNull();
        payload!.Command.Should().Be("./watch.sh --once");
        payload.TimeoutMs.Should().Be(90000);
    }

    [Fact]
    public void AgentDefinition_WithoutTimeout_UsesTenMinuteDefault()
    {
        var (_, payload) = AgenticsRunnerRunCommand.ParseCodeStepAgentDefinition(
            """{"jobType":"code_step","codeStepPayload":{"command":"true"}}""");

        payload!.TimeoutMs.Should().BeNull();
        AgenticsRunnerRunCommand.CodeStepTimeoutSeconds(payload.TimeoutMs).Should().Be(600);
        AgenticsRunnerRunCommand.CodeStepTimeoutSeconds(1500).Should().Be(2);
        AgenticsRunnerRunCommand.CodeStepTimeoutSeconds(0).Should().Be(600);
    }

    // ── conclusion + logs ────────────────────────────────────────────────────────────────

    [Fact]
    public void ExitZero_IsSuccess_WithExitLineFirst()
    {
        var (conclusion, logs) = AgenticsRunnerRunCommand.FormatCodeStepCompletion(0, "checked 12 domains\n");

        conclusion.Should().Be("success");
        logs.Should().Be("exit 0\nchecked 12 domains");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(127)]
    public void NonZeroExit_IsFailure(int exitCode)
    {
        var (conclusion, logs) = AgenticsRunnerRunCommand.FormatCodeStepCompletion(exitCode, "boom");

        conclusion.Should().Be("failure");
        logs.Should().StartWith($"exit {exitCode}\n").And.EndWith("boom");
    }

    [Fact]
    public void Timeout_IsFailure_AndSaysSo()
    {
        var (conclusion, logs) = AgenticsRunnerRunCommand.FormatCodeStepCompletion(124, "partial", timedOut: true, timeoutSeconds: 600);

        conclusion.Should().Be("failure");
        logs.Should().Be("exit 124\ntimed out after 600s\npartial");
    }

    [Theory]
    [InlineData(124, 600, true)]
    [InlineData(137, 610, true)]
    [InlineData(137, 2, false)]   // OOM-killed early: a failure, not a timeout
    [InlineData(124, 5, false)]   // the script's own exit 124
    [InlineData(1, 600, false)]
    public void Timeout_IsOnlyReported_WhenTheLimitWasActuallyReached(int exitCode, int elapsedSeconds, bool expected)
    {
        AgenticsRunnerRunCommand.IsCodeStepTimeout(exitCode, TimeSpan.FromSeconds(elapsedSeconds), 600).Should().Be(expected);
    }

    [Fact]
    public void SpawnError_IsFailure_EvenWithoutAnExitCode()
    {
        var (conclusion, logs) = AgenticsRunnerRunCommand.FormatCodeStepCompletion(-1, null, error: "devcontainer spawn failed: no docker");

        conclusion.Should().Be("failure");
        logs.Should().Be("exit -1\ndevcontainer spawn failed: no docker");
    }

    [Fact]
    public void LongOutput_KeepsTheTail_AndTheExitLine_WithinTheCap()
    {
        var output = new string('a', 10_000) + "THE-END";

        var (_, logs) = AgenticsRunnerRunCommand.FormatCodeStepCompletion(3, output);

        logs.Length.Should().Be(AgenticsRunnerRunCommand.CodeStepLogsMaxChars);
        logs.Should().StartWith("exit 3\n").And.EndWith("THE-END");
    }

    // ── shared environment ───────────────────────────────────────────────────────────────

    private static AgenticsRunnerRegistration Registration() => new()
    {
        Id = "r1",
        Name = "runner",
        Token = "run'tok",
        Owner = "acme",
        Project = "line",
    };

    [Fact]
    public void JobAgenticsExportLines_PinTheStartShLines()
    {
        var lines = AgenticsRunnerRunCommand.BuildJobAgenticsExportLines(
            Registration(), "job-123", "jt", "172.17.0.1:3000", operatorServerUrl: null,
            new Uri("https://agentics.example:8443/"));

        lines.Should().Equal(
            "export AGENTICS_SERVER=172.17.0.1:3000",
            "export AGENTIC_SERVER=172.17.0.1:3000",
            "export AGENTICS_PROJECT=acme/line",
            "export AGENTICS_JOB_ID=job-123",
            "export PKS_TOKEN='jt'",
            "export AGENTICS_TOKEN='run'\\''tok'",
            "export AGENTICS_OWNER='acme'",
            "export AGENTICS_PROJECT_NAME='line'",
            "export AGENTICS_BASE_URL='https://agentics.example:8443'");
    }

    [Fact]
    public void JobAgenticsExportLines_OmitPksToken_WhenNoJobTokenWasMinted()
    {
        var lines = AgenticsRunnerRunCommand.BuildJobAgenticsExportLines(
            Registration(), "job-123", null, "h:1", null, new Uri("https://agentics.example/"));

        lines.Should().NotContain(l => l.StartsWith("export PKS_TOKEN"));
        lines.Should().Contain("export AGENTICS_BASE_URL='https://agentics.example'");
    }

    [Fact]
    public void CodeStepScript_ExportsEnv_CdsIntoCheckout_AndRunsCommandInLoginShell()
    {
        var script = AgenticsRunnerRunCommand.BuildCodeStepScript(
            new[] { "export AGENTICS_JOB_ID=j" }, "/workspaces/line", "echo 'hi' && ./watch.sh");

        script.Should().StartWith("#!/bin/bash\n");
        script.Should().NotContain("echo $$");
        script.Should().Contain("export AGENTICS_JOB_ID=j\n");
        script.Should().Contain("cd '/workspaces/line' || exit 1\n");
        script.Should().EndWith("exec bash -lc 'echo '\\''hi'\\'' && ./watch.sh'\n");
        script.IndexOf("export AGENTICS_JOB_ID").Should().BeLessThan(script.IndexOf("cd '/workspaces/line'"));
    }

    [Fact]
    public void CodeStepScript_RecordsItsPid_BeforeExec()
    {
        var script = AgenticsRunnerRunCommand.BuildCodeStepScript(
            Array.Empty<string>(), "/workspaces/line", "true", pidFile: "/home/node/.code-step-jobs/j/pid");

        script.Should().Contain("echo $$ > '/home/node/.code-step-jobs/j/pid'\n");
        script.IndexOf("echo $$").Should().BeLessThan(script.IndexOf("exec bash -lc"));
    }
}
