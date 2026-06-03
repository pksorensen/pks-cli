using Xunit;
using Moq;
using FluentAssertions;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.CLI.Tests.Services;

public class ScalewayServiceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => _handler(request);
    }

    private const string SecretKey = "secret-123";

    private static ScalewayService CreateService(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var http = new HttpClient(new MockHttpMessageHandler(handler));
        var config = new Mock<IConfigurationService>();
        var credsJson = JsonSerializer.Serialize(new ScalewayStoredCredentials
        {
            AccessKey = "SCWXXX",
            SecretKey = SecretKey,
            OrganizationId = "org-1",
            DefaultProjectId = "proj-1",
            DefaultZone = "fr-par-2"
        });
        config.Setup(x => x.GetAsync("scaleway.auth.credentials")).ReturnsAsync(credsJson);
        return new ScalewayService(http, config.Object);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    [Trait("Category", "Scaleway")]
    [Trait("Speed", "Fast")]
    public async Task ListServersAsync_SendsAuthTokenHeader_AndParsesServers()
    {
        string? sentToken = null;
        var service = CreateService(req =>
        {
            sentToken = req.Headers.TryGetValues("X-Auth-Token", out var v) ? v.First() : null;
            req.RequestUri!.ToString().Should().Contain("/instance/v1/zones/fr-par-2/servers");
            return Task.FromResult(Json("""
                { "servers": [
                  { "id": "srv-1", "name": "h100", "commercial_type": "H100-1-80G",
                    "state": "running", "public_ip": { "address": "51.1.2.3" } }
                ] }
                """));
        });

        var servers = await service.ListServersAsync("fr-par-2", "proj-1");

        sentToken.Should().Be(SecretKey);
        servers.Should().HaveCount(1);
        servers[0].CommercialType.Should().Be("H100-1-80G");
        servers[0].State.Should().Be("running");
        servers[0].PublicIpAddress.Should().Be("51.1.2.3");
        servers[0].Zone.Should().Be("fr-par-2");
    }

    [Fact]
    [Trait("Category", "Scaleway")]
    [Trait("Speed", "Fast")]
    public async Task ListServersAsync_PrefersIpv4FromPublicIps()
    {
        var service = CreateService(_ => Task.FromResult(Json("""
            { "servers": [
              { "id": "srv-1", "name": "h100", "commercial_type": "H100-1-80G", "state": "stopped",
                "public_ip": { "address": "2001:bc8:1210::1", "family": "inet6" },
                "public_ips": [
                  { "address": "2001:bc8:1210::1", "family": "inet6" },
                  { "address": "51.159.10.20", "family": "inet" }
                ] }
            ] }
            """)));

        var servers = await service.ListServersAsync("fr-par-2");

        servers[0].PublicIpAddress.Should().Be("51.159.10.20");
    }

    [Fact]
    [Trait("Category", "Scaleway")]
    [Trait("Speed", "Fast")]
    public async Task ListServersAsync_FallsBackToLegacyPublicIp_WhenNoPublicIps()
    {
        var service = CreateService(_ => Task.FromResult(Json("""
            { "servers": [
              { "id": "srv-1", "name": "h100", "commercial_type": "H100-1-80G", "state": "running",
                "public_ip": { "address": "51.159.10.20", "family": "inet" } }
            ] }
            """)));

        var servers = await service.ListServersAsync("fr-par-2");

        servers[0].PublicIpAddress.Should().Be("51.159.10.20");
    }

    [Fact]
    [Trait("Category", "Scaleway")]
    [Trait("Speed", "Fast")]
    public async Task PerformActionAsync_PostsActionBody()
    {
        string? body = null;
        HttpMethod? method = null;
        var service = CreateService(async req =>
        {
            method = req.Method;
            req.RequestUri!.ToString().Should().Contain("/servers/srv-1/action");
            body = req.Content == null ? null : await req.Content.ReadAsStringAsync();
            return Json("{}");
        });

        await service.PerformActionAsync("fr-par-2", "srv-1", "poweron");

        method.Should().Be(HttpMethod.Post);
        body.Should().Contain("\"action\"");
        body.Should().Contain("poweron");
    }

    [Fact]
    [Trait("Category", "Scaleway")]
    [Trait("Speed", "Fast")]
    public async Task ListServerTypesAsync_KeysTypeNameFromMap_AndFlagsGpu()
    {
        var service = CreateService(_ => Task.FromResult(Json("""
            { "servers": {
              "H100-1-80G": { "ncpus": 24, "ram": 257698037760, "gpu": 1,
                              "gpu_info": { "gpu_name": "H100", "gpu_manufacturer": "Nvidia" }, "arch": "x86_64" },
              "GP1-S":      { "ncpus": 4, "ram": 17179869184, "gpu": 0, "arch": "x86_64" }
            } }
            """)));

        var types = await service.ListServerTypesAsync("fr-par-2");

        types.Should().HaveCount(2);
        var h100 = types.Single(t => t.Name == "H100-1-80G");
        h100.IsGpu.Should().BeTrue();
        h100.Gpu.Should().Be(1);
        h100.GpuInfo!.Name.Should().Be("H100");
        types.Single(t => t.Name == "GP1-S").IsGpu.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Scaleway")]
    [Trait("Speed", "Fast")]
    public async Task SetCloudInitAsync_PatchesRawUserData_WithAuthHeader()
    {
        HttpMethod? method = null;
        string? path = null;
        string? body = null;
        string? token = null;
        var service = CreateService(async req =>
        {
            method = req.Method;
            path = req.RequestUri!.AbsolutePath;
            token = req.Headers.TryGetValues("X-Auth-Token", out var v) ? v.First() : null;
            body = req.Content == null ? null : await req.Content.ReadAsStringAsync();
            return Json("{}");
        });

        await service.SetCloudInitAsync("fr-par-2", "srv-1", "#cloud-config\nssh_authorized_keys:\n  - ssh-ed25519 AAAA pks\n", SecretKey);

        method.Should().Be(HttpMethod.Patch);
        path.Should().Be("/instance/v1/zones/fr-par-2/servers/srv-1/user_data/cloud-init");
        token.Should().Be(SecretKey);
        body.Should().Contain("ssh_authorized_keys");
        body.Should().Contain("ssh-ed25519 AAAA pks");
    }

    [Fact]
    [Trait("Category", "Scaleway")]
    [Trait("Speed", "Fast")]
    public async Task GetServerStateAsync_ReturnsState()
    {
        var service = CreateService(_ => Task.FromResult(Json("""
            { "server": { "id": "srv-1", "name": "h100", "commercial_type": "H100-1-80G", "state": "stopped" } }
            """)));

        var state = await service.GetServerStateAsync("fr-par-2", "srv-1");

        state.Should().Be("stopped");
    }
}
