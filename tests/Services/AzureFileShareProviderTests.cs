using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.Net;
using System.Text.Json;

namespace PKS.CLI.Tests.Services;

public class AzureFileShareProviderTests
{
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
        return new HttpClient(new MockHttpMessageHandler(handler));
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }

    private static AzureFileShareProvider CreateProvider(
        HttpClient? httpClient = null,
        Mock<IConfigurationService>? configMock = null,
        AzureFileShareAuthConfig? config = null)
    {
        return new AzureFileShareProvider(
            httpClient ?? new HttpClient(),
            (configMock ?? CreateConfigServiceMock()).Object,
            new Mock<ILogger<AzureFileShareProvider>>().Object,
            config ?? new AzureFileShareAuthConfig());
    }

    private static FileShareStoredCredentials CreateValidCredentials() => new()
    {
        TenantId = "test-tenant-id",
        RefreshToken = "test-refresh-token",
        SelectedSubscriptionId = "sub-123",
        SelectedSubscriptionName = "My Subscription",
        SelectedStorageAccountName = "mystorage",
        SelectedStorageAccountResourceGroup = "my-rg",
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        LastRefreshedAt = DateTime.UtcNow.AddHours(-1)
    };

    // ═══════════════════════════════════════
    //  Provider metadata
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "FileShare")]
    public void ProviderName_IsAzureFileShare()
    {
        var provider = CreateProvider();
        provider.ProviderName.Should().Be("Azure File Share");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void ProviderKey_IsAzureFileshare()
    {
        var provider = CreateProvider();
        provider.ProviderKey.Should().Be("azure-fileshare");
    }

    // ═══════════════════════════════════════
    //  IsAuthenticatedAsync
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task IsAuthenticated_ReturnsTrue_WhenCredentialsExist()
    {
        var json = JsonSerializer.Serialize(CreateValidCredentials());
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["fileshare.azure.credentials"] = json
        });
        var provider = CreateProvider(configMock: configMock);

        var result = await provider.IsAuthenticatedAsync();

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task IsAuthenticated_ReturnsFalse_WhenNoCredentials()
    {
        var provider = CreateProvider();

        var result = await provider.IsAuthenticatedAsync();

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task IsAuthenticated_ReturnsFalse_WhenEmptyRefreshToken()
    {
        var credentials = CreateValidCredentials();
        credentials.RefreshToken = string.Empty;
        var json = JsonSerializer.Serialize(credentials);
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["fileshare.azure.credentials"] = json
        });
        var provider = CreateProvider(configMock: configMock);

        var result = await provider.IsAuthenticatedAsync();

        result.Should().BeFalse();
    }

    // ═══════════════════════════════════════
    //  GetAccessTokenAsync
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task GetAccessToken_ReturnsNull_WhenNotAuthenticated()
    {
        var provider = CreateProvider();

        var result = await provider.GetAccessTokenAsync("https://management.azure.com/.default");

        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task GetAccessToken_ReturnsToken_WhenRefreshSucceeds()
    {
        var credentials = CreateValidCredentials();
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["fileshare.azure.credentials"] = JsonSerializer.Serialize(credentials)
        });

        var tokenResponse = new FileShareTokenResponse
        {
            AccessToken = "new-access-token",
            RefreshToken = credentials.RefreshToken,
            ExpiresIn = 3600,
            TokenType = "Bearer"
        };

        var httpClient = CreateMockHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(tokenResponse))
        }));

        var provider = CreateProvider(httpClient: httpClient, configMock: configMock);

        var result = await provider.GetAccessTokenAsync("https://management.azure.com/.default");

        result.Should().Be("new-access-token");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task GetAccessToken_RotatesRefreshToken_WhenServerRotates()
    {
        var credentials = CreateValidCredentials();
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["fileshare.azure.credentials"] = JsonSerializer.Serialize(credentials)
        });

        var tokenResponse = new FileShareTokenResponse
        {
            AccessToken = "new-access-token",
            RefreshToken = "rotated-refresh-token",
            ExpiresIn = 3600,
            TokenType = "Bearer"
        };

        var httpClient = CreateMockHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(tokenResponse))
        }));

        var provider = CreateProvider(httpClient: httpClient, configMock: configMock);

        var result = await provider.GetAccessTokenAsync("https://management.azure.com/.default");

        result.Should().Be("new-access-token");
        configMock.Verify(x => x.SetAsync(
            "fileshare.azure.credentials",
            It.Is<string>(json => json.Contains("rotated-refresh-token")),
            true,
            false),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task GetAccessToken_ReturnsNull_WhenRefreshFails()
    {
        var credentials = CreateValidCredentials();
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["fileshare.azure.credentials"] = JsonSerializer.Serialize(credentials)
        });

        var httpClient = CreateMockHttpClient(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\"}")
        }));

        var provider = CreateProvider(httpClient: httpClient, configMock: configMock);

        var result = await provider.GetAccessTokenAsync("https://management.azure.com/.default");

        result.Should().BeNull();
    }

    // ═══════════════════════════════════════
    //  ListStorageAccountsAsync
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task ListStorageAccounts_ReturnsAccounts()
    {
        var response = new StorageAccountListResponse
        {
            Value = new List<StorageAccountInfo>
            {
                new() { Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/storage1", Name = "storage1", Location = "eastus", Kind = "StorageV2" },
                new() { Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/storage2", Name = "storage2", Location = "westus", Kind = "FileStorage" }
            }
        };

        var httpClient = CreateMockHttpClient(async request =>
        {
            request.RequestUri!.ToString().Should().Contain("Microsoft.Storage/storageAccounts");
            request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            request.Headers.Authorization.Parameter.Should().Be("test-token");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response))
            };
        });

        var provider = CreateProvider(httpClient: httpClient);

        var result = await provider.ListStorageAccountsAsync("test-token", "sub-1");

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("storage1");
        result[1].Name.Should().Be("storage2");
    }

    // ═══════════════════════════════════════
    //  ListFileSharesAsync
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task ListFileShares_ReturnsShares()
    {
        var response = new AzureFileShareListResponse
        {
            Value = new List<AzureFileShareInfo>
            {
                new() { Name = "share1", Properties = new AzureFileShareProperties { ShareQuota = 100, EnabledProtocols = "SMB" } },
                new() { Name = "share2", Properties = new AzureFileShareProperties { ShareQuota = 50, EnabledProtocols = "NFS" } }
            }
        };

        var httpClient = CreateMockHttpClient(async request =>
        {
            request.RequestUri!.ToString().Should().Contain("fileServices/default/shares");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response))
            };
        });

        var provider = CreateProvider(httpClient: httpClient);

        var result = await provider.ListFileSharesAsync("test-token", "sub-1", "my-rg", "mystorage");

        result.Should().HaveCount(2);
        result[0].Name.Should().Be("share1");
        result[0].Properties.ShareQuota.Should().Be(100);
        result[1].Name.Should().Be("share2");
        result[1].Properties.EnabledProtocols.Should().Be("NFS");
    }

    // ═══════════════════════════════════════
    //  ListResourcesAsync
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task ListResources_ReturnsEmpty_WhenNotAuthenticated()
    {
        var provider = CreateProvider();

        var result = await provider.ListResourcesAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task ListResources_ReturnsShares_WhenAuthenticated()
    {
        var credentials = CreateValidCredentials();
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["fileshare.azure.credentials"] = JsonSerializer.Serialize(credentials)
        });

        var callCount = 0;
        var httpClient = CreateMockHttpClient(async request =>
        {
            callCount++;
            var url = request.RequestUri!.ToString();

            // First call: token refresh
            if (url.Contains("/token"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new FileShareTokenResponse
                    {
                        AccessToken = "mgmt-token",
                        RefreshToken = "test-refresh-token",
                        ExpiresIn = 3600
                    }))
                };
            }

            // Second call: list file shares
            if (url.Contains("fileServices/default/shares"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new AzureFileShareListResponse
                    {
                        Value = new List<AzureFileShareInfo>
                        {
                            new() { Name = "data-share", Properties = new AzureFileShareProperties { ShareQuota = 100 } }
                        }
                    }))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = CreateProvider(httpClient: httpClient, configMock: configMock);

        var result = (await provider.ListResourcesAsync()).ToList();

        result.Should().HaveCount(1);
        result[0].ResourceName.Should().Be("data-share");
        result[0].AccountName.Should().Be("mystorage");
        result[0].ProviderKey.Should().Be("azure-fileshare");
    }
}
