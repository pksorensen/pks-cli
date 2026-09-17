using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.Commands.Otel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Otel;

[Trait("Category", "Otel")]
[Collection("StdoutCapture")]
public class OtelCommandTests
{
    private static AzureResourceEntry Resource(string name, string appId, bool enabled = true, string? tenant = "t1") => new()
    {
        Kind = AzureResourceKind.AppInsights,
        Name = name,
        Key = appId,
        TenantId = tenant,
        Enabled = enabled
    };

    private static readonly AzureResourceEntry[] TwoEnabled =
    [
        Resource("ai-prod", "app-prod"),
        Resource("ai-staging", "app-staging")
    ];

    private static Mock<IAzureResourceRegistry> CreateRegistry(params AzureResourceEntry[] entries)
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

    private static (Mock<IAzureResourceRegistry>, Mock<IAppInsightsQueryService>, TestConsole) CreateMocks(
        params AzureResourceEntry[] entries)
        => (CreateRegistry(entries), new Mock<IAppInsightsQueryService>(), Wide());

    private static CommandContext Ctx(string name = "errors")
        => new(Mock.Of<IRemainingArguments>(), name, null);

    /// <summary>A TestConsole wide enough that table cells are not wrapped mid-word.</summary>
    private static TestConsole Wide()
    {
        var console = new TestConsole();
        console.Profile.Width = 240;
        return console;
    }

    private static OtelQueryResult<T> Result<T>(params T[] items) where T : IOtelRecord
        => new() { Items = items.ToList(), Attempted = 1 };

    private static List<OtelError> SampleErrors() =>
    [
        new OtelError
        {
            Resource = "ai-prod",
            Timestamp = DateTimeOffset.UtcNow,
            ExceptionType = "System.NullReferenceException",
            Message = "Object reference not set",
            OperationId = "op-abc",
            AppName = "api-service"
        },
        new OtelError
        {
            Resource = "ai-staging",
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExceptionType = "System.ArgumentException",
            Message = "Value cannot be null",
            OperationId = "op-def",
            AppName = "worker-service"
        }
    ];

    private static string CaptureStdout(Action act)
    {
        var sb = new System.Text.StringBuilder();
        using var sw = new System.IO.StringWriter(sb);
        var origOut = Console.Out;
        Console.SetOut(sw);
        try { act(); }
        finally { Console.SetOut(origOut); }
        return sb.ToString().Trim();
    }

    // ── OtelErrorsCommand ────────────────────────────────────────────────────

    [Fact]
    public void OtelErrors_ReturnsOne_WhenNoEnabledResources()
    {
        var (registry, queryMock, console) = CreateMocks(Resource("ai-off", "app-off", enabled: false));
        var cmd = new OtelErrorsCommand(registry.Object, queryMock.Object, console);

        var result = cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings());

