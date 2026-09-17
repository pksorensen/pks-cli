using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services;

[Trait("Category", "AppInsights")]
public class AppInsightsQueryServiceTests
{
    private static Mock<IAppInsightsConfigService> CreateConfigMock(bool isConfigured = true)
    {
        var mock = new Mock<IAppInsightsConfigService>();
        mock.Setup(m => m.IsConfiguredAsync()).ReturnsAsync(isConfigured);
        if (isConfigured)
        {
            mock.Setup(m => m.GetConfigAsync()).ReturnsAsync(new AppInsightsConfig
            {
                AppId = "test-app-id",
                ResourceName = "Test Resource"
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

    private static Mock<IAppInsightsHttpAdapter> CreateHttpMock(AppInsightsQueryResponse? response = null)
    {
        var mock = new Mock<IAppInsightsHttpAdapter>();
        mock.Setup(m => m.QueryAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response ?? new AppInsightsQueryResponse());
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
                    : Task.FromException<string>(new InvalidOperationException("Several Azure tenants are signed in. Pass --tenant <id>.")));
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

    private static AzureResourceEntry Resource(string name, string appId, string? tenant, bool enabled = true) => new()
    {
        Kind = AzureResourceKind.AppInsights,
        Name = name,
        Key = appId,
        TenantId = tenant,
        Enabled = enabled
    };

    private static AppInsightsQueryService CreateService(
        Mock<IAppInsightsConfigService>? configMock = null,
        Mock<IAppInsightsHttpAdapter>? httpMock = null,
        Mock<IAzureFoundryAuthService>? authMock = null,
        Mock<IAzureTenantCredentialStore>? tenantMock = null,
        Mock<IAzureResourceRegistry>? registryMock = null)
    {
        return new AppInsightsQueryService(
            (configMock ?? CreateConfigMock()).Object,
            (httpMock ?? CreateHttpMock()).Object,
            (authMock ?? CreateAuthMock()).Object,
            (tenantMock ?? CreateTenantStoreMock()).Object,
            (registryMock ?? CreateRegistryMock()).Object);
    }

    // Helper to build a mock AppInsightsQueryResponse with given rows
    private static AppInsightsQueryResponse MakeResponse(List<string> columns, List<List<object?>> rows)
    {
        var colList = columns.Select(c => new AppInsightsColumn { Name = c, Type = "string" }).ToList();
        var rowList = rows.Select(row =>
            row.Select(v => v switch
            {
                null => System.Text.Json.JsonDocument.Parse("null").RootElement,
                string s => System.Text.Json.JsonDocument.Parse($"\"{s}\"").RootElement,
                double d => System.Text.Json.JsonDocument.Parse(d.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement,
                bool b => System.Text.Json.JsonDocument.Parse(b ? "true" : "false").RootElement,
                DateTimeOffset dt => System.Text.Json.JsonDocument.Parse($"\"{dt:O}\"").RootElement,
                _ => System.Text.Json.JsonDocument.Parse($"\"{v}\"").RootElement
            }).ToList()
        ).ToList();

        return new AppInsightsQueryResponse
        {
            Tables =
            [
                new AppInsightsTable
                {
                    Name = "PrimaryResult",
                    Columns = colList,
                    Rows = rowList
                }
            ]
        };
    }

    [Fact]
    public async Task QueryErrorsAsync_BuildsKql_WithExceptionsTable()
    {
        string? capturedKql = null;
        var httpMock = new Mock<IAppInsightsHttpAdapter>();
        httpMock.Setup(m => m.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, kql, _) => capturedKql = kql)
            .ReturnsAsync(new AppInsightsQueryResponse());

        var svc = CreateService(httpMock: httpMock);
        await svc.QueryErrorsAsync(TimeSpan.FromHours(1), 20);

        capturedKql.Should().Contain("exceptions");
        capturedKql.Should().Contain("order by timestamp desc");
        capturedKql.Should().Contain("take 20");
    }

    [Fact]
    public async Task QueryErrorsAsync_AddsAppNameFilter_WhenProvided()
    {
        string? capturedKql = null;
        var httpMock = new Mock<IAppInsightsHttpAdapter>();
        httpMock.Setup(m => m.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, kql, _) => capturedKql = kql)
            .ReturnsAsync(new AppInsightsQueryResponse());

        var svc = CreateService(httpMock: httpMock);
        await svc.QueryErrorsAsync(TimeSpan.FromHours(1), 20, appName: "my-app");

        capturedKql.Should().Contain("my-app");
        capturedKql.Should().Contain("cloud_RoleName");
    }

    [Fact]
    public async Task QueryErrorsAsync_AddsOperationIdFilter_WhenProvided()
    {
        string? capturedKql = null;
        var httpMock = new Mock<IAppInsightsHttpAdapter>();
        httpMock.Setup(m => m.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, kql, _) => capturedKql = kql)
            .ReturnsAsync(new AppInsightsQueryResponse());

        var svc = CreateService(httpMock: httpMock);
        await svc.QueryErrorsAsync(TimeSpan.FromHours(1), 20, operationId: "op-xyz-123");

        capturedKql.Should().Contain("op-xyz-123");
        capturedKql.Should().Contain("operation_Id");
    }

    [Fact]
    public async Task QueryErrorsAsync_ReturnsEmptyList_WhenNoResults()
    {
        var svc = CreateService(httpMock: CreateHttpMock(new AppInsightsQueryResponse()));
        var result = await svc.QueryErrorsAsync(TimeSpan.FromHours(1), 20);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryErrorsAsync_MapsRows_ToOtelError()
    {
        var ts = DateTimeOffset.UtcNow;
        var response = MakeResponse(
            ["timestamp", "type", "outerMessage", "innermostMessage", "operation_Id", "cloud_RoleName"],
            [[ts, "System.NullReferenceException", "Outer msg", "Inner msg", "op-123", "my-service"]]);

        var svc = CreateService(httpMock: CreateHttpMock(response));
        var result = await svc.QueryErrorsAsync(TimeSpan.FromHours(1), 20);

        result.Should().HaveCount(1);
        result[0].ExceptionType.Should().Be("System.NullReferenceException");
        result[0].Message.Should().Be("Inner msg");
        result[0].OuterMessage.Should().Be("Outer msg");
        result[0].OperationId.Should().Be("op-123");
        result[0].AppName.Should().Be("my-service");
    }

    [Fact]
    public async Task QueryTracesAsync_AddsHasErrorFilter_WhenFlagSet()
    {
        string? capturedKql = null;
        var httpMock = new Mock<IAppInsightsHttpAdapter>();
        httpMock.Setup(m => m.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, kql, _) => capturedKql = kql)
            .ReturnsAsync(new AppInsightsQueryResponse());

        var svc = CreateService(httpMock: httpMock);
        await svc.QueryTracesAsync(TimeSpan.FromHours(1), 20, hasError: true);

        capturedKql.Should().Contain("success == false");
    }

    [Fact]
    public async Task QueryTracesAsync_MapsRows_ToOtelTrace()
    {
        var ts = DateTimeOffset.UtcNow;
        var response = MakeResponse(
            ["timestamp", "operation_Id", "name", "cloud_RoleName", "duration", "success", "resultCode"],
            [[ts, "op-456", "GET /api/health", "api-service", 42.5, false, "500"]]);

        var svc = CreateService(httpMock: CreateHttpMock(response));
        var result = await svc.QueryTracesAsync(TimeSpan.FromHours(1), 20);

        result.Should().HaveCount(1);
        result[0].OperationId.Should().Be("op-456");
        result[0].Name.Should().Be("GET /api/health");
        result[0].AppName.Should().Be("api-service");
        result[0].DurationMs.Should().BeApproximately(42.5, 0.001);
        result[0].Success.Should().BeFalse();
        result[0].ResultCode.Should().Be("500");
        result[0].HasError.Should().BeTrue();
    }

    [Fact]
    public async Task QueryLogsAsync_MapsSeverity_ToInteger()
    {
        string? capturedKql = null;
        var httpMock = new Mock<IAppInsightsHttpAdapter>();
        httpMock.Setup(m => m.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, kql, _) => capturedKql = kql)
            .ReturnsAsync(new AppInsightsQueryResponse());

        var svc = CreateService(httpMock: httpMock);
        await svc.QueryLogsAsync(TimeSpan.FromHours(1), severity: "Error");

        capturedKql.Should().Contain("severityLevel >= 3");
    }

    [Fact]
    public async Task QueryLogsAsync_AddsTraceIdFilter_WhenProvided()
    {
        string? capturedKql = null;
        var httpMock = new Mock<IAppInsightsHttpAdapter>();
        httpMock.Setup(m => m.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, kql, _) => capturedKql = kql)
            .ReturnsAsync(new AppInsightsQueryResponse());

        var svc = CreateService(httpMock: httpMock);
        await svc.QueryLogsAsync(TimeSpan.FromHours(1), traceId: "trace-789");

        capturedKql.Should().Contain("trace-789");
        capturedKql.Should().Contain("operation_Id");
    }

