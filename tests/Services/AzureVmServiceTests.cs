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

    private static AzureVmService CreateService(Func<HttpRequestMessage, Task<HttpResponseMessage>>? handler = null)
    {
        var httpClient = new HttpClient(new MockHttpMessageHandler(
            handler ?? (_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            }))));

        return new AzureVmService(httpClient, new Mock<ILogger<AzureVmService>>().Object);
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
