using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Security;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.Azure;

/// <summary>
/// The tenant-keyed credential store behind <c>pks loganalytics/appinsights/storage</c>. Every
/// token literal here is obviously fake; the config mock is dictionary-backed so a test can read
/// what the store persisted, and the token cache gets a fresh directory per test so nothing is
/// served from a previous test's refresh.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public sealed class AzureTenantCredentialStoreTests : IDisposable
{
    private const string TenantsKey = "azure.tenants.credentials";
    private const string FileShareKey = "fileshare.azure.credentials";
    private const string FoundryKey = "foundry.auth.credentials";
    private const string ManagementScope = "https://management.azure.com/.default";

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "pks-cli-tests", "tenant-store", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _settings = new();
    private readonly Mock<IConfigurationService> _config;
    private readonly List<HttpRequestMessage> _requests = new();
    private readonly List<string> _requestBodies = new();
    private Func<HttpRequestMessage, string, HttpResponseMessage> _respond = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound);

    public AzureTenantCredentialStoreTests()
    {
        _config = new Mock<IConfigurationService>();
        _config.Setup(x => x.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => _settings.TryGetValue(key, out var v) ? v : null);
        _config.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<string, string, bool, bool>((k, v, _, _) => _settings[k] = v)
            .Returns(Task.CompletedTask);
        _config.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .Callback<string>(k => _settings.Remove(k))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly AzureTenantCredentialStoreTests _owner;
        public Handler(AzureTenantCredentialStoreTests owner) => _owner = owner;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            _owner._requests.Add(request);
            _owner._requestBodies.Add(body);
            return _owner._respond(request, body);
        }
    }

    private AzureTenantCredentialStore CreateStore() => new(
        new HttpClient(new Handler(this)),
        _config.Object,
        FakeSecretResolver.BackedBy(_config.Object.GetAsync),
        new AzureTokenCache(_cacheDir),
        new Mock<ILogger<AzureTenantCredentialStore>>().Object);

    private void SeedTenants(params AzureTenantCredentials[] tenants)
        => _settings[TenantsKey] = JsonSerializer.Serialize(tenants.ToList(), SecretJson.Persistence);

    private static AzureTenantCredentials Tenant(string id, string refreshToken, string? email = null) => new()
    {
        TenantId = id,
        Email = email,
        RefreshToken = SecretValue.From(refreshToken),
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        LastRefreshedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
    };

    private List<AzureTenantCredentials> StoredTenants()
        => JsonSerializer.Deserialize<List<AzureTenantCredentials>>(_settings[TenantsKey], SecretJson.Persistence)!;

    private static HttpResponseMessage TokenResponse(string accessToken, string? refreshToken, int expiresIn = 3600) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            refresh_token = refreshToken,
            expires_in = expiresIn,
            token_type = "Bearer"
        }))
    };

    private void SeedFileShare(string tenantId, string refreshToken, string account = "mystorage")
        => _settings[FileShareKey] = JsonSerializer.Serialize(new FileShareStoredCredentials
        {
            TenantId = tenantId,
            RefreshToken = refreshToken,
            SelectedSubscriptionId = "sub-123",
            SelectedSubscriptionName = "My Subscription",
            SelectedStorageAccountName = account,
            SelectedStorageAccountResourceGroup = "my-rg",
            CreatedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            LastRefreshedAt = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc)
        });

    // ── Round trip ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTenants_RoundTripsTwoTenants()
    {
        SeedTenants(Tenant("tenant-a", "fake-rt-a", "a@example.com"), Tenant("tenant-b", "fake-rt-b"));
        var store = CreateStore();

        var tenants = await store.ListTenantsAsync();

        tenants.Select(t => t.TenantId).Should().BeEquivalentTo(new[] { "tenant-a", "tenant-b" });
        tenants.Single(t => t.TenantId == "tenant-a").Email.Should().Be("a@example.com");
        (await store.HasTenantAsync("tenant-a")).Should().BeTrue();
        (await store.HasTenantAsync("tenant-c")).Should().BeFalse();
    }

    [Fact]
    public async Task ListTenants_IsEmpty_WhenNothingStored()
    {
        var store = CreateStore();

        (await store.ListTenantsAsync()).Should().BeEmpty();
        (await store.HasTenantAsync("tenant-a")).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveTenant_RemovesOnlyThatTenant()
    {
        SeedTenants(Tenant("tenant-a", "fake-rt-a"), Tenant("tenant-b", "fake-rt-b"));
        var store = CreateStore();

        await store.RemoveTenantAsync("tenant-a");

        (await store.ListTenantsAsync()).Select(t => t.TenantId).Should().Equal("tenant-b");
        _settings[TenantsKey].Should().NotContain("fake-rt-a");
        _settings[TenantsKey].Should().Contain("fake-rt-b");
    }

    // ── GetAccessTokenAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetAccessToken_RedeemsTheTenantsRefreshToken_AndPersistsRotationForThatTenantOnly()
    {
        SeedTenants(Tenant("tenant-a", "fake-rt-a"), Tenant("tenant-b", "fake-rt-b"));
        _respond = (_, _) => TokenResponse("fake-at-a", "fake-rt-a-rotated");
        var store = CreateStore();

        var token = await store.GetAccessTokenAsync("tenant-a", ManagementScope);

        token.Should().Be("fake-at-a");
        _requests.Should().ContainSingle();
        _requests[0].Method.Should().Be(HttpMethod.Post);
        _requests[0].RequestUri!.ToString().Should().Be("https://login.microsoftonline.com/tenant-a/oauth2/v2.0/token");
        _requestBodies[0].Should().Contain("refresh_token=fake-rt-a").And.Contain("grant_type=refresh_token");

        var stored = StoredTenants();
        stored.Single(t => t.TenantId == "tenant-a").RefreshToken.Should().Be(SecretValue.From("fake-rt-a-rotated"));
        stored.Single(t => t.TenantId == "tenant-b").RefreshToken.Should().Be(SecretValue.From("fake-rt-b"));
        _config.Verify(x => x.SetAsync(TenantsKey, It.Is<string>(j => j.Contains("fake-rt-a-rotated")), true, true), Times.Once);
    }

    [Fact]
    public async Task GetAccessToken_SecondCall_IsServedFromCache()
    {
        SeedTenants(Tenant("tenant-a", "fake-rt-a"));
        _respond = (_, _) => TokenResponse("fake-at-a", "fake-rt-a-rotated");
        var store = CreateStore();

        var first = await store.GetAccessTokenAsync("tenant-a", ManagementScope);
        var second = await store.GetAccessTokenAsync("tenant-a", ManagementScope);

        first.Should().Be("fake-at-a");
        second.Should().Be("fake-at-a");
        _requests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAccessToken_DeadRefreshToken_ThrowsAuthExpired_NamingTheTenant()
    {
        SeedTenants(Tenant("tenant-a", "fake-rt-a"));
        _respond = (_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS700082: The refresh token has expired\"}")
        };
        var store = CreateStore();

        var act = () => store.GetAccessTokenAsync("tenant-a", ManagementScope);

        var ex = (await act.Should().ThrowAsync<AzureAuthExpiredException>()).Which;
        ex.TenantId.Should().Be("tenant-a");
        ex.Message.Should().Contain("pks loganalytics init --reauth tenant-a");
    }

    [Fact]
    public async Task GetAccessToken_UnknownTenant_ThrowsAuthExpired()
    {
        SeedTenants(Tenant("tenant-a", "fake-rt-a"));
        var store = CreateStore();

        var act = () => store.GetAccessTokenAsync("tenant-z", ManagementScope);

        (await act.Should().ThrowAsync<AzureAuthExpiredException>()).Which.TenantId.Should().Be("tenant-z");
        _requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAccessToken_TransientFailure_DoesNotThrowAuthExpired()
    {
        SeedTenants(Tenant("tenant-a", "fake-rt-a"));
        _respond = (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":\"temporarily_unavailable\"}") };
        var store = CreateStore();

        var act = () => store.GetAccessTokenAsync("tenant-a", ManagementScope);

        var ex = (await act.Should().ThrowAsync<AzureTokenEndpointException>()).Which;
        ex.Should().NotBeOfType<AzureRefreshTokenExpiredException>();
    }

    // ── ResolveTenantAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ResolveTenant_ReturnsTheOnlyTenant_WhenNoneGiven()
    {
        SeedTenants(Tenant("tenant-a", "fake-rt-a"));
        var store = CreateStore();

        (await store.ResolveTenantAsync(null)).Should().Be("tenant-a");
        (await store.ResolveTenantAsync("")).Should().Be("tenant-a");
    }

    [Fact]
    public async Task ResolveTenant_Throws_WhenNoneKnown()
    {
        var store = CreateStore();

        var act = () => store.ResolveTenantAsync(null);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("pks loganalytics init");
    }

    [Fact]
    public async Task ResolveTenant_Throws_WhenSeveralKnown_ListingThem()
    {
        SeedTenants(Tenant("tenant-a", "fake-rt-a"), Tenant("tenant-b", "fake-rt-b"));
        var store = CreateStore();

        var act = () => store.ResolveTenantAsync(null);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("tenant-a").And.Contain("tenant-b").And.NotContain("fake-rt");
    }

    [Fact]
    public async Task ResolveTenant_ReturnsGivenTenant_WhenKnown_AndThrowsWhenNot()
    {
        SeedTenants(Tenant("tenant-a", "fake-rt-a"), Tenant("tenant-b", "fake-rt-b"));
        var store = CreateStore();

        (await store.ResolveTenantAsync("tenant-b")).Should().Be("tenant-b");
        var act = () => store.ResolveTenantAsync("tenant-z");
        (await act.Should().ThrowAsync<AzureAuthExpiredException>()).Which.TenantId.Should().Be("tenant-z");
    }

    // ── Fileshare migration ─────────────────────────────────────────────────

    [Fact]
    public async Task Migration_MovesFileShareToken_AndBlanksIt_WithoutDeletingTheKey()
    {
        SeedFileShare("tenant-fs", "fake-fs-rt");
        var store = CreateStore();

        (await store.HasTenantAsync("tenant-fs")).Should().BeTrue();

        _settings[TenantsKey].Should().Contain("fake-fs-rt");
        _settings.Should().ContainKey(FileShareKey);
        var fileShare = JsonSerializer.Deserialize<FileShareStoredCredentials>(_settings[FileShareKey])!;
        fileShare.RefreshToken.Should().BeEmpty();
        fileShare.TenantId.Should().Be("tenant-fs");
        fileShare.SelectedSubscriptionId.Should().Be("sub-123");
        fileShare.SelectedSubscriptionName.Should().Be("My Subscription");
        fileShare.SelectedStorageAccountName.Should().Be("mystorage");
        fileShare.SelectedStorageAccountResourceGroup.Should().Be("my-rg");
        fileShare.CreatedAt.Should().Be(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        _config.Verify(x => x.DeleteAsync(It.IsAny<string>()), Times.Never);

        var migrated = (await store.ListTenantsAsync()).Single();
        migrated.TenantId.Should().Be("tenant-fs");
        migrated.CreatedAt.Should().Be(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Migration_RunsOnFirstAccess_ThroughAnyMethod()
    {
        SeedFileShare("tenant-fs", "fake-fs-rt");
        var store = CreateStore();

        var tenants = await store.ListTenantsAsync();

        tenants.Select(t => t.TenantId).Should().Equal("tenant-fs");
        JsonSerializer.Deserialize<FileShareStoredCredentials>(_settings[FileShareKey])!.RefreshToken.Should().BeEmpty();
    }

    [Fact]
    public async Task Migration_RedeemsTheMovedToken()
    {
        SeedFileShare("tenant-fs", "fake-fs-rt");
        _respond = (_, _) => TokenResponse("fake-at-fs", "fake-fs-rt-rotated");
        var store = CreateStore();

        var token = await store.GetAccessTokenAsync("tenant-fs", "https://storage.azure.com/.default");

        token.Should().Be("fake-at-fs");
        _requestBodies[0].Should().Contain("refresh_token=fake-fs-rt");
        StoredTenants().Single().RefreshToken.Should().Be(SecretValue.From("fake-fs-rt-rotated"));
        _settings[FileShareKey].Should().NotContain("fake-fs-rt");
    }

    [Fact]
    public async Task Migration_IsNoOp_WhenTenantAlreadyKnown()
    {
        SeedTenants(Tenant("tenant-fs", "fake-rt-existing"));
        SeedFileShare("tenant-fs", "fake-fs-rt-2");
        var fileShareBefore = _settings[FileShareKey];
        var store = CreateStore();

        (await store.HasTenantAsync("tenant-fs")).Should().BeTrue();

        _settings[FileShareKey].Should().Be(fileShareBefore);
        StoredTenants().Single().RefreshToken.Should().Be(SecretValue.From("fake-rt-existing"));
        _config.Verify(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Migration_IsNoOp_WhenFileShareHasNoToken()
    {
        SeedFileShare("tenant-fs", "");
        var fileShareBefore = _settings[FileShareKey];
        var store = CreateStore();

        (await store.HasTenantAsync("tenant-fs")).Should().BeFalse();

        _settings[FileShareKey].Should().Be(fileShareBefore);
        _settings.Should().NotContainKey(TenantsKey);
    }

    [Fact]
    public async Task Migration_LeavesFoundryCredentialsAlone()
    {
        var foundry = JsonSerializer.Serialize(new FoundryStoredCredentials
        {
            TenantId = "tenant-foundry",
            RefreshToken = SecretValue.From("fake-foundry-rt"),
            SelectedResourceName = "my-foundry"
        }, SecretJson.Persistence);
        _settings[FoundryKey] = foundry;
        var store = CreateStore();

        (await store.ListTenantsAsync()).Should().BeEmpty();

        _settings[FoundryKey].Should().Be(foundry);
        _settings.Should().NotContainKey(TenantsKey);
    }
}
