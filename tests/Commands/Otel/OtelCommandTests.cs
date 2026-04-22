using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.Commands.Otel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Otel;

[Trait("Category", "Otel")]
public class OtelCommandTests
{
    private static (Mock<IAppInsightsConfigService>, Mock<IAppInsightsQueryService>, TestConsole) CreateMocks(
        bool isConfigured = true)
    {
        var configMock = new Mock<IAppInsightsConfigService>();
        var queryMock = new Mock<IAppInsightsQueryService>();
        var console = new TestConsole();
        configMock.Setup(m => m.IsConfiguredAsync()).ReturnsAsync(isConfigured);
        return (configMock, queryMock, console);
    }

    private static CommandContext Ctx(string name = "errors")
        => new(Mock.Of<IRemainingArguments>(), name, null);

    private static List<OtelError> SampleErrors() =>
    [
        new OtelError
        {
            Timestamp = DateTimeOffset.UtcNow,
            ExceptionType = "System.NullReferenceException",
            Message = "Object reference not set",
            OperationId = "op-abc",
            AppName = "api-service"
        },
        new OtelError
        {
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExceptionType = "System.ArgumentException",
            Message = "Value cannot be null",
            OperationId = "op-def",
            AppName = "worker-service"
        }
    ];

    // ── OtelErrorsCommand ────────────────────────────────────────────────────

    [Fact]
    public async Task OtelErrors_ReturnsOne_WhenNotConfigured()
    {
        var (configMock, queryMock, console) = CreateMocks(isConfigured: false);
        var cmd = new OtelErrorsCommand(configMock.Object, queryMock.Object, console);

        var result = cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings());

