using Xunit;
using Moq;
using FluentAssertions;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.CLI.Tests.Services;

public class HetznerCloudServiceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => _handler(request);
    }

    private const string Token = "hc-token-123";

    private static HetznerCloudService CreateService(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var http = new HttpClient(new MockHttpMessageHandler(handler));
        var config = new Mock<IConfigurationService>();
        var credsJson = JsonSerializer.Serialize(new HetznerStoredCredentials
        {
            Token = SecretValue.From(Token),
            DefaultLocation = "fsn1",
            ProjectName = "pks"
        }, SecretJson.Persistence);
        config.Setup(x => x.GetAsync("hetzner.auth.credentials")).ReturnsAsync(credsJson);

        // Same trap as ScalewayServiceTests: an unfaked resolver falls back to the developer's real
        // ~/.pks-cli secret store.
        var secrets = new Mock<ISecretResolver>();
        secrets.Setup(x => x.RevealAsync(It.IsAny<string>()))
            .Returns((string key) => config.Object.GetAsync(key));

        return new HetznerCloudService(http, config.Object, secrets.Object);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string ServerTypesJson = """
        { "server_types": [
          { "id": 1, "name": "cx23", "description": "CX23", "cores": 2, "memory": 4.0, "disk": 40,
            "cpu_type": "shared", "architecture": "x86", "storage_type": "local", "deprecation": null,
            "prices": [
              { "location": "fsn1", "price_hourly": { "net": "0.0060000000", "gross": "0.0071400000" },
                "price_monthly": { "net": "3.4900000000", "gross": "4.1531000000" }, "included_traffic": 21990232555520 },
              { "location": "nbg1", "price_hourly": { "net": "0.0060000000", "gross": "0.0071400000" },
                "price_monthly": { "net": "3.4900000000", "gross": "4.1531000000" }, "included_traffic": 21990232555520 }
            ],
            "locations": [ { "id": 1, "name": "fsn1", "available": true, "deprecation": null },
                           { "id": 2, "name": "nbg1", "available": false, "deprecation": null } ] },
          { "id": 2, "name": "cx11", "description": "CX11", "cores": 1, "memory": 2.0, "disk": 20,
            "cpu_type": "shared", "architecture": "x86", "storage_type": "local",
            "deprecation": { "announced": "2024-01-01T00:00:00+00:00", "unavailable_after": "2024-06-01T00:00:00+00:00" },
            "prices": [], "locations": [] }
        ], "meta": { "pagination": { "page": 1, "per_page": 50, "next_page": null } } }
        """;

    [Fact]
    [Trait("Category", "Hetzner")]
    [Trait("Speed", "Fast")]
    public async Task ListServerTypesAsync_SendsBearerToken_AndParsesPricesPerLocation()
    {
        string? auth = null;
        var service = CreateService(req =>
        {
            auth = req.Headers.Authorization?.ToString();
            req.RequestUri!.AbsolutePath.Should().Be("/v1/server_types");
            return Task.FromResult(Json(ServerTypesJson));
        });

        var types = await service.ListServerTypesAsync();

        auth.Should().Be($"Bearer {Token}");
        types.Should().HaveCount(2);
        var cx23 = types.Single(t => t.Name == "cx23");
        cx23.Cores.Should().Be(2);
        cx23.MemoryGb.Should().Be(4.0);
        cx23.DiskGb.Should().Be(40);
        cx23.IsDeprecated.Should().BeFalse();
        var price = cx23.PriceAt("fsn1")!;
        price.HourlyNet.Should().Be(0.006m);
        price.MonthlyNet.Should().Be(3.49m);
        cx23.IsAvailableAt("fsn1").Should().BeTrue();
        cx23.IsAvailableAt("nbg1").Should().BeFalse();
        cx23.PriceAt("hel1").Should().BeNull();
        types.Single(t => t.Name == "cx11").IsDeprecated.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Hetzner")]
    [Trait("Speed", "Fast")]
    public void ServerType_DisplayLabel_ShowsHourlyAndMonthlyNetPrice()
    {
        var types = JsonSerializer.Deserialize<HetznerServerTypeListResponse>(ServerTypesJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.ServerTypes;
        var cx23 = types.Single(t => t.Name == "cx23");

        var label = cx23.DisplayLabel("fsn1");

        label.Should().Contain("cx23");
        label.Should().Contain("2 vCPU");
        label.Should().Contain("4 GB RAM");
        label.Should().Contain("40 GB");
        label.Should().Contain("€0.0060/hr");
        label.Should().Contain("€3.49/mo");
    }

    [Fact]
    [Trait("Category", "Hetzner")]
    [Trait("Speed", "Fast")]
    public async Task ListServersAsync_ParsesStatusLocationAndIpv4()
    {
        var service = CreateService(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/v1/servers");
            return Task.FromResult(Json("""
                { "servers": [
                  { "id": 42, "name": "pks-hc-1", "status": "running", "created": "2026-11-07T10:00:00+00:00",
                    "public_net": { "ipv4": { "id": 7, "ip": "65.21.1.2", "blocked": false },
                                    "ipv6": { "id": 8, "ip": "2a01:4f8::/64", "blocked": false } },
                    "server_type": { "id": 1, "name": "cx23", "cores": 2, "memory": 4.0, "disk": 40 },
                    "location": { "id": 1, "name": "fsn1", "city": "Falkenstein" },
                    "image": { "id": 1, "name": "ubuntu-24.04" },
                    "labels": { "managed-by": "pks" } }
                ], "meta": { "pagination": { "page": 1, "next_page": null } } }
                """));
        });

        var servers = await service.ListServersAsync();

        servers.Should().HaveCount(1);
        servers[0].Id.Should().Be(42);
        servers[0].Name.Should().Be("pks-hc-1");
        servers[0].Status.Should().Be("running");
        servers[0].PublicIpAddress.Should().Be("65.21.1.2");
        servers[0].LocationName.Should().Be("fsn1");
        servers[0].ServerTypeName.Should().Be("cx23");
    }

    [Fact]
    [Trait("Category", "Hetzner")]
    [Trait("Speed", "Fast")]
    public async Task PerformActionAsync_PostsToActionEndpoint()
    {
        HttpMethod? method = null;
        string? path = null;
        var service = CreateService(req =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            return Task.FromResult(Json("""{ "action": { "id": 1, "command": "shutdown", "status": "running" } }"""));
        });

        await service.PerformActionAsync(42, "shutdown");

        method.Should().Be(HttpMethod.Post);
        path.Should().Be("/v1/servers/42/actions/shutdown");
    }

    [Fact]
    [Trait("Category", "Hetzner")]
    [Trait("Speed", "Fast")]
    public async Task CreateServerAsync_SendsTypeImageLocationSshKeyAndCloudInit()
    {
        string? body = null;
        var service = CreateService(async req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/v1/servers");
            body = await req.Content!.ReadAsStringAsync();
            return Json("""
                { "server": { "id": 99, "name": "pks-hc-ab12", "status": "initializing",
                              "public_net": { "ipv4": { "ip": "65.21.9.9" } },
                              "server_type": { "name": "cx23" }, "location": { "name": "fsn1" } },
                  "action": { "id": 5, "command": "create_server", "status": "running" },
                  "root_password": null }
                """, HttpStatusCode.Created);
        });

        var server = await service.CreateServerAsync(new HetznerCreateOptions
        {
            Name = "pks-hc-ab12",
            ServerType = "cx23",
            Image = "ubuntu-24.04",
            Location = "fsn1",
            SshKeyIds = new[] { 1234L },
            SshPublicKey = "ssh-ed25519 AAAA pks",
            InstallDocker = true
        });

        server.Id.Should().Be(99);
        server.Status.Should().Be("initializing");
        server.PublicIpAddress.Should().Be("65.21.9.9");

        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        root.GetProperty("name").GetString().Should().Be("pks-hc-ab12");
        root.GetProperty("server_type").GetString().Should().Be("cx23");
        root.GetProperty("image").GetString().Should().Be("ubuntu-24.04");
        root.GetProperty("location").GetString().Should().Be("fsn1");
        root.GetProperty("start_after_create").GetBoolean().Should().BeTrue();
        root.GetProperty("ssh_keys").EnumerateArray().Select(e => e.GetInt64()).Should().Equal(1234L);
        root.GetProperty("labels").GetProperty("managed-by").GetString().Should().Be("pks");
        var userData = root.GetProperty("user_data").GetString()!;
        userData.Should().StartWith("#cloud-config");
        userData.Should().Contain("ssh-ed25519 AAAA pks");
        userData.Should().Contain("get.docker.com");
    }

    [Fact]
    [Trait("Category", "Hetzner")]
    [Trait("Speed", "Fast")]
    public async Task EnsureSshKeyAsync_ReusesExistingKey_OnUniquenessError()
    {
        var calls = new List<string>();
        var service = CreateService(req =>
        {
            calls.Add($"{req.Method} {req.RequestUri!.PathAndQuery}");
            if (req.Method == HttpMethod.Post)
                return Task.FromResult(Json(
                    """{ "error": { "code": "uniqueness_error", "message": "SSH key with the same fingerprint already exists" } }""",
                    HttpStatusCode.Conflict));
            return Task.FromResult(Json("""
                { "ssh_keys": [
                  { "id": 77, "name": "pks-other", "fingerprint": "aa:bb", "public_key": "ssh-ed25519 AAAA other-comment" }
                ], "meta": { "pagination": { "next_page": null } } }
                """));
        });

        var id = await service.EnsureSshKeyAsync("pks-hc-ab12", "ssh-ed25519 AAAA pks-hc-ab12");

        id.Should().Be(77);
        calls.Should().Contain(c => c.StartsWith("POST /v1/ssh_keys"));
        calls.Should().Contain(c => c.StartsWith("GET /v1/ssh_keys"));
    }

    [Fact]
    [Trait("Category", "Hetzner")]
    [Trait("Speed", "Fast")]
    public async Task EnsureSshKeyAsync_ReturnsNewId_OnCreate()
    {
        var service = CreateService(req => Task.FromResult(Json(
            """{ "ssh_key": { "id": 501, "name": "pks-hc-ab12", "fingerprint": "cc:dd", "public_key": "ssh-ed25519 AAAA pks" } }""",
            HttpStatusCode.Created)));

        var id = await service.EnsureSshKeyAsync("pks-hc-ab12", "ssh-ed25519 AAAA pks");

        id.Should().Be(501);
    }

    [Fact]
    [Trait("Category", "Hetzner")]
    [Trait("Speed", "Fast")]
    public async Task GetPricingAsync_ParsesIpv4HourlyAndStoragePerGb()
    {
        var service = CreateService(req =>
        {
            req.RequestUri!.AbsolutePath.Should().Be("/v1/pricing");
            return Task.FromResult(Json("""
                { "pricing": { "currency": "EUR", "vat_rate": "19.00",
                  "primary_ips": [
                    { "type": "ipv4", "prices": [ { "location": "fsn1",
                        "price_hourly": { "net": "0.0008000000", "gross": "0.0009520000" },
                        "price_monthly": { "net": "0.5000000000", "gross": "0.5950000000" } } ] },
                    { "type": "ipv6", "prices": [ { "location": "fsn1",
                        "price_hourly": { "net": "0.0000000000", "gross": "0.0000000000" },
                        "price_monthly": { "net": "0.0000000000", "gross": "0.0000000000" } } ] }
                  ],
                  "image": { "price_per_gb_month": { "net": "0.0119000000", "gross": "0.0141610000" } },
                  "volume": { "price_per_gb_month": { "net": "0.0440000000", "gross": "0.0523600000" } },
                  "server_backup": { "percentage": "20.00" },
                  "server_types": [] } }
                """));
        });

        var pricing = await service.GetPricingAsync();

        pricing.Currency.Should().Be("EUR");
        pricing.Ipv4HourlyNetAt("fsn1").Should().Be(0.0008m);
        pricing.Ipv4MonthlyNetAt("fsn1").Should().Be(0.5m);
        pricing.Ipv4HourlyNetAt("hel1").Should().BeNull();
        pricing.SnapshotPerGbMonthNet.Should().Be(0.0119m);
        pricing.VolumePerGbMonthNet.Should().Be(0.044m);
    }

    [Fact]
    [Trait("Category", "Hetzner")]
    [Trait("Speed", "Fast")]
    public async Task DeleteServerAsync_IgnoresNotFound()
    {
        var service = CreateService(req => Task.FromResult(Json(
            """{ "error": { "code": "not_found", "message": "server not found" } }""", HttpStatusCode.NotFound)));

        var act = async () => await service.DeleteServerAsync(42);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    [Trait("Category", "Hetzner")]
    [Trait("Speed", "Fast")]
    public async Task ListLocationsAsync_WithExplicitToken_UsesThatToken()
    {
        string? auth = null;
        var service = CreateService(req =>
        {
            auth = req.Headers.Authorization?.ToString();
            return Task.FromResult(Json("""
                { "locations": [ { "id": 1, "name": "fsn1", "description": "Falkenstein DC Park 1",
                                   "country": "DE", "city": "Falkenstein", "network_zone": "eu-central" } ] }
                """));
        });

        var locations = await service.ListLocationsAsync(token: "probe-token");

        auth.Should().Be("Bearer probe-token");
        locations.Should().ContainSingle(l => l.Name == "fsn1" && l.City == "Falkenstein");
    }

    [Fact]
    [Trait("Category", "Hetzner")]
    [Trait("Speed", "Fast")]
    public void Cost_ForOneHourADay_IsHourlyTimesThirty()
    {
        HetznerCost.MonthlyForDailyHours(hourlyNet: 0.0068m, hoursPerDay: 1).Should().Be(0.204m);
    }
}
