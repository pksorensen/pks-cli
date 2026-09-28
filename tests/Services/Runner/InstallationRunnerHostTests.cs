using System.Net;
using System.Text;
using FluentAssertions;
using Moq;
using PKS.Commands.Agentics.Runner;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// The host runner on a self-hosted box: its identity is the installation key (no control token),
/// its own traffic stays on the internal URL, and what it hands jobs is the public one.
/// </summary>
public class InstallationRunnerHostTests
{
    // Shared with www-site's src/lib/__tests__/installation-jwt.test.ts: a throwaway key and the JWT
    // it must produce. Ed25519 is deterministic, so both sides agree byte for byte or not at all.
    private const string VectorKeyPem = """
        -----BEGIN PRIVATE KEY-----
        MC4CAQAwBQYDK2VwBCIEIEmQRKR2fb1ea1xsWPuy6qea0/MjixJlHaJQJ2CHxRx3
        -----END PRIVATE KEY-----

        """;
    private const string VectorJwt =
        "eyJhbGciOiJFZERTQSIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJhZ2VudGljcy1pbnN0YWxsYXRpb24iLCJzdWIiOiJzaC12ZWN0b3IiLCJhdWQiOiJhcHAuZXhhbXBsZS50ZXN0IiwiaWF0IjoxNzkwMDAwMDAwLCJleHAiOjE3OTAwMDAzMDB9.umPPT-pNXVFVFLAVtN26i0QdcJdHMz_NuaeOuNYAI10k5Fqsayl2SmkAZhwDi6udLSH5Xkqir9y3AatjUaSBAQ";