        result.Should().Be(1);
        console.Output.Should().ContainAny("not configured", "appinsights init");
    }

    [Fact]
    public async Task OtelErrors_RendersTable_WhenResultsExist()
    {
        var (configMock, queryMock, console) = CreateMocks();
        queryMock.Setup(m => m.QueryErrorsAsync(
            It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleErrors());

        var cmd = new OtelErrorsCommand(configMock.Object, queryMock.Object, console);
        var result = cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().ContainAny("NullReferenceException", "ArgumentException", "api-service");
    }

    [Fact]
    public async Task OtelErrors_WritesJson_WhenFormatIsJson()
    {
        var (configMock, queryMock, console) = CreateMocks();
        queryMock.Setup(m => m.QueryErrorsAsync(
            It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleErrors());

        // Capture Console.Out (JSON output bypasses TestConsole)
        var sb = new System.Text.StringBuilder();
        using var sw = new System.IO.StringWriter(sb);
        var origOut = Console.Out;
        Console.SetOut(sw);

        var cmd = new OtelErrorsCommand(configMock.Object, queryMock.Object, console);
        var result = cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings { Format = "Json" });

        Console.SetOut(origOut);

        result.Should().Be(0);
        var json = sb.ToString().Trim();
        json.Should().StartWith("[");
        var parsed = JsonSerializer.Deserialize<List<OtelError>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        parsed.Should().HaveCount(2);
    }

    [Fact]
    public async Task OtelErrors_PassesOperationId_ToQueryService()
    {
        var (configMock, queryMock, console) = CreateMocks();
        queryMock.Setup(m => m.QueryErrorsAsync(
            It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OtelError>());

        var cmd = new OtelErrorsCommand(configMock.Object, queryMock.Object, console);
        cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings { OperationId = "my-op-id" });

        queryMock.Verify(m => m.QueryErrorsAsync(
            It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<string?>(), "my-op-id", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OtelErrors_ParsesSince_Correctly()
    {
        var (configMock, queryMock, console) = CreateMocks();
        TimeSpan? capturedSince = null;
        queryMock.Setup(m => m.QueryErrorsAsync(
            It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<TimeSpan, int, string?, string?, CancellationToken>((since, _, _, _, _) => capturedSince = since)
            .ReturnsAsync(new List<OtelError>());

        var cmd = new OtelErrorsCommand(configMock.Object, queryMock.Object, console);
        cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings { Since = "6h" });

        capturedSince.Should().Be(TimeSpan.FromHours(6));
    }

    // ── OtelTracesCommand ────────────────────────────────────────────────────

    [Fact]
    public async Task OtelTraces_RendersTable_WhenResultsExist()
    {
        var (configMock, queryMock, console) = CreateMocks();
        queryMock.Setup(m => m.QueryTracesAsync(
            It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new OtelTrace
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    OperationId = "op-789",
                    Name = "GET /api/users",
                    AppName = "api",
                    DurationMs = 55.2,
                    Success = false,
                    HasError = true
                }
            ]);

        var cmd = new OtelTracesCommand(configMock.Object, queryMock.Object, console);
        var result = cmd.Execute(Ctx("traces"), new OtelTracesCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().ContainAny("GET /api/users", "op-789");
    }

    [Fact]
    public async Task OtelTraces_PassesHasError_ToQueryService()
    {
        var (configMock, queryMock, console) = CreateMocks();
        queryMock.Setup(m => m.QueryTracesAsync(
            It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OtelTrace>());

        var cmd = new OtelTracesCommand(configMock.Object, queryMock.Object, console);
        cmd.Execute(Ctx("traces"), new OtelTracesCommand.Settings { HasError = true });

        queryMock.Verify(m => m.QueryTracesAsync(
            It.IsAny<TimeSpan>(), It.IsAny<int>(), true, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OtelTraces_WritesJson_WhenFormatIsJson()
    {
        var (configMock, queryMock, console) = CreateMocks();
        queryMock.Setup(m => m.QueryTracesAsync(
            It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OtelTrace { Name = "GET /ping", OperationId = "op-1", AppName = "svc" }]);

        var sb = new System.Text.StringBuilder();
        using var sw = new System.IO.StringWriter(sb);
        var origOut = Console.Out;
        Console.SetOut(sw);

        var cmd = new OtelTracesCommand(configMock.Object, queryMock.Object, console);
        cmd.Execute(Ctx("traces"), new OtelTracesCommand.Settings { Format = "Json" });

        Console.SetOut(origOut);

        sb.ToString().Trim().Should().StartWith("[");
    }

    // ── OtelLogsCommand ──────────────────────────────────────────────────────

    [Fact]
    public async Task OtelLogs_PassesSeverity_ToQueryService()
    {
        var (configMock, queryMock, console) = CreateMocks();
        queryMock.Setup(m => m.QueryLogsAsync(
            It.IsAny<TimeSpan>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OtelLog>());

        var cmd = new OtelLogsCommand(configMock.Object, queryMock.Object, console);
        cmd.Execute(Ctx("logs"), new OtelLogsCommand.Settings { Severity = "Error" });

        queryMock.Verify(m => m.QueryLogsAsync(
            It.IsAny<TimeSpan>(), "Error", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OtelLogs_WritesJson_WhenFormatIsJson()
    {
        var (configMock, queryMock, console) = CreateMocks();
        queryMock.Setup(m => m.QueryLogsAsync(
            It.IsAny<TimeSpan>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OtelLog { Message = "Something happened", Severity = "Error", OperationId = "op-1", AppName = "svc" }]);

        var sb = new System.Text.StringBuilder();
        using var sw = new System.IO.StringWriter(sb);
        var origOut = Console.Out;
        Console.SetOut(sw);

        var cmd = new OtelLogsCommand(configMock.Object, queryMock.Object, console);
        cmd.Execute(Ctx("logs"), new OtelLogsCommand.Settings { Format = "Json" });

        Console.SetOut(origOut);

        sb.ToString().Trim().Should().StartWith("[");
    }

    // ── OtelSpansCommand ─────────────────────────────────────────────────────

    [Fact]
    public async Task OtelSpans_ValidationError_WhenNoOperationId()
    {
        var settings = new OtelSpansCommand.Settings { OperationId = null };
        var validation = settings.Validate();
        validation.Successful.Should().BeFalse();
    }

    [Fact]
    public async Task OtelSpans_RendersTable_WhenResultsExist()
    {
        var (configMock, queryMock, console) = CreateMocks();
        queryMock.Setup(m => m.QuerySpansAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new OtelSpan
                {
                    SpanId = "span-001",
                    Name = "SELECT users",
                    Type = "SQL",
                    Target = "mydb",
                    DurationMs = 12.3,
                    Success = true
                }
            ]);

        var cmd = new OtelSpansCommand(configMock.Object, queryMock.Object, console);
        var result = cmd.Execute(Ctx("spans"), new OtelSpansCommand.Settings { OperationId = "op-123" });

        result.Should().Be(0);
        console.Output.Should().ContainAny("SELECT users", "SQL", "span-001");
    }

    [Fact]
    public async Task OtelSpans_WritesJson_WhenFormatIsJson()
    {
        var (configMock, queryMock, console) = CreateMocks();
        queryMock.Setup(m => m.QuerySpansAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OtelSpan { SpanId = "s1", Name = "GET /", Type = "HTTP", DurationMs = 5 }]);

        var sb = new System.Text.StringBuilder();
        using var sw = new System.IO.StringWriter(sb);
        var origOut = Console.Out;
        Console.SetOut(sw);

        var cmd = new OtelSpansCommand(configMock.Object, queryMock.Object, console);
        cmd.Execute(Ctx("spans"), new OtelSpansCommand.Settings { OperationId = "op-123", Format = "Json" });

        Console.SetOut(origOut);

        sb.ToString().Trim().Should().StartWith("[");
    }
}
