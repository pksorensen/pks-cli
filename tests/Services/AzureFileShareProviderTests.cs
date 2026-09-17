using PKS.CLI.Tests.Security;
using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using System.Net;
using System.Text.Json;

namespace PKS.CLI.Tests.Services;

public class AzureFileShareProviderTests : IDisposable
{
    private const string TenantsKey = "azure.tenants.credentials";
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "pks-cli-tests", "fileshare", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

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

    /// <summary>A real tenant store over the same config mock, so the fileshare-token migration and
    /// the tenant-keyed refresh run for real; the token cache gets a fresh directory per test.</summary>
    private AzureFileShareProvider CreateProvider(
        HttpClient? httpClient = null,
        Mock<IConfigurationService>? configMock = null,
        AzureFileShareAuthConfig? config = null,
        IAzureResourceRegistry? registry = null)
    {
        var configuration = configMock ?? CreateConfigServiceMock();
        var http = httpClient ?? new HttpClient();
        var secrets = FakeSecretResolver.BackedBy(configuration.Object.GetAsync);
        var tenants = new AzureTenantCredentialStore(
            http,
            configuration.Object,
            secrets,
            new AzureTokenCache(_cacheDir),
            new Mock<ILogger<AzureTenantCredentialStore>>().Object);
        return new AzureFileShareProvider(
            http,
            configuration.Object,
            new Mock<ILogger<AzureFileShareProvider>>().Object,
            tenants,
            registry ?? CreateRegistry(configuration),
            config ?? new AzureFileShareAuthConfig());
    }

    /// <summary>A real registry over the same config mock, in the per-test directory so its one-shot
    /// legacy migration never touches the real <c>~/.pks-cli</c>.</summary>
    private AzureResourceRegistry CreateRegistry(Mock<IConfigurationService> configuration)
        => new AzureResourceRegistry(
            configuration.Object,
            FakeSecretResolver.BackedBy(configuration.Object.GetAsync),
            Path.Combine(_cacheDir, "azure-resources.json"));

    private static AzureResourceEntry StorageEntry(string account, string tenantId, bool enabled = true, string? subscriptionName = null) => new()
    {
        Kind = AzureResourceKind.Storage,
        Key = account,
        Name = account,
        TenantId = tenantId,
        SubscriptionId = $"sub-{account}",
        SubscriptionName = subscriptionName,
        ResourceGroup = $"rg-{account}",
        Enabled = enabled,
        DiscoveredAt = DateTime.UtcNow
    };

    private static string TenantStoreJson(string tenantId, string refreshToken)
        => TenantStoreJson((tenantId, refreshToken));

