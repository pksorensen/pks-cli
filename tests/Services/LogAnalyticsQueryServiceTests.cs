using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services;

[Trait("Category", "LogAnalytics")]
public class LogAnalyticsQueryServiceTests
{
    private const string Scope = "https://api.loganalytics.io/.default";

    private static Mock<ILogAnalyticsConfigService> CreateConfigMock(bool isConfigured = true)
    {
        var mock = new Mock<ILogAnalyticsConfigService>();
        mock.Setup(m => m.IsConfiguredAsync()).ReturnsAsync(isConfigured);
        if (isConfigured)
        {
            mock.Setup(m => m.GetConfigAsync()).ReturnsAsync(new LogAnalyticsConfig
            {
                WorkspaceId = "configured-workspace",
                WorkspaceName = "law-test"
            });
        }
        return mock;
    }

    private static Mock<IAzureFoundryAuthService> CreateAuthMock(string? token = "test-bearer-token")
    {
        var mock = new Mock<IAzureFoundryAuthService>();
        mock.Setup(m => m.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(token);
        return mock;
    }

    private static Mock<ILogAnalyticsHttpAdapter> CreateHttpMock(KustoQueryResponse? response = null)
    {
        var mock = new Mock<ILogAnalyticsHttpAdapter>();
        mock.Setup(m => m.QueryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response ?? new KustoQueryResponse());
        return mock;
    }

    /// <summary>A tenant store that knows the given tenants and hands out "token-{tenant}" for each.</summary>
    private static Mock<IAzureTenantCredentialStore> CreateTenantStoreMock(params string[] tenantIds)
    {
        var mock = new Mock<IAzureTenantCredentialStore>();
        mock.Setup(m => m.ListTenantsAsync())
            .ReturnsAsync(tenantIds.Select(t => new AzureTenantInfo(t, t + "-name", "user@example.com", DateTime.UtcNow, DateTime.UtcNow)).ToList());
        mock.Setup(m => m.ResolveTenantAsync(It.IsAny<string?>()))
            .Returns<string?>(t => t is not null
                ? Task.FromResult(t)
                : tenantIds.Length == 1
                    ? Task.FromResult(tenantIds[0])
                    : Task.FromException<string>(new InvalidOperationException(
                        tenantIds.Length == 0 ? "No Azure tenant is signed in." : "Several Azure tenants are signed in. Pass --tenant <id>.")));
        mock.Setup(m => m.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((t, _, _) => Task.FromResult("token-" + t));
        return mock;
    }

    private static Mock<IAzureResourceRegistry> CreateRegistryMock(params AzureResourceEntry[] entries)
    {
        var mock = new Mock<IAzureResourceRegistry>();
        mock.Setup(m => m.ListEnabledAsync(It.IsAny<AzureResourceKind>()))
            .Returns<AzureResourceKind>(k => Task.FromResult<IReadOnlyList<AzureResourceEntry>>(
                entries.Where(e => e.Kind == k && e.Enabled).ToList()));
        mock.Setup(m => m.FindAsync(It.IsAny<AzureResourceKind>(), It.IsAny<string>()))
            .Returns<AzureResourceKind, string>((k, n) => Task.FromResult(entries.FirstOrDefault(e =>
                e.Kind == k && (string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(e.Key, n, StringComparison.OrdinalIgnoreCase)))));
        return mock;
    }

    private static AzureResourceEntry Workspace(string name, string key, string? tenant, bool enabled = true) => new()
    {
        Kind = AzureResourceKind.LogAnalytics,
        Name = name,
        Key = key,
        TenantId = tenant,
        Enabled = enabled
    };

    private static LogAnalyticsQueryService CreateService(
        Mock<ILogAnalyticsConfigService>? configMock = null,
        Mock<ILogAnalyticsHttpAdapter>? httpMock = null,
        Mock<IAzureFoundryAuthService>? authMock = null,
        Mock<IAzureTenantCredentialStore>? tenantMock = null,
        Mock<IAzureResourceRegistry>? registryMock = null)
        => new(
            (configMock ?? CreateConfigMock()).Object,
            (httpMock ?? CreateHttpMock()).Object,
            (authMock ?? CreateAuthMock()).Object,
            (tenantMock ?? CreateTenantStoreMock()).Object,
            (registryMock ?? CreateRegistryMock()).Object);

    // ── Single-workspace path (legacy, Foundry fallback when the tenant store is empty) ──

    [Fact]
    public async Task QueryAsync_UsesConfiguredWorkspace_AndFallsBackToFoundry_WhenNoTenantSignedIn()
    {
        var http = CreateHttpMock();
        var auth = CreateAuthMock();
        var svc = CreateService(httpMock: http, authMock: auth);

        await svc.QueryAsync("Heartbeat | take 1");

        http.Verify(m => m.QueryAsync(
            "configured-workspace", "test-bearer-token", "Heartbeat | take 1", null, It.IsAny<CancellationToken>()), Times.Once);
        auth.Verify(m => m.GetAccessTokenAsync(Scope, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueryAsync_UsesTenantStoreToken_WhenWorkspaceIsRegisteredWithTenant()
    {
        var http = CreateHttpMock();
        var auth = CreateAuthMock();
        var tenants = CreateTenantStoreMock("t1");
        var registry = CreateRegistryMock(Workspace("law-test", "configured-workspace", "t1"));
        var svc = CreateService(httpMock: http, authMock: auth, tenantMock: tenants, registryMock: registry);

        await svc.QueryAsync("Heartbeat | take 1");

        http.Verify(m => m.QueryAsync(
            "configured-workspace", "token-t1", "Heartbeat | take 1", null, It.IsAny<CancellationToken>()), Times.Once);
        tenants.Verify(m => m.GetAccessTokenAsync("t1", Scope, It.IsAny<CancellationToken>()), Times.Once);
        auth.Verify(m => m.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueryAsync_PrefersWorkspaceOverride_WithoutReadingConfig()
    {
        var config = CreateConfigMock();
        var http = CreateHttpMock();
        var svc = CreateService(config, http);

        await svc.QueryAsync("Heartbeat | take 1", workspaceIdOverride: "other-workspace");

        http.Verify(m => m.QueryAsync(
            "other-workspace", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        config.Verify(m => m.GetConfigAsync(), Times.Never);
    }

    [Fact]
    public async Task QueryAsync_SendsSinceAsIso8601Timespan()
    {
        var http = CreateHttpMock();
        var svc = CreateService(httpMock: http);

        await svc.QueryAsync("Heartbeat", TimeSpan.FromHours(6));

        http.Verify(m => m.QueryAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), "PT6H", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueryAsync_Throws_WhenNotConfigured()
    {
        var svc = CreateService(CreateConfigMock(isConfigured: false));

        var act = () => svc.QueryAsync("Heartbeat");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*loganalytics init*");
    }

    [Fact]
    public async Task QueryAsync_Throws_WhenNotAuthenticated()
    {
        var svc = CreateService(authMock: CreateAuthMock(token: null));

        var act = () => svc.QueryAsync("Heartbeat");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Not authenticated*");
    }

    [Fact]
    public async Task QueryAsync_Throws_OnEmptyQuery()
    {
        var svc = CreateService();

        var act = () => svc.QueryAsync("   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task QueryAsync_SurfacesAuthExpired_ForTheWorkspaceTenant()
    {
        var tenants = CreateTenantStoreMock("t1");
        tenants.Setup(m => m.GetAccessTokenAsync("t1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AzureAuthExpiredException("t1"));
        var registry = CreateRegistryMock(Workspace("law-test", "configured-workspace", "t1"));
        var svc = CreateService(tenantMock: tenants, registryMock: registry);

        var act = () => svc.QueryAsync("Heartbeat");

        var ex = await act.Should().ThrowAsync<AzureAuthExpiredException>();
        ex.Which.TenantId.Should().Be("t1");
    }

    // ── Fan-out ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryManyAsync_QueriesEveryWorkspace_WithItsOwnTenantToken()
    {
        var http = CreateHttpMock();
        var tenants = CreateTenantStoreMock("t1", "t2");
        var svc = CreateService(httpMock: http, tenantMock: tenants);
        var targets = new[] { Workspace("law-a", "guid-a", "t1"), Workspace("law-b", "guid-b", "t2") };

        var results = await svc.QueryManyAsync(targets, "Heartbeat | take 1", TimeSpan.FromHours(1));

        results.Should().HaveCount(2);
        results.Select(r => r.Workspace.Name).Should().Equal("law-a", "law-b");
        results.Should().OnlyContain(r => r.Succeeded);
        http.Verify(m => m.QueryAsync("guid-a", "token-t1", "Heartbeat | take 1", "PT1H", It.IsAny<CancellationToken>()), Times.Once);
        http.Verify(m => m.QueryAsync("guid-b", "token-t2", "Heartbeat | take 1", "PT1H", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueryManyAsync_CapturesOneWorkspaceFailure_AndStillReturnsTheOther()
    {
        var http = new Mock<ILogAnalyticsHttpAdapter>();
        http.Setup(m => m.QueryAsync("guid-a", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LogAnalyticsQueryException("Log Analytics query failed (HTTP 500): boom"));
        http.Setup(m => m.QueryAsync("guid-b", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KustoQueryResponse { Tables = [new KustoTable { Name = "PrimaryResult" }] });
        var svc = CreateService(httpMock: http, tenantMock: CreateTenantStoreMock("t1"));
        var targets = new[] { Workspace("law-a", "guid-a", "t1"), Workspace("law-b", "guid-b", "t1") };

        var results = await svc.QueryManyAsync(targets, "Heartbeat", null);

        results.Should().HaveCount(2);
        results[0].Succeeded.Should().BeFalse();
        results[0].Error.Should().BeOfType<LogAnalyticsQueryException>().Which.Message.Should().Contain("HTTP 500");
        results[1].Succeeded.Should().BeTrue();
        results[1].Response!.Tables.Should().ContainSingle();
    }

    [Fact]
    public async Task QueryManyAsync_SurfacesAuthExpired_PerWorkspace()
    {
        var tenants = CreateTenantStoreMock("t1", "t2");
        tenants.Setup(m => m.GetAccessTokenAsync("t2", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AzureAuthExpiredException("t2"));
        var svc = CreateService(tenantMock: tenants);
        var targets = new[] { Workspace("law-a", "guid-a", "t1"), Workspace("law-b", "guid-b", "t2") };

        var results = await svc.QueryManyAsync(targets, "Heartbeat", null);

        results[0].Succeeded.Should().BeTrue();
        results[1].Error.Should().BeOfType<AzureAuthExpiredException>().Which.TenantId.Should().Be("t2");
    }

    [Fact]
    public async Task QueryManyAsync_FallsBackToFoundry_OnlyWhenTenantStoreIsEmpty()
    {
        var http = CreateHttpMock();
        var auth = CreateAuthMock("foundry-token");
        var tenants = CreateTenantStoreMock();
        var svc = CreateService(httpMock: http, authMock: auth, tenantMock: tenants);

        var results = await svc.QueryManyAsync(new[] { Workspace("law-a", "guid-a", null) }, "Heartbeat", null);

        results.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
        http.Verify(m => m.QueryAsync("guid-a", "foundry-token", "Heartbeat", null, It.IsAny<CancellationToken>()), Times.Once);
        tenants.Verify(m => m.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueryManyAsync_ExplainsLegacyEntryWithoutTenant_WhenSeveralTenantsSignedIn()
    {
        var svc = CreateService(tenantMock: CreateTenantStoreMock("t1", "t2"));

        var results = await svc.QueryManyAsync(new[] { Workspace("law-old", "guid-old", null) }, "Heartbeat", null);

        results.Should().ContainSingle().Which.Error.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("law-old").And.Contain("loganalytics init");
    }

    [Fact]
    public async Task QueryManyAsync_ReturnsEmpty_ForNoWorkspaces()
    {
        var http = CreateHttpMock();
        var svc = CreateService(httpMock: http);

        var results = await svc.QueryManyAsync(Array.Empty<AzureResourceEntry>(), "Heartbeat", null);

        results.Should().BeEmpty();
        http.VerifyNoOtherCalls();
    }

    // ── TestConnection ───────────────────────────────────────────────────────

    [Fact]
    public async Task TestConnectionAsync_ReturnsFailure_WhenQueryRejected()
    {
        var http = new Mock<ILogAnalyticsHttpAdapter>();
        http.Setup(m => m.QueryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LogAnalyticsQueryException("boom"));

        var result = await CreateService(httpMock: http).TestConnectionAsync();

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public async Task TestConnectionAsync_ForEntry_UsesThatWorkspaceAndTenant()
    {
        var http = CreateHttpMock();
        var svc = CreateService(httpMock: http, tenantMock: CreateTenantStoreMock("t1", "t2"));

        var result = await svc.TestConnectionAsync(Workspace("law-b", "guid-b", "t2"));

        result.Success.Should().BeTrue();
        result.WorkspaceName.Should().Be("law-b");
        http.Verify(m => m.QueryAsync("guid-b", "token-t2", It.IsAny<string>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Formatting helpers ───────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void FormatTimespan_ReturnsNull_ForNoWindow(int? minutes)
    {
        var since = minutes is null ? (TimeSpan?)null : TimeSpan.FromMinutes(minutes.Value);
        LogAnalyticsQueryService.FormatTimespan(since).Should().BeNull();
    }

    [Fact]
    public void FormatTimespan_UsesIso8601Durations()
    {
        LogAnalyticsQueryService.FormatTimespan(TimeSpan.FromHours(1)).Should().Be("PT1H");
        LogAnalyticsQueryService.FormatTimespan(TimeSpan.FromDays(7)).Should().Be("P7D");
        // 24h normalises to a day-component duration — still valid ISO 8601 for the API.
        LogAnalyticsQueryService.FormatTimespan(TimeSpan.FromHours(24)).Should().Be("P1D");
        LogAnalyticsQueryService.FormatTimespan(TimeSpan.FromMinutes(30)).Should().Be("PT30M");
    }

    [Fact]
    public void FormatApiError_SurfacesInnermostKustoDiagnostic()
    {
        // Shape returned live by api.loganalytics.io for a KQL syntax error.
        const string body = """
            {"error":{"message":"The request had some invalid properties","code":"BadArgumentError",
            "innererror":{"code":"SyntaxError","message":"A recognition error occurred in the query.",
            "innererror":{"code":"SYN0002","message":"Query could not be parsed at 'wher' on line [1,13]","line":1,"pos":13}}}}
            """;

        var message = LogAnalyticsQueryService.FormatApiError(400, body);

        message.Should().Contain("HTTP 400");
        message.Should().Contain("BadArgumentError");
        message.Should().Contain("SYN0002");
        message.Should().Contain("Query could not be parsed at 'wher' on line [1,13]");
    }

    [Fact]
    public void FormatApiError_FallsBackToRawBody_WhenNotJson()
    {
        LogAnalyticsQueryService.FormatApiError(503, "upstream unavailable")
            .Should().Contain("upstream unavailable");
    }

    [Fact]
    public void FormatApiError_HandlesEmptyBody()
    {
        LogAnalyticsQueryService.FormatApiError(401, null).Should().Contain("HTTP 401");
    }
}
