using System.Net;
using System.Net.Http;
using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Security;
using PKS.Commands.Agentics.Runner;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Acs;
using PKS.Infrastructure.Services.Agent;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// The runner's <c>sms</c> capability and delivery loop. The capability is honest — advertised only
/// while <see cref="IAcsSmsService.IsConfiguredAsync"/> says the host can send right now — and the
/// deliveries are drained by <see cref="AgenticsRunnerRunCommand.DrainDeliveriesOnceAsync"/>: GET
/// the queue, send each, POST the outcome back, never log the text.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class RunnerSmsCapabilityTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string Body)> Requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => Json("{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            return Respond(request);
        }

        public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private static AgenticsRunnerRegistration MakeRegistration() => new()
    {
        Id = "runner-1", Name = "test-runner", Token = "rnt_tok", Owner = "acme", Project = "widgets",
        Server = "https://agentics.test", RegisteredAt = DateTime.UtcNow,
    };

    private static (AgenticsRunnerRunCommand Command, Spectre.Console.Testing.TestConsole Console) CreateCommand(
        IAcsSmsService? sms, StubHandler? handler = null)
    {
        var probe = new Mock<IRunnerExecutionCapabilityProbe>();
        probe.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerExecutionCapabilityStatus(false, "no docker in tests"));

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(x => x.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler ?? new StubHandler(), disposeHandler: false));

        var foundryAuthService = new Mock<IAzureFoundryAuthService>();
        var chatProviderFactory = new AgentChatProviderFactory(
            new Mock<IConfigurationService>().Object, new HttpClient(), FakeSecretResolver.Empty, foundryAuthService.Object);
        var console = new Spectre.Console.Testing.TestConsole();
        console.Profile.Capabilities.Interactive = false;

        var command = new AgenticsRunnerRunCommand(
            new Mock<IAgenticsRunnerConfigurationService>().Object,
            new Mock<IDevcontainerSpawnerService>().Object,
            httpClientFactory.Object,
            new Mock<IGitHubAuthenticationService>().Object,
            foundryAuthService.Object,
            new AzureFoundryAuthConfig(),
            chatProviderFactory,
            console,
            probe.Object,
            acsSms: sms);
        return (command, console);
    }

    private static Task<List<string>> CapabilitiesAsync(AgenticsRunnerRunCommand command)
        => command.ComputeCapabilitiesAsync(inProcess: false, chatLlmBackendUrl: "http://localhost:11434/v1", chatLlmModelId: "gpt-5.5", ct: CancellationToken.None);

    [Fact]
    public async Task Capability_AdvertisedOnlyWhenTheServiceIsConfigured()
    {
        var sms = new Mock<IAcsSmsService>();
        sms.Setup(s => s.IsConfiguredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var (command, _) = CreateCommand(sms.Object);

        (await CapabilitiesAsync(command)).Should().NotContain("sms");

        sms.Setup(s => s.IsConfiguredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        (await CapabilitiesAsync(command)).Should().Contain("sms");
    }

    [Fact]
    public async Task Capability_AbsentWithoutTheService()
    {
        var (command, _) = CreateCommand(sms: null);
        (await CapabilitiesAsync(command)).Should().NotContain("sms");
    }

    [Fact]
    public async Task Capability_OperatorProfileCanNarrowItAway()
    {
        var sms = new Mock<IAcsSmsService>();
        sms.Setup(s => s.IsConfiguredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var (command, _) = CreateCommand(sms.Object);

        var caps = await command.ComputeCapabilitiesAsync(
            inProcess: false, chatLlmBackendUrl: "http://localhost:11434/v1", chatLlmModelId: "gpt-5.5", ct: CancellationToken.None,
            capabilityOverride: new List<string> { "chat-llm:v1" });

        caps.Should().NotContain("sms");
    }

    [Fact]
    public async Task Drain_SendsEachQueuedDelivery_AndAcksTheOutcome()
    {
        var registration = MakeRegistration();
        var baseUrl = $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}";
        var handler = new StubHandler
        {
            Respond = req => req.Method == HttpMethod.Get
                ? StubHandler.Json("""{"deliveries":[{"id":"d1","channel":"sms","message":"MitID-kode 1234","tag":"job-1-kode","jobId":"job-1"},{"id":"d2","channel":"sms","message":"second"}]}""")
                : StubHandler.Json("""{"ok":true}"""),
        };
        var sms = new Mock<IAcsSmsService>();
        sms.Setup(s => s.IsConfiguredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        sms.Setup(s => s.SendAsync("MitID-kode 1234", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmsSendResult(true, "msg-1", null, false));
        sms.Setup(s => s.SendAsync("second", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SmsSendResult.Fail("ACS returned 403"));
        var (command, console) = CreateCommand(sms.Object, handler);

        var attempted = await command.DrainDeliveriesOnceAsync(registration, CancellationToken.None);

        attempted.Should().Be(2);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Url.Should().Be($"{baseUrl}/runners/deliveries");

        var ack1 = handler.Requests[1];
        ack1.Method.Should().Be(HttpMethod.Post);
        ack1.Url.Should().Be($"{baseUrl}/runners/deliveries/d1");
        ack1.Body.Should().Contain("\"status\":\"sent\"").And.Contain("msg-1");

        var ack2 = handler.Requests[2];
        ack2.Url.Should().Be($"{baseUrl}/runners/deliveries/d2");
        ack2.Body.Should().Contain("\"status\":\"failed\"").And.Contain("403");

        // The text is a one-time code more often than not: never on the console.
        console.Output.Should().Contain("d1").And.NotContain("1234");
    }

    [Fact]
    public async Task Drain_UnknownChannel_IsAckedAsFailed_WithoutSending()
    {
        var handler = new StubHandler
        {
            Respond = req => req.Method == HttpMethod.Get
                ? StubHandler.Json("""{"deliveries":[{"id":"d9","channel":"pigeon","message":"coo"}]}""")
                : StubHandler.Json("{}"),
        };
        var sms = new Mock<IAcsSmsService>(MockBehavior.Strict);
        sms.Setup(s => s.IsConfiguredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var (command, _) = CreateCommand(sms.Object, handler);

        await command.DrainDeliveriesOnceAsync(MakeRegistration(), CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Body.Should().Contain("\"status\":\"failed\"").And.Contain("pigeon");
        sms.Verify(s => s.SendAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Drain_PlatformWithoutTheQueue_IsQuiet()
    {
        var handler = new StubHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var sms = new Mock<IAcsSmsService>();
        var (command, console) = CreateCommand(sms.Object, handler);

        var attempted = await command.DrainDeliveriesOnceAsync(MakeRegistration(), CancellationToken.None);

        attempted.Should().Be(0);
        handler.Requests.Should().ContainSingle();
        console.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task Loop_StopsOnCancellation()
    {
        var sms = new Mock<IAcsSmsService>();
        sms.Setup(s => s.IsConfiguredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var (command, _) = CreateCommand(sms.Object);
        using var cts = new CancellationTokenSource();

        var loop = command.RunDeliveryLoopAsync(MakeRegistration(), cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var finished = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().BeSameAs(loop, "the loop must honour the runner's cancellation token");
        await loop;
    }
}
