using PKS.CLI.Tests.Security;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PKS.Commands.Agentics.Runner;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Commands.Agentics;

/// <summary>
/// The runner side of the embedded ALP server (the sandbox controller as its own Assembly Line
/// Protocol server). Each group pins one clause of the cross-repo contract AND that the same code
/// is byte-for-byte unchanged when the embedded fields are absent, which is every agentics.dk job.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AgenticsRunnerEmbeddedAlpTests
{
    private static readonly JsonSerializerOptions JobJson = new() { PropertyNameCaseInsensitive = true };

    // ── (a) Server with a scheme in the runner config ─────────────────────────────────────────

    [Theory]
    [InlineData("http://172.17.0.1:8080", "http://172.17.0.1:8080")]
    [InlineData("https://agents.example.test", "https://agents.example.test")]
    [InlineData("https://agentics.dk/", "https://agentics.dk/")] // kept exactly as written
    [InlineData("172.17.0.1:8080", "https://172.17.0.1:8080")]
    [InlineData("localhost:3000", "http://localhost:3000")]
    [InlineData("agentics.dk", "https://agentics.dk")]
    [InlineData("", "")]
    public void Registration_server_gets_a_scheme_only_when_it_has_none(string stored, string expected)
    {
        var registration = new AgenticsRunnerRegistration { Server = stored };
        registration.Server.Should().Be(expected);
    }

    [Fact]
    public async Task A_config_preseeded_in_PascalCase_with_an_http_server_binds_and_drives_the_api_base()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pks-embedded-alp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "agentics-runners.json");
            await File.WriteAllTextAsync(path, """
                {
                  "Registrations": [
                    { "Id": "r-1", "Name": "sandbox", "Token": "rnt_abc", "Owner": "acme",
                      "Project": "widgets", "Server": "http://172.17.0.1:8080" }
                  ]
                }
                """);
            var service = new AgenticsRunnerConfigurationService(
                NullLogger<AgenticsRunnerConfigurationService>.Instance, path);

            var registrations = await service.ListRegistrationsAsync();

            registrations.Should().ContainSingle();
            var r = registrations[0];
            r.Token.Should().Be("rnt_abc");
            r.Server.Should().Be("http://172.17.0.1:8080");
            r.ApiBase.Should().Be("http://172.17.0.1:8080");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task The_registrar_returns_a_preseeded_registration_without_registering_and_keeps_its_scheme()
    {
        var config = new Mock<IAgenticsRunnerConfigurationService>();
        config.Setup(c => c.ListRegistrationsAsync()).ReturnsAsync(new List<AgenticsRunnerRegistration>
        {
            new() { Id = "r-1", Token = "rnt_abc", Owner = "acme", Project = "widgets", Server = "http://172.17.0.1:8080" },
        });

        var registration = await RunnerRegistrar.ResolveOrRegisterAsync(config.Object, "acme/widgets", serverOverride: null);

        registration.Server.Should().Be("http://172.17.0.1:8080");
        config.Verify(c => c.AddRegistrationAsync(It.IsAny<AgenticsRunnerRegistration>()), Times.Never);
    }

    [Fact]
    public async Task The_poll_goes_to_the_http_server_with_the_runner_bearer()
    {
        var registration = new AgenticsRunnerRegistration
        {
            Id = "r-1",
            Name = "sandbox",
            Token = "rnt_abc",
            Owner = "acme",
            Project = "widgets",
            Server = "http://172.17.0.1:8080",
        };
        var seen = new List<(string Url, string? Auth)>();
        var handler = new StubHandler(req =>
        {
            seen.Add((req.RequestUri!.ToString(), req.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"jobs":[]}""", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var command = MakeCommand(handler);

        var processed = await command.PollAndDispatchOnceAsync(
            registration, new AgenticsRunnerRunCommand.Settings { InProcess = false, PollingInterval = 10 },
            credentialServer: null, chatLlmBackendUrl: null, chatLlmBackendKey: null,
            chatLlmModelId: null, chatLlmVerbose: false, ct: CancellationToken.None);

        processed.Should().Be(0);
        seen.Should().Contain(("http://172.17.0.1:8080/api/owners/acme/projects/widgets/runners/jobs", "Bearer rnt_abc"));
        seen.Select(s => s.Url).Should().OnlyContain(u => u.StartsWith("http://172.17.0.1:8080/"));
    }

    // ── (b) operatorConfig.broadcast / serverUrl → start.sh ───────────────────────────────────

    [Fact]
    public void Operator_config_binds_broadcast_and_serverUrl_from_the_wire_shape()
    {
        var config = JsonSerializer.Deserialize<AgenticsRunnerRunCommand.RunnerOperatorConfig>(
            """{"agent":"codex","broadcast":"off","serverUrl":"http://172.17.0.1:8080"}""", JobJson)!;

        config.Agent.Should().Be("codex");
        config.Broadcast.Should().Be("off");
        config.ServerUrl.Should().Be("http://172.17.0.1:8080");
        AgenticsRunnerRunCommand.IsEmbeddedOperatorConfig(config).Should().BeTrue();
    }

    [Fact]
    public void Without_serverUrl_the_server_exports_are_exactly_the_old_lines()
    {
        AgenticsRunnerRunCommand.BuildAgenticsServerExportLines("agentics.dk:443", null)
            .Should().Equal("export AGENTICS_SERVER=agentics.dk:443", "export AGENTIC_SERVER=agentics.dk:443");
    }

    [Fact]
    public void ServerUrl_is_exported_as_written_less_trailing_slashes_and_quoted()
    {
        AgenticsRunnerRunCommand.BuildAgenticsServerExportLines("172.17.0.1:8080", "http://172.17.0.1:8080/")
            .Should().Equal("export AGENTICS_SERVER='http://172.17.0.1:8080'", "export AGENTIC_SERVER='http://172.17.0.1:8080'");
        AgenticsRunnerRunCommand.ResolveContainerAgenticsServer("x:1", "https://agents.example.test")
            .Should().Be("https://agents.example.test");
        AgenticsRunnerRunCommand.ContainerReachCheckBase("https", "x:1", "http://172.17.0.1:8080")
            .Should().Be("http://172.17.0.1:8080");
        AgenticsRunnerRunCommand.ContainerReachCheckBase("https", "agentics.dk:443", null)
            .Should().Be("https://agentics.dk:443");
    }

    [Theory]
    [InlineData("172.17.0.1:8080")]          // bare: not a URL with scheme
    [InlineData("ftp://host/")]
    [InlineData("http://host'; rm -rf /'")]  // not a valid absolute URL
    [InlineData("http://a:8080/x';touch /tmp/p;'")] // valid to Uri.TryCreate; closes the bash -c quote
    [InlineData("http://a:8080/$(id)")]
    [InlineData("http://a:8080/`id`")]
    [InlineData("http://a:8080/x y")]
    [InlineData("http://a:8080/x;id")]
    [InlineData("http://a:8080/x?q=1")]
    [InlineData("http://a:8080/x#frag")]
    [InlineData("http://user:pw@a:8080")]
    [InlineData("http://a:8080/%27")]
    [InlineData("http://a\"b:8080")]
    [InlineData("   ")]
    public void An_invalid_serverUrl_falls_back_to_the_derived_address(string serverUrl)
    {
        AgenticsRunnerRunCommand.BuildAgenticsServerExportLines("agentics.dk:443", serverUrl)
            .Should().Equal("export AGENTICS_SERVER=agentics.dk:443", "export AGENTIC_SERVER=agentics.dk:443");
    }

    [Theory]
    [InlineData("http://172.17.0.1:8080/", "http://172.17.0.1:8080")]
    [InlineData("https://agents.example.test", "https://agents.example.test")]
    [InlineData("https://agents.example.test/alp//", "https://agents.example.test/alp")] // path prefix: vibecast's BasePath
    [InlineData("http://[::1]:8080", "http://[::1]:8080")]
    [InlineData("http://sandbox_controller:8080", "http://sandbox_controller:8080")] // compose-style name
    [InlineData("HTTP://Sandbox-1.example.test:80", "HTTP://Sandbox-1.example.test:80")] // as written
    public void A_valid_serverUrl_is_passed_on_as_written_less_trailing_slashes(string serverUrl, string expected)
    {
        AgenticsRunnerRunCommand.ValidOperatorServerUrl(serverUrl).Should().Be(expected);
        AgenticsRunnerRunCommand.RejectedOperatorServerUrlWarning(serverUrl, "x:1").Should().BeNull();
    }

    [Fact]
    public void A_rejected_serverUrl_is_warned_about_and_an_absent_one_is_not()
    {
        AgenticsRunnerRunCommand.RejectedOperatorServerUrlWarning(null, "agentics.dk:443").Should().BeNull();
        AgenticsRunnerRunCommand.RejectedOperatorServerUrlWarning("  ", "agentics.dk:443").Should().BeNull();
        var warning = AgenticsRunnerRunCommand.RejectedOperatorServerUrlWarning("172.17.0.1:8080", "172.17.0.1:8080");
        warning.Should().Contain("172.17.0.1:8080").And.Contain("ignoring");
    }

    [Theory]
    [InlineData("http://a:8080/x';touch /tmp/p;'")]
    [InlineData("http://a:8080/$(id)")]
    public void A_hostile_serverUrl_never_reaches_the_reachability_check(string serverUrl)
    {
        AgenticsRunnerRunCommand.ContainerReachCheckBase("https", "agentics.dk:443", serverUrl)
            .Should().Be("https://agentics.dk:443");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("on", false)]
    [InlineData("ON", false)]
    [InlineData("bogus", false)]
    [InlineData("off", true)]
    [InlineData("Off", true)]
    public void Broadcast_off_and_only_off_exports_VIBECAST_BROADCAST(string? broadcast, bool exported)
    {
        var lines = AgenticsRunnerRunCommand.BuildBroadcastExportLines(
            new AgenticsRunnerRunCommand.RunnerOperatorConfig { Broadcast = broadcast });

        if (exported) lines.Should().Equal("export VIBECAST_BROADCAST=off");
        else lines.Should().BeEmpty();
        AgenticsRunnerRunCommand.BuildBroadcastExportLines(null).Should().BeEmpty();
    }

    [Fact]
    public void An_agentics_dk_operator_config_is_not_embedded()
    {
        AgenticsRunnerRunCommand.IsEmbeddedOperatorConfig(null).Should().BeFalse();
        AgenticsRunnerRunCommand.IsEmbeddedOperatorConfig(new AgenticsRunnerRunCommand.RunnerOperatorConfig
        {
            Agent = "claude",
            Model = "opus",
            Effort = "high",
            AutoApproveImageUploads = true,
        }).Should().BeFalse();
    }

    [Fact]
    public void The_runner_token_reaches_vibecast_as_AGENTICS_TOKEN()
    {
        // start.sh is generated inline in ExecuteSpawnModeAsync; this pins the export at source
        // level so a refactor that drops it fails here rather than at the controller's 401.
        var source = File.ReadAllText(FindSource("src/Commands/Agentics/Runner/AgenticsRunnerRunCommand.cs"));
        // start.sh takes its AGENTICS_* exports from the helper it shares with code_step jobs.
        source.Should().Contain("foreach (var line in BuildJobAgenticsExportLines(registration, job.Id, jobToken,");
        AgenticsRunnerRunCommand.BuildJobAgenticsExportLines(
                new PKS.Infrastructure.Services.Models.AgenticsRunnerRegistration { Token = "rnt_a'b", Owner = "o", Project = "p" },
                "job", null, "h:1", null, new Uri("https://x.example/"))
            .Should().Contain("export AGENTICS_TOKEN='rnt_a'\\''b'");
        source.Should().Contain("AGENTICS_TOKEN={ShellSingleQuote(registration.Token)} AGENTICS_OWNER"); // in-process env line
    }

    [Theory]
    [InlineData("rnt_abc123", "'rnt_abc123'")] // a normal token: the same bytes as before
    [InlineData("rnt_a'b", "'rnt_a'\\''b'")]
    public void The_runner_token_is_shell_quoted(string token, string expected)
    {
        AgenticsRunnerRunCommand.ShellSingleQuote(token).Should().Be(expected);
    }

    [Theory]
    [InlineData("cancelled", null, "cancelled")]
    [InlineData("cancelled", "agent pane never started", "cancelled")]
    [InlineData("idle_failure", "agent idled on a prompt", "failure")]
    [InlineData("session_ended", null, "success")]
    [InlineData("max_timeout", null, "success")] // unchanged from before: the controller still needs a session end
    public void Embedded_conclusion_has_a_cancelled_branch(string reason, string? failure, string expected)
    {
        AgenticsRunnerRunCommand.EmbeddedSpawnConclusion(reason, failure).Should().Be(expected);
    }

    // ── (c) spawn-mode completion PATCH ───────────────────────────────────────────────────────

    [Fact]
    public void Embedded_completion_body_keeps_status_and_conclusion_and_adds_the_rest()
    {
        var json = AgenticsRunnerRunCommand.BuildEmbeddedSpawnCompletionBody(
            "success", "session_ended", "sess-1", "bc-1", "vibecast.log (tail):\nbye\n");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("status").GetString().Should().Be("completed");
        root.GetProperty("conclusion").GetString().Should().Be("success");
        root.GetProperty("completionReason").GetString().Should().Be("session_ended");
        root.GetProperty("sessionId").GetString().Should().Be("sess-1");
        root.GetProperty("broadcastId").GetString().Should().Be("bc-1");
        root.GetProperty("logs").GetString().Should().Contain("bye");
        root.TryGetProperty("result", out _).Should().BeFalse("the runner never holds the agent's answer");
    }

    [Fact]
    public void Embedded_completion_body_omits_what_it_does_not_know()
    {
        var json = AgenticsRunnerRunCommand.BuildEmbeddedSpawnCompletionBody("failure", null, null, null, null);
        json.Should().Be("""{"status":"completed","conclusion":"failure"}""");
    }

    [Fact]
    public void Completion_logs_carry_the_failure_the_signal_file_and_a_bounded_log_tail()
    {
        AgenticsRunnerRunCommand.ComposeEmbeddedCompletionLogs(null, null, "  ").Should().BeNull();

        var logs = AgenticsRunnerRunCommand.ComposeEmbeddedCompletionLogs(
            "agent pane never started", "the answer is 42", "line1\nline2\n")!;
        logs.Should().StartWith("failure: agent pane never started\n");
        logs.Should().Contain("task-complete:\nthe answer is 42\n");
        logs.Should().EndWith("vibecast.log (tail):\nline1\nline2\n");

        var huge = new string('x', 50_000) + "THE-END";
        var bounded = AgenticsRunnerRunCommand.ComposeEmbeddedCompletionLogs(null, null, huge)!;
        bounded.Length.Should().BeLessThanOrEqualTo(AgenticsRunnerRunCommand.EmbeddedCompletionLogsMaxChars + 2);
        bounded.Should().EndWith("THE-END\n");
    }

    // ── (d) commit-back with an installation token, no device login ──────────────────────────

    [Fact]
    public void A_commit_back_token_without_a_kind_is_exported_so_vibecast_uses_it()
    {
        var lines = AgenticsRunnerRunCommand.BuildCommitBackExportLines(new AgenticsRunnerRunCommand.CommitBackRepositoryDefinition
        {
            Url = "https://github.com/acme/widgets.git",
            Token = "ghs_installation",
        });

        lines.Should().Equal(
            "export AGENTICS_COMMIT_BACK_URL='https://github.com/acme/widgets.git'",
            "export AGENTICS_COMMIT_BACK_CREDENTIAL_KIND='agentics-repo'",
            "export AGENTICS_COMMIT_BACK_TOKEN='ghs_installation'");
    }

    [Theory]
    [InlineData("runner", null, "runner")]
    [InlineData("agentics-repo", "tok", "agentics-repo")]
    [InlineData("runner", "tok", "runner")]
    [InlineData(null, null, null)]
    [InlineData("", null, "")]
    public void The_kinds_agentics_dk_sends_are_exported_unchanged(string? kind, string? token, string? expected)
    {
        AgenticsRunnerRunCommand.EffectiveCommitBackCredentialKind(kind, token).Should().Be(expected);
    }

    [Fact]
    public void A_clone_url_that_already_carries_an_installation_token_is_used_as_is_without_a_stored_login()
    {
        var url = "https://x-access-token:ghs_installation@github.com/acme/widgets.git";

        // SecretValue.None is what ResolveJobGitTokenAsync returns when nobody ran device login.
        GitCloneUrl.ForRepository(url, SecretValue.None).Reveal().Should().Be(url);
        GitCloneUrl.WithoutCredentials(url).Should().Be("https://github.com/acme/widgets.git");
    }

    [Theory]
    [InlineData(false, true, false, false, false)] // embedded controller: requiresGitHub:false
    [InlineData(false, true, true, true, false)]   // GitHub App mode
    [InlineData(true, true, true, false, false)]   // --inprocess
    [InlineData(false, false, true, false, false)] // no docker
    [InlineData(false, true, true, false, true)]   // agentics.dk GitHub project: unchanged
    public void Device_login_runs_only_for_a_spawn_runner_on_a_github_project_without_an_app(
        bool inProcess, bool spawn, bool requiresGitHub, bool app, bool expected)
    {
        AgenticsRunnerRunCommand.NeedsOperatorGitHubLogin(inProcess, spawn, requiresGitHub, app).Should().Be(expected);
    }

    [Fact]
    public async Task Repo_info_requiresGitHub_false_is_read_as_false()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"requiresGitHub":false}""", System.Text.Encoding.UTF8, "application/json"),
        });
        var command = MakeCommand(handler);

        var info = await command.ProjectRepoInfoAsync(new AgenticsRunnerRegistration
        {
            Token = "rnt_abc",
            Owner = "acme",
            Project = "widgets",
            Server = "http://172.17.0.1:8080",
        });

        info.RequiresGitHub.Should().BeFalse();
        info.GitHubApp.Should().BeNull();
        AgenticsRunnerRunCommand.NeedsOperatorGitHubLogin(false, true, info.RequiresGitHub, false).Should().BeFalse();
    }

    [Fact]
    public async Task Repo_info_failing_falls_back_to_requiring_github()
    {
        // Documents the existing safe default: the controller MUST answer repo-info with 200, or a
        // spawn-mode runner without a stored GitHub token goes into device-code login.
        var command = MakeCommand(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var info = await command.ProjectRepoInfoAsync(new AgenticsRunnerRegistration
        {
            Token = "rnt_abc",
            Owner = "acme",
            Project = "widgets",
            Server = "http://172.17.0.1:8080",
        });

        info.RequiresGitHub.Should().BeTrue();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static AgenticsRunnerRunCommand MakeCommand(HttpMessageHandler handler)
    {
        var probe = new Mock<IRunnerExecutionCapabilityProbe>();
        probe.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerExecutionCapabilityStatus(false, "Docker is not available"));
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var foundryAuthService = new Mock<IAzureFoundryAuthService>();
        var chatProviderFactory = new AgentChatProviderFactory(
            new Mock<IConfigurationService>().Object, new HttpClient(), FakeSecretResolver.Empty, foundryAuthService.Object);
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Capabilities.Interactive = false;

        return new AgenticsRunnerRunCommand(
            new Mock<IAgenticsRunnerConfigurationService>().Object,
            new Mock<IDevcontainerSpawnerService>().Object,
            httpClientFactory.Object,
            new Mock<IGitHubAuthenticationService>().Object,
            foundryAuthService.Object,
            new AzureFoundryAuthConfig(),
            chatProviderFactory,
            console,
            probe.Object);
    }

    private static string FindSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir == null ? throw new FileNotFoundException(relative) : Path.Combine(dir.FullName, relative);
    }
}