    private static readonly InstallationContext Box =
        new("sh-vector", "/opt/agentics/identity/installation.key", "https://app.example.test", "http://127.0.0.1:3000");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Signer_ProducesTheSharedVector_ThatThePlatformVerifies()
    {
        var keyPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(keyPath, VectorKeyPem);
            var signer = new OpenSslInstallationTokenSigner(() => DateTimeOffset.FromUnixTimeSeconds(1_790_000_000));

            var jwt = await signer.MintAsync(Box with { KeyPath = keyPath }, Box.Audience);

            jwt.Should().Be(VectorJwt);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Context_ComesFromTheInstallerEnvironment_AndNeedsIdKeyAndServer()
    {
        var env = new Dictionary<string, string?>
        {
            ["AGENTICS_INSTALLATION_ID"] = "sh-vector",
            ["AGENTICS_INSTALLATION_KEY"] = "/opt/agentics/identity/installation.key",
            ["AGENTICS_SERVER"] = "app.example.test",
            ["AGENTICS_INTERNAL_SERVER"] = "http://127.0.0.1:3000/",
        };

        var context = InstallationContext.FromEnvironment(k => env.GetValueOrDefault(k));

        context.Should().Be(Box);
        context!.Audience.Should().Be("app.example.test");
        context.ApiBase.Should().Be("http://127.0.0.1:3000");

        env.Remove("AGENTICS_INSTALLATION_KEY");
        InstallationContext.FromEnvironment(k => env.GetValueOrDefault(k)).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ControlClient_CallsTheInternalUrl_WithAnInstallationJwtForThePublicHost_AndRegistersWithBoth()
    {
        var signer = new Mock<IInstallationTokenSigner>();
        signer.Setup(s => s.MintAsync(Box, "app.example.test", It.IsAny<CancellationToken>())).ReturnsAsync("signed.jwt");
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? """{"projects":[{"owner":"acme","project":"web","modelPolicy":{"allowedModels":["luna"],"defaultModel":"luna"}}]}"""
            : """{"id":"r1","name":"Host runner (sh-vector)","token":"runner-token","owner":"acme","project":"web"}""");
        var client = new ManagedRunnerControlClient(new HttpClient(handler), signer.Object);

        var projects = await client.ListProjectsAsync(Box, default);
        var registration = await client.BootstrapAsync(Box, "acme", "web", default);

        projects.Should().ContainSingle().Which.ModelPolicy!.AllowedModels.Should().Equal("luna");
        handler.Requests.Should().AllSatisfy(r =>
        {
            r.Uri.Should().Be("http://127.0.0.1:3000/api/internal/managed-runners");
            r.Authorization.Should().Be("Bearer signed.jwt");
        });
        handler.Requests[1].Body.Should().Be("""{"owner":"acme","project":"web"}""");
        registration.Should().BeEquivalentTo(new
        {
            Id = "r1",
            Token = "runner-token",
            Server = "https://app.example.test",
            InternalServer = "http://127.0.0.1:3000",
            ApiBase = "http://127.0.0.1:3000",
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Reconcile_RegistersNewProjects_StopsGoneOnes_AndLeavesOtherServersAlone()
    {
        var control = new Mock<IManagedRunnerControlClient>();
        control.Setup(c => c.ListProjectsAsync(Box, It.IsAny<CancellationToken>())).ReturnsAsync(
            [new("acme", "web", null), new("acme", "mine-elsewhere", null)]);
        control.Setup(c => c.BootstrapAsync(Box, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstallationContext _, string o, string p, CancellationToken _) =>
                new AgenticsRunnerRegistration { Id = "r-" + p, Token = "t", Owner = o, Project = p, Server = Box.Server, InternalServer = Box.InternalServer });
        var registrations = new InMemoryRegistrations(
            new AgenticsRunnerRegistration { Id = "dev", Token = "t", Owner = "acme", Project = "mine-elsewhere", Server = "https://agentics.dk" });
        var launcher = new FakeLauncher();
        var host = new InstallationRunnerHost(Box, control.Object, registrations, launcher, _ => { });

        await host.ReconcileOnceAsync(default);

        launcher.Started.Should().ContainSingle().Which.Project.Should().Be("web");
        registrations.Items.Should().Contain(r => r.Project == "web" && r.InternalServer == "http://127.0.0.1:3000");
        registrations.Items.Single(r => r.Project == "mine-elsewhere").Server.Should().Be("https://agentics.dk");

        // The project is deleted on the platform: its runner stops.
        control.Setup(c => c.ListProjectsAsync(Box, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        await host.ReconcileOnceAsync(default);

        launcher.Processes.Single().Stopped.Should().BeTrue();
        host.RunningProjects.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Reconcile_ReRegistersARunnerThatCrashed_ButNotOneThatIsStillRunning()
    {
        var control = new Mock<IManagedRunnerControlClient>();
        control.Setup(c => c.ListProjectsAsync(Box, It.IsAny<CancellationToken>())).ReturnsAsync([new("acme", "web", null)]);
        var bootstraps = 0;
        control.Setup(c => c.BootstrapAsync(Box, "acme", "web", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AgenticsRunnerRegistration
            {
                Id = "r1", Token = $"t{++bootstraps}", Owner = "acme", Project = "web", Server = Box.Server, InternalServer = Box.InternalServer,
            });
        var launcher = new FakeLauncher();
        var host = new InstallationRunnerHost(Box, control.Object, new InMemoryRegistrations(), launcher, _ => { });

        await host.ReconcileOnceAsync(default);
        await host.ReconcileOnceAsync(default);
        bootstraps.Should().Be(1);
        launcher.Started.Should().HaveCount(1);

        launcher.Processes[0].Exit(1);
        await host.ReconcileOnceAsync(default);

        bootstraps.Should().Be(2);
        launcher.Started.Should().HaveCount(2);
        launcher.Started[1].Token.Should().Be("t2");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ServiceUnit_RunsTheSameBuild_WithTheInstallerEnvironment()
    {
        var unit = InstallationRunnerMode.BuildUnit(
            new RunnerLauncherCommand(RunnerLauncherKind.Self, "/usr/local/bin/pks"), "root", "/root", withEnvironmentFile: true);

        unit.Should().Contain("ExecStart=/usr/local/bin/pks --no-logo agentics runner run\n");
        unit.Should().Contain("EnvironmentFile=/etc/agentics/runner.env\n");
        unit.Should().Contain("Environment=HOME=/root\n");
        unit.Should().Contain("Restart=always");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("ActiveState=active\nNRestarts=0\n", true)]
    [InlineData("ActiveState=activating\nNRestarts=3\n", false)] // crash-looping on startup
    [InlineData("ActiveState=active\nNRestarts=1\n", false)]     // up, but only after dying once
    [InlineData("ActiveState=failed\nNRestarts=0\n", false)]
    public void ServiceHealth_IsWhatSystemdSaw_NotWhetherRestartReturnedZero(string show, bool healthy)
    {
        InstallationRunnerMode.IsHealthy(show).Should().Be(healthy);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Registration_ApiBase_IsTheInternalServer_WhenThereIsOne()
    {
        new AgenticsRunnerRegistration { Server = "https://app.example.test" }.ApiBase.Should().Be("https://app.example.test");
        new AgenticsRunnerRegistration { Server = "https://app.example.test", InternalServer = "http://127.0.0.1:3000/" }
            .ApiBase.Should().Be("http://127.0.0.1:3000");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class InMemoryRegistrations(params AgenticsRunnerRegistration[] initial) : IAgenticsRunnerConfigurationService
    {
        public List<AgenticsRunnerRegistration> Items { get; } = [.. initial];

        public Task<AgenticsRunnerConfiguration> LoadAsync() => Task.FromResult(new AgenticsRunnerConfiguration { Registrations = Items });
        public Task SaveAsync(AgenticsRunnerConfiguration configuration) => Task.CompletedTask;

        public Task<AgenticsRunnerRegistration> AddRegistrationAsync(AgenticsRunnerRegistration registration)
        {
            Items.RemoveAll(r => r.Owner == registration.Owner && r.Project == registration.Project);
            Items.Add(registration);
            return Task.FromResult(registration);
        }

        public Task<bool> RemoveRegistrationAsync(string registrationId) => Task.FromResult(Items.RemoveAll(r => r.Id == registrationId) > 0);
        public Task<List<AgenticsRunnerRegistration>> ListRegistrationsAsync() => Task.FromResult(Items.ToList());
        public Task<AgenticsRunnerRegistration?> GetRegistrationAsync(string registrationId) => Task.FromResult(Items.FirstOrDefault(r => r.Id == registrationId));
    }

    private sealed class FakeLauncher : IProjectRunnerLauncher
    {
        public List<AgenticsRunnerRegistration> Started { get; } = [];
        public List<FakeProcess> Processes { get; } = [];

        public IProjectRunnerProcess Start(AgenticsRunnerRegistration registration)
        {
            Started.Add(registration);
            var process = new FakeProcess();
            Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess : IProjectRunnerProcess
    {
        public bool HasExited { get; private set; }
        public int? ExitCode { get; private set; }
        public bool Stopped { get; private set; }

        public void Exit(int code) { HasExited = true; ExitCode = code; }

        public Task StopAsync(TimeSpan grace)
        {
            Stopped = true;
            HasExited = true;
            ExitCode = 0;
            return Task.CompletedTask;
        }
    }
}