        result.Should().Be(1);
        console.Output.Should().Contain("appinsights init");
        queryMock.VerifyNoOtherCalls();
    }

    [Fact]
    public void OtelErrors_FansOutOverEnabledResources_AndRendersResourceColumn()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        IReadOnlyList<AzureResourceEntry>? captured = null;
        queryMock.Setup(m => m.QueryErrorsManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AzureResourceEntry>, TimeSpan, int, string?, string?, CancellationToken>(
                (r, _, _, _, _, _) => captured = r)
            .ReturnsAsync(new OtelQueryResult<OtelError> { Items = SampleErrors(), Attempted = 2 });

        var cmd = new OtelErrorsCommand(registry.Object, queryMock.Object, console);
        var result = cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings());

        result.Should().Be(0);
        captured!.Select(r => r.Name).Should().Equal("ai-prod", "ai-staging");
        console.Output.Should().Contain("Resource");
        console.Output.Should().Contain("ai-prod").And.Contain("ai-staging");
        console.Output.Should().Contain("NullReferenceException").And.Contain("ArgumentException");
    }

    [Fact]
    public void OtelErrors_WritesFlatJson_WithResourceOnEveryRow()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        queryMock.Setup(m => m.QueryErrorsManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OtelQueryResult<OtelError> { Items = SampleErrors(), Attempted = 2 });

        var cmd = new OtelErrorsCommand(registry.Object, queryMock.Object, console);
        int result = -1;
        var json = CaptureStdout(() => result = cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings { Format = "Json" }));

        result.Should().Be(0);
        json.Should().StartWith("[");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().Should().Be(2);
        doc.RootElement.EnumerateArray().Select(e => e.GetProperty("Resource").GetString())
            .Should().Equal("ai-prod", "ai-staging");
        var parsed = JsonSerializer.Deserialize<List<OtelError>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        parsed.Should().HaveCount(2);
    }

    [Fact]
    public void OtelErrors_ResourceOption_NarrowsToThatEnabledEntry()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        IReadOnlyList<AzureResourceEntry>? captured = null;
        queryMock.Setup(m => m.QueryErrorsManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AzureResourceEntry>, TimeSpan, int, string?, string?, CancellationToken>(
                (r, _, _, _, _, _) => captured = r)
            .ReturnsAsync(Result<OtelError>());

        var cmd = new OtelErrorsCommand(registry.Object, queryMock.Object, console);
        // By name and by app id, case-insensitively, and a duplicate collapses to one target.
        var result = cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings { Resources = ["AI-STAGING", "app-staging"] });

        result.Should().Be(0);
        captured.Should().ContainSingle().Which.Name.Should().Be("ai-staging");
    }

    [Fact]
    public void OtelErrors_ResourceOption_Unknown_ReturnsOne_AndListsEnabledNames()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        var cmd = new OtelErrorsCommand(registry.Object, queryMock.Object, console);

        var result = cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings { Resources = ["nope"] });

        result.Should().Be(1);
        console.Output.Should().Contain("nope").And.Contain("ai-prod").And.Contain("ai-staging");
        queryMock.VerifyNoOtherCalls();
    }

    [Fact]
    public void OtelErrors_ResourceOption_Disabled_ReturnsOne()
    {
        var (registry, queryMock, console) = CreateMocks(Resource("ai-prod", "app-prod"), Resource("ai-off", "app-off", enabled: false));
        var cmd = new OtelErrorsCommand(registry.Object, queryMock.Object, console);

        var result = cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings { Resources = ["ai-off"] });

        result.Should().Be(1);
        console.Output.Should().Contain("ai-off").And.Contain("ai-prod");
        queryMock.VerifyNoOtherCalls();
    }

    [Fact]
    public void OtelErrors_PassesOperationId_ToQueryService()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        queryMock.Setup(m => m.QueryErrorsManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<OtelError>());

        var cmd = new OtelErrorsCommand(registry.Object, queryMock.Object, console);
        cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings { OperationId = "my-op-id" });

        queryMock.Verify(m => m.QueryErrorsManyAsync(
            It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
            It.IsAny<string?>(), "my-op-id", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void OtelErrors_ParsesSince_Correctly()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        TimeSpan? capturedSince = null;
        queryMock.Setup(m => m.QueryErrorsManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AzureResourceEntry>, TimeSpan, int, string?, string?, CancellationToken>(
                (_, since, _, _, _, _) => capturedSince = since)
            .ReturnsAsync(Result<OtelError>());

        var cmd = new OtelErrorsCommand(registry.Object, queryMock.Object, console);
        cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings { Since = "6h" });

        capturedSince.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public void OtelErrors_ReportsPerResourceFailure_AndStillExitsZero_WhenOneSucceeded()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        var errorConsole = Wide();
        queryMock.Setup(m => m.QueryErrorsManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OtelQueryResult<OtelError>
            {
                Items = [SampleErrors()[0]],
                Errors = [new OtelResourceError { Resource = TwoEnabled[1], Error = new HttpRequestException("500 boom") }],
                Attempted = 2
            });

        var cmd = new OtelErrorsCommand(registry.Object, queryMock.Object, console) { ErrorConsole = errorConsole };
        var result = cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings());

        result.Should().Be(0);
        errorConsole.Output.Should().Contain("ai-staging").And.Contain("500 boom");
        console.Output.Should().Contain("NullReferenceException");
    }

    [Fact]
    public void OtelErrors_ReturnsOne_WhenEveryResourceFailed_AndNamesReauthForExpiredTenant()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        var errorConsole = Wide();
        queryMock.Setup(m => m.QueryErrorsManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OtelQueryResult<OtelError>
            {
                Errors =
                [
                    new OtelResourceError { Resource = TwoEnabled[0], Error = new AzureAuthExpiredException("t1") },
                    new OtelResourceError { Resource = TwoEnabled[1], Error = new HttpRequestException("down") }
                ],
                Attempted = 2
            });

        var cmd = new OtelErrorsCommand(registry.Object, queryMock.Object, console) { ErrorConsole = errorConsole };
        var result = cmd.Execute(Ctx("errors"), new OtelErrorsCommand.Settings());

        result.Should().Be(1);
        errorConsole.Output.Should().Contain("ai-prod").And.Contain("t1").And.Contain("pks appinsights init --reauth t1");
        errorConsole.Output.Should().Contain("ai-staging").And.Contain("down");
    }

    // ── OtelTracesCommand ────────────────────────────────────────────────────

    [Fact]
    public void OtelTraces_RendersTable_WithResourceColumn()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        queryMock.Setup(m => m.QueryTracesManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
                It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(
                new OtelTrace
                {
                    Resource = "ai-prod",
                    Timestamp = DateTimeOffset.UtcNow,
                    OperationId = "op-789",
                    Name = "GET /api/users",
                    AppName = "api",
                    DurationMs = 55.2,
                    Success = false,
                    HasError = true
                }));

        var cmd = new OtelTracesCommand(registry.Object, queryMock.Object, console);
        var result = cmd.Execute(Ctx("traces"), new OtelTracesCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().Contain("Resource").And.Contain("ai-prod").And.Contain("GET /api/users");
    }

    [Fact]
    public void OtelTraces_PassesHasError_ToQueryService()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        queryMock.Setup(m => m.QueryTracesManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
                It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<OtelTrace>());

        var cmd = new OtelTracesCommand(registry.Object, queryMock.Object, console);
        cmd.Execute(Ctx("traces"), new OtelTracesCommand.Settings { HasError = true });

        queryMock.Verify(m => m.QueryTracesManyAsync(
            It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
            true, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void OtelTraces_WritesJson_WhenFormatIsJson()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        queryMock.Setup(m => m.QueryTracesManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(), It.IsAny<int>(),
                It.IsAny<bool?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(new OtelTrace { Resource = "ai-prod", Name = "GET /ping", OperationId = "op-1", AppName = "svc" }));

        var cmd = new OtelTracesCommand(registry.Object, queryMock.Object, console);
        var json = CaptureStdout(() => cmd.Execute(Ctx("traces"), new OtelTracesCommand.Settings { Format = "Json" }));

        json.Should().StartWith("[");
        json.Should().Contain("\"Resource\":\"ai-prod\"");
    }

    // ── OtelLogsCommand ──────────────────────────────────────────────────────

    [Fact]
    public void OtelLogs_PassesSeverity_ToQueryService()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        queryMock.Setup(m => m.QueryLogsManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<OtelLog>());

        var cmd = new OtelLogsCommand(registry.Object, queryMock.Object, console);
        cmd.Execute(Ctx("logs"), new OtelLogsCommand.Settings { Severity = "Error" });

        queryMock.Verify(m => m.QueryLogsManyAsync(
            It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(),
            "Error", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void OtelLogs_WritesJson_WhenFormatIsJson()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        queryMock.Setup(m => m.QueryLogsManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(new OtelLog { Resource = "ai-prod", Message = "Something happened", Severity = "Error", OperationId = "op-1", AppName = "svc" }));

        var cmd = new OtelLogsCommand(registry.Object, queryMock.Object, console);
        var json = CaptureStdout(() => cmd.Execute(Ctx("logs"), new OtelLogsCommand.Settings { Format = "Json" }));

        json.Should().StartWith("[");
        json.Should().Contain("\"Resource\":\"ai-prod\"");
    }

    [Fact]
    public void OtelLogs_RendersResourceColumn()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        queryMock.Setup(m => m.QueryLogsManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<TimeSpan>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(
                new OtelLog { Resource = "ai-prod", Message = "from prod", Severity = "Error", AppName = "svc" },
                new OtelLog { Resource = "ai-staging", Message = "from staging", Severity = "Error", AppName = "svc" }));

        var cmd = new OtelLogsCommand(registry.Object, queryMock.Object, console);
        var result = cmd.Execute(Ctx("logs"), new OtelLogsCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().Contain("Resource").And.Contain("ai-prod").And.Contain("ai-staging");
    }

    // ── OtelSpansCommand ─────────────────────────────────────────────────────

    [Fact]
    public void OtelSpans_ValidationError_WhenNoOperationId()
    {
        var settings = new OtelSpansCommand.Settings { OperationId = null };
        var validation = settings.Validate();
        validation.Successful.Should().BeFalse();
    }

    [Fact]
    public void OtelSpans_RendersTable_WithResourceColumn()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        queryMock.Setup(m => m.QuerySpansManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(
                new OtelSpan
                {
                    Resource = "ai-prod",
                    SpanId = "span-001",
                    Name = "SELECT users",
                    Type = "SQL",
                    Target = "mydb",
                    DurationMs = 12.3,
                    Success = true
                }));

        var cmd = new OtelSpansCommand(registry.Object, queryMock.Object, console);
        var result = cmd.Execute(Ctx("spans"), new OtelSpansCommand.Settings { OperationId = "op-123" });

        result.Should().Be(0);
        console.Output.Should().Contain("Resource").And.Contain("ai-prod").And.Contain("SELECT users");
    }

    [Fact]
    public void OtelSpans_ResourceOption_Narrows()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        IReadOnlyList<AzureResourceEntry>? captured = null;
        queryMock.Setup(m => m.QuerySpansManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AzureResourceEntry>, string, CancellationToken>((r, _, _) => captured = r)
            .ReturnsAsync(Result<OtelSpan>());

        var cmd = new OtelSpansCommand(registry.Object, queryMock.Object, console);
        var result = cmd.Execute(Ctx("spans"), new OtelSpansCommand.Settings { OperationId = "op-123", Resources = ["ai-prod"] });

        result.Should().Be(0);
        captured.Should().ContainSingle().Which.Name.Should().Be("ai-prod");
    }

    [Fact]
    public void OtelSpans_WritesJson_WhenFormatIsJson()
    {
        var (registry, queryMock, console) = CreateMocks(TwoEnabled);
        queryMock.Setup(m => m.QuerySpansManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(new OtelSpan { Resource = "ai-prod", SpanId = "s1", Name = "GET /", Type = "HTTP", DurationMs = 5 }));

        var cmd = new OtelSpansCommand(registry.Object, queryMock.Object, console);
        var json = CaptureStdout(() => cmd.Execute(Ctx("spans"), new OtelSpansCommand.Settings { OperationId = "op-123", Format = "Json" }));

        json.Should().StartWith("[");
        json.Should().Contain("\"Resource\":\"ai-prod\"");
    }
}