    [Fact]
    public async Task QuerySpansAsync_RequiresOperationId_InKql()
    {
        string? capturedKql = null;
        var httpMock = new Mock<IAppInsightsHttpAdapter>();
        httpMock.Setup(m => m.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, kql, _) => capturedKql = kql)
            .ReturnsAsync(new AppInsightsQueryResponse());

        var svc = CreateService(httpMock: httpMock);
        await svc.QuerySpansAsync("op-span-123");

        capturedKql.Should().Contain("dependencies");
        capturedKql.Should().Contain("op-span-123");
        capturedKql.Should().Contain("operation_Id");
    }

    [Fact]
    public async Task QuerySpansAsync_MapsRows_ToOtelSpan()
    {
        var ts = DateTimeOffset.UtcNow;
        var response = MakeResponse(
            ["timestamp", "id", "target", "type", "name", "duration", "success"],
            [[ts, "span-001", "mydb.com", "SQL", "SELECT users", 12.3, true]]);

        var svc = CreateService(httpMock: CreateHttpMock(response));
        var result = await svc.QuerySpansAsync("op-123");

        result.Should().HaveCount(1);
        result[0].SpanId.Should().Be("span-001");
        result[0].Target.Should().Be("mydb.com");
        result[0].Type.Should().Be("SQL");
        result[0].Name.Should().Be("SELECT users");
        result[0].DurationMs.Should().BeApproximately(12.3, 0.001);
        result[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsSuccess_WhenQuerySucceeds()
    {
        var response = MakeResponse(["cloud_RoleName"], [["my-app-name"]]);
        var svc = CreateService(httpMock: CreateHttpMock(response));
        var result = await svc.TestConnectionAsync();
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsFailure_WhenQueryThrows()
    {
        var httpMock = new Mock<IAppInsightsHttpAdapter>();
        httpMock.Setup(m => m.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var svc = CreateService(httpMock: httpMock);
        var result = await svc.TestConnectionAsync();
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Connection refused");
    }

    // ── Token source ─────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryErrorsAsync_UsesTenantStoreToken_WhenResourceRegisteredWithTenant()
    {
        var http = CreateHttpMock();
        var auth = CreateAuthMock();
        var tenants = CreateTenantStoreMock("t1");
        var registry = CreateRegistryMock(Resource("Test Resource", "test-app-id", "t1"));
        var svc = CreateService(httpMock: http, authMock: auth, tenantMock: tenants, registryMock: registry);

        await svc.QueryErrorsAsync(TimeSpan.FromHours(1), 20);

        http.Verify(m => m.QueryAsync("test-app-id", "token-t1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        tenants.Verify(m => m.GetAccessTokenAsync("t1", "https://api.applicationinsights.io/.default", It.IsAny<CancellationToken>()), Times.Once);
        auth.Verify(m => m.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueryErrorsAsync_FallsBackToFoundry_WhenTenantStoreIsEmpty()
    {
        var http = CreateHttpMock();
        var auth = CreateAuthMock("foundry-token");
        var tenants = CreateTenantStoreMock();
        var svc = CreateService(httpMock: http, authMock: auth, tenantMock: tenants);

        await svc.QueryErrorsAsync(TimeSpan.FromHours(1), 20);

        http.Verify(m => m.QueryAsync("test-app-id", "foundry-token", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        tenants.Verify(m => m.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueryErrorsAsync_PrefersAppIdOverride_WithoutReadingConfig()
    {
        var config = CreateConfigMock();
        var http = CreateHttpMock();
        var svc = CreateService(config, http);

        await svc.QueryErrorsAsync(TimeSpan.FromHours(1), 20, appIdOverride: "other-app");

        http.Verify(m => m.QueryAsync("other-app", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        config.Verify(m => m.GetConfigAsync(), Times.Never);
    }

    // ── Fan-out ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task QueryErrorsManyAsync_QueriesEveryResource_WithItsOwnTenantToken_AndStampsResource()
    {
        var ts = DateTimeOffset.UtcNow;
        var http = new Mock<IAppInsightsHttpAdapter>();
        http.Setup(m => m.QueryAsync("app-a", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResponse(
                ["timestamp", "type", "outerMessage", "innermostMessage", "operation_Id", "cloud_RoleName"],
                [[ts.AddMinutes(-10), "A.Ex", "o", "older", "op-a", "svc-a"]]));
        http.Setup(m => m.QueryAsync("app-b", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResponse(
                ["timestamp", "type", "outerMessage", "innermostMessage", "operation_Id", "cloud_RoleName"],
                [[ts, "B.Ex", "o", "newer", "op-b", "svc-b"]]));
        var svc = CreateService(httpMock: http, tenantMock: CreateTenantStoreMock("t1", "t2"));
        var targets = new[] { Resource("ai-a", "app-a", "t1"), Resource("ai-b", "app-b", "t2") };

        var result = await svc.QueryErrorsManyAsync(targets, TimeSpan.FromHours(1), 20);

        result.Errors.Should().BeEmpty();
        result.Attempted.Should().Be(2);
        result.Items.Should().HaveCount(2);
        // Merged newest-first across resources, each row carrying the registry name it came from.
        result.Items.Select(e => e.Message).Should().Equal("newer", "older");
        result.Items.Select(e => e.Resource).Should().Equal("ai-b", "ai-a");
        http.Verify(m => m.QueryAsync("app-a", "token-t1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        http.Verify(m => m.QueryAsync("app-b", "token-t2", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueryTracesManyAsync_CapturesOneResourceFailure_AndStillReturnsTheOther()
    {
        var http = new Mock<IAppInsightsHttpAdapter>();
        http.Setup(m => m.QueryAsync("app-a", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Response status code does not indicate success: 500"));
        http.Setup(m => m.QueryAsync("app-b", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResponse(
                ["timestamp", "operation_Id", "name", "cloud_RoleName", "duration", "success", "resultCode"],
                [[DateTimeOffset.UtcNow, "op-b", "GET /b", "svc-b", 1.0, true, "200"]]));
        var svc = CreateService(httpMock: http, tenantMock: CreateTenantStoreMock("t1"));
        var targets = new[] { Resource("ai-a", "app-a", "t1"), Resource("ai-b", "app-b", "t1") };

        var result = await svc.QueryTracesManyAsync(targets, TimeSpan.FromHours(1), 20);

        result.AllFailed.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Resource.Name.Should().Be("ai-a");
        result.Errors[0].Error.Message.Should().Contain("500");
        result.Items.Should().ContainSingle().Which.Resource.Should().Be("ai-b");
    }

    [Fact]
    public async Task QueryLogsManyAsync_SurfacesAuthExpired_PerResource()
    {
        var tenants = CreateTenantStoreMock("t1", "t2");
        tenants.Setup(m => m.GetAccessTokenAsync("t2", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AzureAuthExpiredException("t2"));
        var svc = CreateService(tenantMock: tenants);
        var targets = new[] { Resource("ai-a", "app-a", "t1"), Resource("ai-b", "app-b", "t2") };

        var result = await svc.QueryLogsManyAsync(targets, TimeSpan.FromHours(1));

        result.Errors.Should().ContainSingle();
        result.Errors[0].Resource.Name.Should().Be("ai-b");
        result.Errors[0].Error.Should().BeOfType<AzureAuthExpiredException>().Which.TenantId.Should().Be("t2");
    }

    [Fact]
    public async Task QuerySpansManyAsync_MergesOldestFirst_AndReportsAllFailed()
    {
        var ts = DateTimeOffset.UtcNow;
        var http = new Mock<IAppInsightsHttpAdapter>();
        http.Setup(m => m.QueryAsync("app-a", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResponse(
                ["timestamp", "id", "target", "type", "name", "duration", "success"],
                [[ts, "span-late", "db", "SQL", "later", 1.0, true]]));
        http.Setup(m => m.QueryAsync("app-b", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeResponse(
                ["timestamp", "id", "target", "type", "name", "duration", "success"],
                [[ts.AddSeconds(-5), "span-early", "db", "SQL", "earlier", 1.0, true]]));
        var svc = CreateService(httpMock: http, tenantMock: CreateTenantStoreMock("t1"));
        var targets = new[] { Resource("ai-a", "app-a", "t1"), Resource("ai-b", "app-b", "t1") };

        var merged = await svc.QuerySpansManyAsync(targets, "op-1");
        merged.Items.Select(s => s.SpanId).Should().Equal("span-early", "span-late");

        var failing = new Mock<IAppInsightsHttpAdapter>();
        failing.Setup(m => m.QueryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));
        var allFailed = await CreateService(httpMock: failing, tenantMock: CreateTenantStoreMock("t1"))
            .QuerySpansManyAsync(targets, "op-1");
        allFailed.AllFailed.Should().BeTrue();
        allFailed.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task TestConnectionAsync_ForEntry_UsesThatResourceAndTenant()
    {
        var http = CreateHttpMock(MakeResponse(["cloud_RoleName"], [["role-b"]]));
        var svc = CreateService(httpMock: http, tenantMock: CreateTenantStoreMock("t1", "t2"));

        var result = await svc.TestConnectionAsync(Resource("ai-b", "app-b", "t2"));

        result.Success.Should().BeTrue();
        result.ResourceName.Should().Be("role-b");
        http.Verify(m => m.QueryAsync("app-b", "token-t2", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
