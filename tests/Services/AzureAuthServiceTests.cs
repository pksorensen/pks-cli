using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure;
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

public class AzureAuthServiceTests
{
    // ─────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────

    private static Mock<IConfigurationService> CreateConfigServiceMock(Dictionary<string, string>? initialData = null)
    {
        var mock = new Mock<IConfigurationService>();
        var store = initialData ?? new Dictionary<string, string>();

        mock.Setup(x => x.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => store.TryGetValue(key, out var v) ? v : null);

        mock.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<string, string, bool, bool>((k, v, _, _) => store[k] = v)
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .Callback<string>(k => store.Remove(k))
            .Returns(Task.CompletedTask);

        return mock;
    }

    private static HttpClient CreateMockHttpClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mockHandler = new MockHttpMessageHandler(handler);
        return new HttpClient(mockHandler);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }

    private static AzureAuthService CreateService(
        HttpClient? httpClient = null,
        Mock<IConfigurationService>? configMock = null,
        AzureAuthConfig? config = null)
    {
        return new AzureAuthService(
            httpClient ?? new HttpClient(),
            (configMock ?? CreateConfigServiceMock()).Object,
            new Mock<ILogger<AzureAuthService>>().Object,
            config ?? new AzureAuthConfig());
    }

    private static AzureStoredCredentials CreateValidCredentials()
    {
        return new AzureStoredCredentials
        {
            TenantId = "test-tenant-id",
            RefreshToken = "test-refresh-token",
            SubscriptionId = "sub-123",
            SubscriptionName = "My Subscription",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            LastRefreshedAt = DateTime.UtcNow.AddHours(-1)
        };
    }

    // ═════════════════════════════════════════════
    //  IsAuthenticatedAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureAuth")]
    public async Task IsAuthenticatedAsync_WhenNoCredentials_ReturnsFalse()
    {
        // Arrange
        var configMock = CreateConfigServiceMock();
        var service = CreateService(configMock: configMock);

        // Act
        var result = await service.IsAuthenticatedAsync();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "AzureAuth")]
    public async Task IsAuthenticatedAsync_WhenCredentialsStored_ReturnsTrue()
    {
        // Arrange
        var credentials = CreateValidCredentials();
        var json = JsonSerializer.Serialize(credentials);
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["azure.auth.credentials"] = json
        });
        var service = CreateService(configMock: configMock);

        // Act
        var result = await service.IsAuthenticatedAsync();

        // Assert
        result.Should().BeTrue();
    }

    // ═════════════════════════════════════════════
    //  GetStoredCredentialsAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureAuth")]
    public async Task GetStoredCredentialsAsync_WhenNoData_ReturnsNull()
    {
        // Arrange
        var configMock = CreateConfigServiceMock();
        var service = CreateService(configMock: configMock);

        // Act
        var result = await service.GetStoredCredentialsAsync();

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "AzureAuth")]
    public async Task StoreAndRetrieve_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var configMock = CreateConfigServiceMock();
        var service = CreateService(configMock: configMock);
        var original = CreateValidCredentials();

        // Act
        await service.StoreCredentialsAsync(original);
        var retrieved = await service.GetStoredCredentialsAsync();

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.TenantId.Should().Be(original.TenantId);
        retrieved.RefreshToken.Should().Be(original.RefreshToken);
        retrieved.SubscriptionId.Should().Be(original.SubscriptionId);
        retrieved.SubscriptionName.Should().Be(original.SubscriptionName);
    }

    // ═════════════════════════════════════════════
    //  GetAccessTokenAsync Tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "AzureAuth")]
    public async Task GetAccessToken_ReturnsNull_WhenNotAuthenticated()
    {
        // Arrange
        var configMock = CreateConfigServiceMock();
        var service = CreateService(configMock: configMock);

        // Act
        var result = await service.GetAccessTokenAsync("https://management.azure.com/.default");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "AzureAuth")]
    public async Task GetAccessToken_ReturnsToken_WhenRefreshSucceeds()
    {
        // Arrange
        var credentials = CreateValidCredentials();
        var json = JsonSerializer.Serialize(credentials);
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["azure.auth.credentials"] = json
        });

        var tokenResponse = new
        {
            access_token = "new-access-token",
            refresh_token = credentials.RefreshToken,
            expires_in = 3600,
            token_type = "Bearer"
        };

        var httpClient = CreateMockHttpClient(async _ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(tokenResponse))
            });

        var service = CreateService(httpClient: httpClient, configMock: configMock);

        // Act
        var result = await service.GetAccessTokenAsync("https://management.azure.com/.default");

        // Assert
        result.Should().Be("new-access-token");
    }
}