    private static string TenantStoreJson(params (string TenantId, string RefreshToken)[] tenants) => JsonSerializer.Serialize(
        tenants.Select(t => new AzureTenantCredentials
        {
            TenantId = t.TenantId, RefreshToken = SecretValue.From(t.RefreshToken), CreatedAt = DateTime.UtcNow, LastRefreshedAt = DateTime.UtcNow
        }).ToList(), SecretJson.Persistence);

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body))
    };

    /// <summary>
    /// One handler for the two-tenant scenarios: each tenant's token endpoint mints its own access
    /// token, and each account's ARM share listing must arrive bearing the token of the tenant that
    /// account was registered in. <paramref name="sharesFor"/> answers the listing per account.
    /// </summary>
    private static HttpClient TwoTenantHttp(Func<string, HttpResponseMessage> sharesFor)
        => CreateMockHttpClient(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/tenant-a/oauth2/v2.0/token"))
                return Task.FromResult(Json(new FileShareTokenResponse { AccessToken = "token-a", RefreshToken = "rt-a", ExpiresIn = 3600 }));
            if (url.Contains("/tenant-b/oauth2/v2.0/token"))
                return Task.FromResult(Json(new FileShareTokenResponse { AccessToken = "token-b", RefreshToken = "rt-b", ExpiresIn = 3600 }));

            var match = System.Text.RegularExpressions.Regex.Match(url, @"storageAccounts/([^/]+)/fileServices/default/shares");
            if (!match.Success)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var account = match.Groups[1].Value;
            var expectedToken = account == "acct-a" ? "token-a" : "token-b";
            request.Headers.Authorization!.Parameter.Should().Be(expectedToken,
                $"the listing for {account} must use the token of its own tenant");
            return Task.FromResult(sharesFor(account));
        });

    private static HttpResponseMessage SharesResponse(params string[] names) => Json(new AzureFileShareListResponse
    {
        Value = names.Select(n => new AzureFileShareInfo { Name = n, Properties = new AzureFileShareProperties { ShareQuota = 100, EnabledProtocols = "SMB" } }).ToList()
    });

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
    public async Task IsAuthenticated_ReturnsFalse_WhenTenantIsNotInTheStore()
    {
        // An old entry whose token was already moved away (or never existed): nothing to migrate,
        // and the tenant store knows no such tenant.
        var credentials = CreateValidCredentials();
        credentials.RefreshToken = string.Empty;
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["fileshare.azure.credentials"] = JsonSerializer.Serialize(credentials)
        });
        var provider = CreateProvider(configMock: configMock);

        var result = await provider.IsAuthenticatedAsync();

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task IsAuthenticated_ReturnsTrue_WhenTenantIsInTheStore_AndTokenFieldIsBlank()
    {
        var credentials = CreateValidCredentials();
        credentials.RefreshToken = string.Empty;
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["fileshare.azure.credentials"] = JsonSerializer.Serialize(credentials),
            [TenantsKey] = TenantStoreJson("test-tenant-id", "fake-tenant-refresh-token")
        });
        var provider = CreateProvider(configMock: configMock);

        var result = await provider.IsAuthenticatedAsync();

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task IsAuthenticated_ReturnsFalse_WhenNoStorageAccountSelected()
    {
        var credentials = CreateValidCredentials();
        credentials.SelectedStorageAccountName = string.Empty;
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["fileshare.azure.credentials"] = JsonSerializer.Serialize(credentials)
        });
        var provider = CreateProvider(configMock: configMock);

        var result = await provider.IsAuthenticatedAsync();

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task IsAuthenticated_MovesLegacyTokenIntoTheTenantStore()
    {
        var store = new Dictionary<string, string>
        {
            ["fileshare.azure.credentials"] = JsonSerializer.Serialize(CreateValidCredentials())
        };
        var configMock = CreateConfigServiceMock(store);
        var provider = CreateProvider(configMock: configMock);

        (await provider.IsAuthenticatedAsync()).Should().BeTrue();

        store[TenantsKey].Should().Contain("test-refresh-token");
        var fileShare = JsonSerializer.Deserialize<FileShareStoredCredentials>(store["fileshare.azure.credentials"])!;
        fileShare.RefreshToken.Should().BeEmpty();
        fileShare.SelectedStorageAccountName.Should().Be("mystorage");
        configMock.Verify(x => x.DeleteAsync(It.IsAny<string>()), Times.Never);
    }

    // ═══════════════════════════════════════
    //  GetAccessTokenAsync
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task GetAccessToken_ReturnsNull_WhenNotAuthenticated()
    {
        var provider = CreateProvider();

        var result = await provider.GetAccessTokenAsync("mystorage", "https://management.azure.com/.default");

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

        var result = await provider.GetAccessTokenAsync("mystorage", "https://management.azure.com/.default");

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

        var result = await provider.GetAccessTokenAsync("mystorage", "https://management.azure.com/.default");

        result.Should().Be("new-access-token");
        configMock.Verify(x => x.SetAsync(
            TenantsKey,
            It.Is<string>(json => json.Contains("rotated-refresh-token")),
            true,
            true),
            Times.Once);
        configMock.Verify(x => x.SetAsync(
            "fileshare.azure.credentials",
            It.Is<string>(json => json.Contains("rotated-refresh-token") || json.Contains("test-refresh-token")),
            It.IsAny<bool>(),
            It.IsAny<bool>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task GetAccessToken_UsesTheTenantStore_WhenFileShareEntryHasNoToken()
    {
        var credentials = CreateValidCredentials();
        credentials.RefreshToken = string.Empty;
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            ["fileshare.azure.credentials"] = JsonSerializer.Serialize(credentials),
            [TenantsKey] = TenantStoreJson("test-tenant-id", "fake-tenant-refresh-token")
        });

        string? sentBody = null;
        var httpClient = CreateMockHttpClient(async request =>
        {
            request.RequestUri!.ToString().Should().Contain("/test-tenant-id/oauth2/v2.0/token");
            sentBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new FileShareTokenResponse
                {
                    AccessToken = "new-access-token",
                    RefreshToken = "fake-tenant-refresh-token",
                    ExpiresIn = 3600
                }))
            };
        });

        var provider = CreateProvider(httpClient: httpClient, configMock: configMock);

        var result = await provider.GetAccessTokenAsync("mystorage", "https://storage.azure.com/.default");

        result.Should().Be("new-access-token");
        sentBody.Should().Contain("refresh_token=fake-tenant-refresh-token");
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

        var result = await provider.GetAccessTokenAsync("mystorage", "https://management.azure.com/.default");

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

    // ═══════════════════════════════════════
    //  Multiple enabled storage accounts (registry-driven)
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task IsAuthenticated_ReturnsFalse_WhenNoStorageEntryIsEnabled()
    {
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            [TenantsKey] = TenantStoreJson("tenant-a", "rt-a")
        });
        var registry = CreateRegistry(configMock);
        await registry.UpsertAsync(new[] { StorageEntry("acct-a", "tenant-a", enabled: false) });
        var provider = CreateProvider(configMock: configMock, registry: registry);

        (await provider.IsAuthenticatedAsync()).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task IsAuthenticated_ReturnsTrue_FromRegistryAlone_WhenAnEnabledAccountsTenantIsSignedIn()
    {
        // No legacy fileshare.azure.credentials at all: the registry entry plus the tenant store is enough.
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            [TenantsKey] = TenantStoreJson("tenant-b", "rt-b")
        });
        var registry = CreateRegistry(configMock);
        await registry.UpsertAsync(new[]
        {
            StorageEntry("acct-a", "tenant-a"),   // tenant not signed in
            StorageEntry("acct-b", "tenant-b")
        });
        var provider = CreateProvider(configMock: configMock, registry: registry);

        (await provider.IsAuthenticatedAsync()).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task ListResources_ListsEveryEnabledAccount_UsingEachTenantsOwnToken()
    {
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            [TenantsKey] = TenantStoreJson(("tenant-a", "rt-a"), ("tenant-b", "rt-b"))
        });
        var registry = CreateRegistry(configMock);
        await registry.UpsertAsync(new[]
        {
            StorageEntry("acct-a", "tenant-a", subscriptionName: "Sub A"),
            StorageEntry("acct-b", "tenant-b", subscriptionName: "Sub B")
        });
        var http = TwoTenantHttp(account => account == "acct-a"
            ? SharesResponse("share-a1", "share-a2")
            : SharesResponse("share-b1"));
        var provider = CreateProvider(httpClient: http, configMock: configMock, registry: registry);

        var result = (await provider.ListResourcesAsync()).ToList();

        result.Select(r => (r.AccountName, r.ResourceName)).Should().BeEquivalentTo(new[]
        {
            ("acct-a", "share-a1"), ("acct-a", "share-a2"), ("acct-b", "share-b1")
        });
        result.Should().OnlyContain(r => r.ProviderKey == "azure-fileshare");
        result.First(r => r.AccountName == "acct-b").Description.Should().Contain("Sub B");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task ListResources_SkipsAnAccountThatFails_AndStillListsTheOthers()
    {
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            [TenantsKey] = TenantStoreJson(("tenant-a", "rt-a"), ("tenant-b", "rt-b"))
        });
        var registry = CreateRegistry(configMock);
        await registry.UpsertAsync(new[] { StorageEntry("acct-a", "tenant-a"), StorageEntry("acct-b", "tenant-b") });
        var http = TwoTenantHttp(account => account == "acct-a"
            ? SharesResponse("share-a1")
            : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":\"AuthorizationFailed\"}") });
        var provider = CreateProvider(httpClient: http, configMock: configMock, registry: registry);

        var result = (await provider.ListResourcesAsync()).ToList();

        result.Should().ContainSingle(r => r.AccountName == "acct-a" && r.ResourceName == "share-a1");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task ListResources_SkipsAnAccountWhoseTenantIsNotSignedIn()
    {
        // Only tenant-a is signed in; acct-b's listing must never even be attempted.
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            [TenantsKey] = TenantStoreJson("tenant-a", "rt-a")
        });
        var registry = CreateRegistry(configMock);
        await registry.UpsertAsync(new[] { StorageEntry("acct-a", "tenant-a"), StorageEntry("acct-b", "tenant-b") });
        var http = TwoTenantHttp(account => account == "acct-a"
            ? SharesResponse("share-a1")
            : throw new Xunit.Sdk.XunitException("acct-b must not be listed without a signed-in tenant"));
        var provider = CreateProvider(httpClient: http, configMock: configMock, registry: registry);

        var result = (await provider.ListResourcesAsync()).ToList();

        result.Select(r => r.AccountName).Should().Equal("acct-a");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task ListResources_IgnoresDisabledAccounts()
    {
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            [TenantsKey] = TenantStoreJson(("tenant-a", "rt-a"), ("tenant-b", "rt-b"))
        });
        var registry = CreateRegistry(configMock);
        await registry.UpsertAsync(new[] { StorageEntry("acct-a", "tenant-a"), StorageEntry("acct-b", "tenant-b", enabled: false) });
        var http = TwoTenantHttp(account => account == "acct-a"
            ? SharesResponse("share-a1")
            : throw new Xunit.Sdk.XunitException("a disabled account must not be listed"));
        var provider = CreateProvider(httpClient: http, configMock: configMock, registry: registry);

        var result = (await provider.ListResourcesAsync()).ToList();

        result.Select(r => r.AccountName).Should().Equal("acct-a");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task ShareOperations_Throw_WhenTheAccountIsNotEnabled()
    {
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            [TenantsKey] = TenantStoreJson("tenant-b", "rt-b")
        });
        var registry = CreateRegistry(configMock);
        await registry.UpsertAsync(new[] { StorageEntry("acct-b", "tenant-b", enabled: false) });
        var provider = CreateProvider(configMock: configMock, registry: registry);

        var disabled = () => provider.EnumerateFilesAsync("acct-b", "share", "/", recursive: false);
        await disabled.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Storage account acct-b is not enabled. Run pks fileshare init");

        var unknown = () => provider.DeleteFilesAsync("nosuch", "share", new[] { "x.txt" });
        await unknown.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Storage account nosuch is not enabled. Run pks fileshare init");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public async Task GetAccessToken_UsesTheTenantOfTheNamedAccount()
    {
        var configMock = CreateConfigServiceMock(new Dictionary<string, string>
        {
            [TenantsKey] = TenantStoreJson(("tenant-a", "rt-a"), ("tenant-b", "rt-b"))
        });
        var registry = CreateRegistry(configMock);
        await registry.UpsertAsync(new[] { StorageEntry("acct-a", "tenant-a"), StorageEntry("acct-b", "tenant-b") });
        var provider = CreateProvider(httpClient: TwoTenantHttp(_ => SharesResponse()), configMock: configMock, registry: registry);

        (await provider.GetAccessTokenAsync("acct-b", "https://storage.azure.com/.default")).Should().Be("token-b");
        (await provider.GetAccessTokenAsync("acct-a", "https://storage.azure.com/.default")).Should().Be("token-a");
    }
}
