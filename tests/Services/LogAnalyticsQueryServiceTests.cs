using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services;

[Trait("Category", "LogAnalytics")]
public class LogAnalyticsQueryServiceTests
{
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

    private static LogAnalyticsQueryService CreateService(
        Mock<ILogAnalyticsConfigService>? configMock = null,
        Mock<ILogAnalyticsHttpAdapter>? httpMock = null,
        Mock<IAzureFoundryAuthService>? authMock = null)
        => new(
            (configMock ?? CreateConfigMock()).Object,
            (httpMock ?? CreateHttpMock()).Object,
            (authMock ?? CreateAuthMock()).Object);

    [Fact]
    public async Task QueryAsync_UsesConfiguredWorkspace_AndRequestsQueryScopedToken()
    {
        var http = CreateHttpMock();
        var auth = CreateAuthMock();
        var svc = CreateService(httpMock: http, authMock: auth);

        await svc.QueryAsync("Heartbeat | take 1");

        http.Verify(m => m.QueryAsync(
            "configured-workspace", "test-bearer-token", "Heartbeat | take 1", null, It.IsAny<CancellationToken>()), Times.Once);
        auth.Verify(m => m.GetAccessTokenAsync("https://api.loganalytics.io/.default", It.IsAny<CancellationToken>()), Times.Once);
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
