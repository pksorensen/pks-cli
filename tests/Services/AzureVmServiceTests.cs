using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.CLI.Tests.Services;

public class AzureVmServiceTests
{
    // ─────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }

    private static AzureVmService CreateService(Func<HttpRequestMessage, Task<HttpResponseMessage>>? handler = null, TimeSpan? pollInterval = null)
    {
        var httpClient = new HttpClient(new MockHttpMessageHandler(
            handler ?? (_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            }))));

        return new AzureVmService(httpClient, new Mock<ILogger<AzureVmService>>().Object, pollInterval);
    }

    // ═════════════════════════════════════════════
    //  ListResourceGroupsAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureVm")]
    public async Task ListResourceGroupsAsync_ReturnsDeserializedGroups()
    {
        // Arrange
        var rgResponse = new AzureResourceGroupListResponse
        {
            Value = new List<AzureResourceGroup>
            {
                new() { Id = "/subscriptions/sub/resourceGroups/rg1", Name = "rg1", Location = "eastus" },
                new() { Id = "/subscriptions/sub/resourceGroups/rg2", Name = "rg2", Location = "westeurope" }
            }
        };

        var service = CreateService(async req =>
        {
            req.RequestUri!.ToString().Should().Contain("resourcegroups");
            req.Headers.Authorization!.Scheme.Should().Be("Bearer");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(rgResponse))
            };
        });

        // Act
        var result = await service.ListResourceGroupsAsync("test-token", "sub-1");

        // Assert
        result.Should().HaveCount(2);
        result[0].Name.Should().Be("rg1");
        result[0].Location.Should().Be("eastus");
        result[1].Name.Should().Be("rg2");
        result[1].Location.Should().Be("westeurope");
    }

    // ═════════════════════════════════════════════
    //  EnsureResourceGroupAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureVm")]
    public async Task EnsureResourceGroupAsync_WhenNotExists_CreatesIt()
    {
        // Arrange
        var created = false;
        var service = CreateService(async req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri!.ToString().Contains("resourceGroups/new-rg"))
            {
                created = true;
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new AzureResourceGroup
                    {
                        Id = "/subscriptions/sub/resourceGroups/new-rg",
                        Name = "new-rg",
                        Location = "eastus"
                    }))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        // Act
        var result = await service.EnsureResourceGroupAsync("test-token", "sub-1", "new-rg", "eastus");

        // Assert
        created.Should().BeTrue();
        result.Name.Should().Be("new-rg");
    }

    // ═════════════════════════════════════════════
    //  WaitForSshAsync Tests
    // ═════════════════════════════════════════════

    // ═════════════════════════════════════════════
    //  Feature 1: CreateVmAsync passes diskSizeGB
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureVmService")]
    public async Task CreateVmAsync_PassesDiskSizeGB()
    {
        // Arrange
        string? capturedVmPutBody = null;
        var cts = new CancellationTokenSource();

        var service = CreateService(async req =>
        {
            var url = req.RequestUri!.ToString();
            var body = req.Content != null ? await req.Content.ReadAsStringAsync() : "";

            // Capture VM PUT and cancel to avoid provisioning poll delays
            if (req.Method == HttpMethod.Put && url.Contains("virtualMachines") && !url.Contains("deallocate"))
            {
                capturedVmPutBody = body;
                cts.Cancel();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"id":"/test","properties":{"provisioningState":"Creating"}}""",
                        System.Text.Encoding.UTF8, "application/json")
                };
            }

            // Provider checks — return Registered to skip registration loop
            if (url.Contains("/providers/") && req.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"registrationState":"Registered"}""", System.Text.Encoding.UTF8, "application/json")
                };

            // Everything else (IP PUT, NSG PUT, VNet PUT, NIC PUT) — return Succeeded
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"/test","properties":{"provisioningState":"Succeeded","ipAddress":"1.2.3.4"}}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        }, pollInterval: TimeSpan.Zero);

        var options = new AzureVmCreateOptions
        {
            AccessToken = "tok",
            SubscriptionId = "sub1",
            ResourceGroupName = "rg1",
            Location = "eastus",
            VmName = "test-vm",
            VmSize = "Standard_B2s",
            AdminUsername = "azureuser",
            SshPublicKey = "ssh-ed25519 AAAAB test",
            OsDiskSizeGb = 256
        };

        // Act — will throw OperationCanceledException after VM PUT is captured
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreateVmAsync(options, null, cts.Token));

        // Assert
        capturedVmPutBody.Should().NotBeNull("VM PUT should have been intercepted");
        using var doc = System.Text.Json.JsonDocument.Parse(capturedVmPutBody!);
        var diskSizeGb = doc.RootElement
            .GetProperty("properties")
            .GetProperty("storageProfile")
            .GetProperty("osDisk")
            .GetProperty("diskSizeGB")
            .GetInt32();
        diskSizeGb.Should().Be(256);
    }

    // ═════════════════════════════════════════════
    //  Feature 4: GetOsDiskNameAsync + DestroyVmAsync
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureVmService")]
    public async Task GetOsDiskNameAsync_ReturnsName_FromVmProperties()
    {
        var service = CreateService(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"properties":{"storageProfile":{"osDisk":{"name":"test-vm_OsDisk_1_abc"}}}}""",
                    System.Text.Encoding.UTF8, "application/json")
            }));

        var result = await service.GetOsDiskNameAsync("tok", "sub1", "rg1", "test-vm");

        result.Should().Be("test-vm_OsDisk_1_abc");
    }

    [Fact]
    [Trait("Category", "AzureVmService")]
    public async Task DestroyVmAsync_DeletesInOrder_VmFirstNicSecondIpThirdDiskFourthNsgFifth()
    {
        var deleteOrder = new List<string>();

        var service = CreateService(async req =>
        {
            var url = req.RequestUri!.ToString();

            if (req.Method == HttpMethod.Delete)
            {
                if (url.Contains("/virtualMachines/")) deleteOrder.Add("VM");
                else if (url.Contains("/networkInterfaces/")) deleteOrder.Add("NIC");
                else if (url.Contains("/publicIPAddresses/")) deleteOrder.Add("IP");
                else if (url.Contains("/disks/")) deleteOrder.Add("Disk");
                else if (url.Contains("/networkSecurityGroups/")) deleteOrder.Add("NSG");
            }

            // instanceView for GetVmStatusAsync (returns deallocated to skip deallocate step)
            if (req.Method == HttpMethod.Get && url.Contains("instanceView"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"statuses":[{"code":"PowerState/deallocated"}]}""",
                        System.Text.Encoding.UTF8, "application/json")
                };

            // GET VM for GetOsDiskNameAsync
            if (req.Method == HttpMethod.Get && url.Contains("/virtualMachines/") && !url.Contains("instanceView"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"properties":{"storageProfile":{"osDisk":{"name":"test-vm_OsDisk"}}}}""",
                        System.Text.Encoding.UTF8, "application/json")
                };

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }, pollInterval: TimeSpan.Zero);

        await service.DestroyVmAsync("tok", "sub1", "rg1", "test-vm");

        deleteOrder.Should().ContainInOrder("VM", "NIC", "IP", "Disk", "NSG");
    }

    [Fact]
    [Trait("Category", "AzureVm")]
    public async Task WaitForSshAsync_WhenTimeout_ReturnsFalse()
    {
        // Arrange — use localhost on a port that is not open
        var service = CreateService();

        // Act — use a very short timeout so the test completes quickly
        var result = await service.WaitForSshAsync("127.0.0.1", 19999, TimeSpan.FromMilliseconds(200));

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "AzureVm")]
    public async Task WaitForSshAsync_WhenPortOpen_ReturnsTrue()
    {
        // Arrange — start a local TCP listener to simulate SSH
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var service = CreateService();

        try
        {
            // Act
            var result = await service.WaitForSshAsync("127.0.0.1", port, TimeSpan.FromSeconds(5));

            // Assert
            result.Should().BeTrue();
        }
        finally
        {
            listener.Stop();
        }
    }
}
