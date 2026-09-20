using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Security;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Acs;
using PKS.Infrastructure.Services.Azure;
using Xunit;

namespace PKS.CLI.Tests.Services.Acs;

/// <summary>
/// <see cref="AcsSmsService"/>: the one REST call behind <c>pks acs sms send</c> and the runner's
/// SMS deliveries. Sender = a registry entry of kind CommunicationServices; token = the tenant
/// store for that entry's tenant with the communication.azure.com scope; the recipient defaults to
/// the host's configured one. No SDK, so the request shape is asserted byte by byte.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class AcsSmsServiceTests : IDisposable
{
    private const string Tenant = "11111111-aaaa-aaaa-aaaa-111111111111";
    private const string Endpoint = "com-prd.europe.communication.azure.com";

    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> Bodies = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => Accepted("""{"value":[{"to":"+4512345678","messageId":"msg-1","httpStatusCode":202,"successful":true}]}""");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
            return Respond(request);
        }

        public static HttpResponseMessage Accepted(string json) => new(HttpStatusCode.Accepted)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private readonly string _dir;
    private readonly AzureResourceRegistry _registry;
    private readonly Mock<IAzureTenantCredentialStore> _tenants = new();
    private readonly Mock<IAzureFoundryAuthService> _foundry = new();
    private readonly Dictionary<string, string?> _settings = new();
    private readonly Mock<IConfigurationService> _config = new();
    private readonly StubHandler _handler = new();

    public AcsSmsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"pks-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _config.Setup(m => m.GetAsync(It.IsAny<string>())).ReturnsAsync((string k) => _settings.GetValueOrDefault(k));
        _config.Setup(m => m.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<string, string, bool, bool>((k, v, _, _) => _settings[k] = v).Returns(Task.CompletedTask);
        _registry = new AzureResourceRegistry(_config.Object, FakeSecretResolver.Empty, Path.Combine(_dir, "azure-resources.json"));

        _tenants.Setup(t => t.ListTenantsAsync()).ReturnsAsync(new List<AzureTenantInfo>
        {
            new(Tenant, "Contoso", "user@contoso.com", DateTime.UtcNow, DateTime.UtcNow),
        });
        _tenants.Setup(t => t.ResolveTenantAsync(It.IsAny<string?>())).ReturnsAsync(Tenant);
        _tenants.Setup(t => t.GetAccessTokenAsync(Tenant, AzureArmDiscovery.CommunicationScope, It.IsAny<CancellationToken>()))
            .ReturnsAsync("acs-token");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private AcsSmsService CreateService() => new(_registry, _tenants.Object, _foundry.Object, _config.Object, new HttpClient(_handler));

    private async Task RegisterSenderAsync(string key, bool enabled = true)
    {
        await _registry.UpsertAsync(new[]
        {
            new AzureResourceEntry
            {
                Kind = AzureResourceKind.CommunicationServices, Key = key, Name = key, TenantId = Tenant,
                ResourceId = $"/subscriptions/s/resourceGroups/rg/providers/Microsoft.Communication/communicationServices/com-prd/smsSenders/{key}",
                Endpoint = Endpoint, Enabled = enabled,
            },
        });
    }

    private async Task ConfigureAsync(string from = "+4566339237", string recipient = "+4512345678")
    {
        await RegisterSenderAsync(from);
        _settings[AcsSmsService.FromKey] = from;
        _settings[AcsSmsService.RecipientKey] = recipient;
    }

    [Fact]
    public async Task IsConfigured_RequiresEnabledDefaultSenderAndRecipient()
    {
        var svc = CreateService();
        (await svc.IsConfiguredAsync()).Should().BeFalse("nothing registered");

        await RegisterSenderAsync("+4566339237");
        _settings[AcsSmsService.FromKey] = "+4566339237";
        (await svc.IsConfiguredAsync()).Should().BeFalse("no recipient");

        _settings[AcsSmsService.RecipientKey] = "+4512345678";
        (await svc.IsConfiguredAsync()).Should().BeTrue();

        await _registry.SetEnabledAsync(AzureResourceKind.CommunicationServices, "+4566339237", false);
        (await svc.IsConfiguredAsync()).Should().BeFalse("the default sender was disabled");
    }

    [Fact]
    public async Task Send_PostsToTheSenderEndpoint_WithBearerFromTheCommunicationScope()
    {
        await ConfigureAsync();
        var result = await CreateService().SendAsync("MitID-kode 1234");

        result.Ok.Should().BeTrue(result.Error);
        result.MessageId.Should().Be("msg-1");
        result.Truncated.Should().BeFalse();

        var request = _handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be($"https://{Endpoint}/sms?api-version=2021-03-07");
        request.Headers.Authorization!.Parameter.Should().Be("acs-token");

        using var body = JsonDocument.Parse(_handler.Bodies[0]);
        body.RootElement.GetProperty("from").GetString().Should().Be("+4566339237");
        body.RootElement.GetProperty("smsRecipients")[0].GetProperty("to").GetString().Should().Be("+4512345678");
        body.RootElement.GetProperty("message").GetString().Should().Be("MitID-kode 1234");
        body.RootElement.GetProperty("smsSendOptions").GetProperty("enableDeliveryReport").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Send_ExplicitRecipientAndSender_OverrideDefaults()
    {
        await ConfigureAsync();
        await RegisterSenderAsync("Agentics");

        var result = await CreateService().SendAsync("hej", to: "+4587654321", from: "Agentics");

        result.Ok.Should().BeTrue(result.Error);
        using var body = JsonDocument.Parse(_handler.Bodies[0]);
        body.RootElement.GetProperty("from").GetString().Should().Be("Agentics");
        body.RootElement.GetProperty("smsRecipients")[0].GetProperty("to").GetString().Should().Be("+4587654321");
    }

    [Fact]
    public async Task Send_TruncatesToTwoSegments_AndSaysSo()
    {
        await ConfigureAsync();
        var result = await CreateService().SendAsync(new string('x', 500));

        result.Ok.Should().BeTrue(result.Error);
        result.Truncated.Should().BeTrue();
        using var body = JsonDocument.Parse(_handler.Bodies[0]);
        var sent = body.RootElement.GetProperty("message").GetString()!;
        sent.Length.Should().Be(AcsSmsService.MaxMessageLength);
        sent.Should().EndWith("...", "U+2026 is not GSM-7 and would force UCS-2 encoding — five segments instead of two");
    }

    [Fact]
    public async Task Send_WithoutConfiguration_FailsWithoutCallingAcs()
    {
        var svc = CreateService();

        (await svc.SendAsync("x")).Error.Should().Contain("No recipient");
        _settings[AcsSmsService.RecipientKey] = "+4512345678";
        (await svc.SendAsync("x")).Error.Should().Contain("No sender");
        (await svc.SendAsync("x", to: "12345")).Error.Should().Contain("E.164");

        await RegisterSenderAsync("+4566339237", enabled: false);
        _settings[AcsSmsService.FromKey] = "+4566339237";
        (await svc.SendAsync("x")).Error.Should().Contain("disabled");

        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_AcceptedButUnsuccessfulItem_IsAFailure()
    {
        await ConfigureAsync();
        _handler.Respond = _ => StubHandler.Accepted("""{"value":[{"to":"+4512345678","httpStatusCode":400,"successful":false,"errorMessage":"Invalid To phone number"}]}""");

        var result = await CreateService().SendAsync("x");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("Invalid To phone number");
    }

    [Fact]
    public async Task Send_Forbidden_ExplainsTheMissingRole()
    {
        await ConfigureAsync();
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };

        var result = await CreateService().SendAsync("x");

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("403").And.Contain("role");
    }

    [Fact]
    public async Task Send_RateLimitsPerHour()
    {
        await ConfigureAsync();
        var svc = CreateService();
        for (var i = 0; i < AcsSmsService.MaxSendsPerHour; i++)
            (await svc.SendAsync("x")).Ok.Should().BeTrue();

        var over = await svc.SendAsync("x");

        over.Ok.Should().BeFalse();
        over.Error.Should().Contain("Rate limit");
        _handler.Requests.Should().HaveCount(AcsSmsService.MaxSendsPerHour);
    }

    [Fact]
    public async Task Send_NeverPutsTheMessageInTheError()
    {
        await ConfigureAsync();
        _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") };

        var result = await CreateService().SendAsync("MitID-kode 9999");

        result.Error.Should().NotContain("9999");
    }
}
