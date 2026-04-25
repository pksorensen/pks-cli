using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
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

    private static AppInsightsQueryService CreateService(
        Mock<IAppInsightsConfigService>? configMock = null,
        Mock<IAppInsightsHttpAdapter>? httpMock = null,
        Mock<IAzureFoundryAuthService>? authMock = null)
    {
        return new AppInsightsQueryService(
            (configMock ?? CreateConfigMock()).Object,
            (httpMock ?? CreateHttpMock()).Object,
            (authMock ?? CreateAuthMock()).Object);
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
}
