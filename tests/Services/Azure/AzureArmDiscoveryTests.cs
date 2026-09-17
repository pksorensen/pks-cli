using System.Net;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Azure;

/// <summary>
/// ARM listings that take a tenant id instead of a bearer token: the token comes from the tenant
/// store, the requests go to the same endpoints the Foundry service has always used.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public sealed class AzureArmDiscoveryTests
{
    private const string ManagementScope = "https://management.azure.com/.default";
    private readonly List<HttpRequestMessage> _requests = new();
    private Func<HttpRequestMessage, HttpResponseMessage> _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    private sealed class Handler : HttpMessageHandler
    {
        private readonly AzureArmDiscoveryTests _owner;
        public Handler(AzureArmDiscoveryTests owner) => _owner = owner;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _owner._requests.Add(request);
            return Task.FromResult(_owner._respond(request));
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body))
    };

    private (AzureArmDiscovery Discovery, Mock<IAzureTenantCredentialStore> Store) Create()
    {
        var store = new Mock<IAzureTenantCredentialStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAccessTokenAsync("tenant-a", ManagementScope, It.IsAny<CancellationToken>()))
            .ReturnsAsync("fake-arm-token-a");
        var discovery = new AzureArmDiscovery(new HttpClient(new Handler(this)), store.Object);
        return (discovery, store);
    }

    private void AssertSingleGet(string expectedUrl)
    {
        _requests.Should().ContainSingle();
        _requests[0].Method.Should().Be(HttpMethod.Get);
        _requests[0].RequestUri!.ToString().Should().Be(expectedUrl);
        _requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        _requests[0].Headers.Authorization!.Parameter.Should().Be("fake-arm-token-a");
    }

    [Fact]
    public async Task ListSubscriptions_UsesTheTenantsToken()
    {
        _respond = _ => Json(new AzureSubscriptionListResponse
        {
            Value = { new AzureSubscription { SubscriptionId = "sub-1", DisplayName = "One", State = "Enabled", TenantId = "tenant-a" } }
        });
        var (discovery, _) = Create();

        var subs = await discovery.ListSubscriptionsAsync("tenant-a");

        subs.Should().ContainSingle().Which.SubscriptionId.Should().Be("sub-1");
        AssertSingleGet("https://management.azure.com/subscriptions?api-version=2022-12-01");
    }

    [Fact]
    public async Task ListLogAnalyticsWorkspaces_HitsTheOperationalInsightsProvider()
    {
        _respond = _ => Json(new LogAnalyticsWorkspaceListResponse
        {
            Value = { new LogAnalyticsWorkspace { Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/ws", Name = "ws" } }
        });
        var (discovery, _) = Create();

        var workspaces = await discovery.ListLogAnalyticsWorkspacesAsync("tenant-a", "sub-1");

        workspaces.Should().ContainSingle().Which.Name.Should().Be("ws");
        AssertSingleGet("https://management.azure.com/subscriptions/sub-1/providers/Microsoft.OperationalInsights/workspaces?api-version=2022-10-01");
    }

    [Fact]
    public async Task ListAppInsightsResources_HitsTheInsightsProvider()
    {
        _respond = _ => Json(new AppInsightsComponentListResponse
        {
            Value = { new AppInsightsComponent { Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Insights/components/ai", Name = "ai" } }
        });
        var (discovery, _) = Create();

        var components = await discovery.ListAppInsightsResourcesAsync("tenant-a", "sub-1");

        components.Should().ContainSingle().Which.Name.Should().Be("ai");
        AssertSingleGet("https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Insights/components?api-version=2020-02-02");
    }

    [Fact]
    public async Task ListStorageAccounts_ExcludesBlobOnlyAccounts()
    {
        _respond = _ => Json(new StorageAccountListResponse
        {
            Value =
            {
                new StorageAccountInfo { Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/files", Name = "files", Kind = "StorageV2" },
                new StorageAccountInfo { Id = "/subscriptions/sub-1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/blobs", Name = "blobs", Kind = "BlobStorage" }
            }
        });
        var (discovery, _) = Create();

        var accounts = await discovery.ListStorageAccountsAsync("tenant-a", "sub-1");

        accounts.Should().ContainSingle().Which.Name.Should().Be("files");
        AssertSingleGet("https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Storage/storageAccounts?api-version=2023-01-01");
    }

    [Fact]
    public async Task AnyListing_PropagatesAuthExpired_FromTheStore()
    {
        var store = new Mock<IAzureTenantCredentialStore>(MockBehavior.Strict);
        store.Setup(s => s.GetAccessTokenAsync("tenant-dead", ManagementScope, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AzureAuthExpiredException("tenant-dead"));
        var discovery = new AzureArmDiscovery(new HttpClient(new Handler(this)), store.Object);

        var act = () => discovery.ListSubscriptionsAsync("tenant-dead");

        (await act.Should().ThrowAsync<AzureAuthExpiredException>()).Which.TenantId.Should().Be("tenant-dead");
        _requests.Should().BeEmpty();
    }
}
