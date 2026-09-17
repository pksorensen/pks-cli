using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.Commands.Kusto;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Kusto;

[Trait("Category", "LogAnalytics")]
[Collection("StdoutCapture")]
public class KustoCommandTests
{
    private const string GuidA = "11111111-1111-1111-1111-111111111111";
    private const string GuidB = "22222222-2222-2222-2222-222222222222";
    private const string GuidOff = "33333333-3333-3333-3333-333333333333";
    private const string GuidUnknown = "44444444-4444-4444-4444-444444444444";

    private static AzureResourceEntry Workspace(string name, string key, bool enabled = true, string? tenant = "t1") => new()
    {
        Kind = AzureResourceKind.LogAnalytics,
        Name = name,
        Key = key,
        TenantId = tenant,
        Enabled = enabled
    };

    private static readonly AzureResourceEntry[] TwoEnabled =
    [
        Workspace("law-prod", GuidA),
        Workspace("law-staging", GuidB)
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

    private static KustoQueryResponse Response(params (string name, string value)[] rows)
        => new()
        {
            Tables =
            [
                new KustoTable
                {
                    Name = "PrimaryResult",
                    Columns = [new KustoColumn { Name = "Computer", Type = "string" }, new KustoColumn { Name = "Count", Type = "long" }],
                    Rows = rows.Select(r => new List<JsonElement>
                    {
                        JsonDocument.Parse($"\"{r.name}\"").RootElement,
                        JsonDocument.Parse(r.value).RootElement
                    }).ToList()
                }
            ]
        };

    /// <summary>A query service whose fan-out answers each workspace from <paramref name="perWorkspace"/> (an exception value ⇒ that workspace fails).</summary>
    private static Mock<ILogAnalyticsQueryService> CreateQueryService(Dictionary<string, object> perWorkspace, Action<IReadOnlyList<AzureResourceEntry>>? onTargets = null)
    {
        var mock = new Mock<ILogAnalyticsQueryService>();
        mock.Setup(m => m.QueryManyAsync(
                It.IsAny<IReadOnlyList<AzureResourceEntry>>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<AzureResourceEntry>, string, TimeSpan?, CancellationToken>((targets, _, _, _) =>
            {
                onTargets?.Invoke(targets);
                IReadOnlyList<WorkspaceQueryResult> results = targets.Select(t =>
                    perWorkspace.TryGetValue(t.Key, out var v) && v is Exception ex
                        ? new WorkspaceQueryResult { Workspace = t, Error = ex }
                        : new WorkspaceQueryResult { Workspace = t, Response = v as KustoQueryResponse ?? Response() }).ToList();
                return Task.FromResult(results);
            });
        return mock;
    }

    private static CommandContext Ctx() => new(Mock.Of<IRemainingArguments>(), "kusto", null);

    /// <summary>A TestConsole wide enough that table cells and error lines are not wrapped.</summary>
    private static TestConsole Wide()
    {
        var console = new TestConsole();
        console.Profile.Width = 240;
        return console;
    }

    private static string CaptureStdout(Action act)
    {
        var sb = new System.Text.StringBuilder();
        using var sw = new System.IO.StringWriter(sb);
        var origOut = Console.Out;
        Console.SetOut(sw);
        try { act(); }
        finally { Console.SetOut(origOut); }
        return sb.ToString();
    }

    private static Dictionary<string, object> BothAnswer() => new()
    {
        [GuidA] = Response(("vm-prod", "3")),
        [GuidB] = Response(("vm-staging", "5"))
    };

    // ── Targets ──────────────────────────────────────────────────────────────

    [Fact]
    public void ReturnsOne_WhenNoEnabledWorkspaces()
    {
        var registry = CreateRegistry(Workspace("law-off", GuidOff, enabled: false));
        var query = CreateQueryService(new());
        var console = Wide();
        var cmd = new KustoCommand(registry.Object, query.Object, console);

        var result = cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat | take 1" });

        result.Should().Be(1);
        console.Output.Should().Contain("No enabled Log Analytics workspaces").And.Contain("pks loganalytics init");
        query.VerifyNoOtherCalls();
    }

    [Fact]
    public void FansOutOverEveryEnabledWorkspace_ByDefault()
    {
        IReadOnlyList<AzureResourceEntry>? captured = null;
        var query = CreateQueryService(BothAnswer(), t => captured = t);
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, Wide());

        var result = cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat | take 1" });

        result.Should().Be(0);
        captured!.Select(t => t.Name).Should().Equal("law-prod", "law-staging");
    }

    [Fact]
    public void WorkspaceOption_ByName_NarrowsToThatEnabledEntry()
    {
        IReadOnlyList<AzureResourceEntry>? captured = null;
        var query = CreateQueryService(BothAnswer(), t => captured = t);
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, Wide());

        var result = cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat", Workspace = "LAW-STAGING" });

        result.Should().Be(0);
        captured.Should().ContainSingle().Which.Key.Should().Be(GuidB);
    }

    [Fact]
    public void WorkspaceOption_UnknownName_ReturnsOne_AndListsEnabledNames()
    {
        var query = CreateQueryService(BothAnswer());
        var console = Wide();
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, console);

        var result = cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat", Workspace = "nope" });

        result.Should().Be(1);
        console.Output.Should().Contain("nope").And.Contain("law-prod").And.Contain("law-staging");
        query.VerifyNoOtherCalls();
    }

    [Fact]
    public void WorkspaceOption_DisabledName_ReturnsOne()
    {
        var registry = CreateRegistry(Workspace("law-prod", GuidA), Workspace("law-off", GuidOff, enabled: false));
        var query = CreateQueryService(BothAnswer());
        var console = Wide();
        var cmd = new KustoCommand(registry.Object, query.Object, console);

        var result = cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat", Workspace = "law-off" });

        result.Should().Be(1);
        console.Output.Should().Contain("law-off").And.Contain("law-prod");
        query.VerifyNoOtherCalls();
    }

    [Fact]
    public void WorkspaceOption_BareGuid_IsQueriedDirectly_EvenWhenUnregistered()
    {
        IReadOnlyList<AzureResourceEntry>? captured = null;
        var query = CreateQueryService(new() { [GuidUnknown] = Response(("vm-x", "1")) }, t => captured = t);
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, Wide());

        var result = cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat", Workspace = GuidUnknown });

        result.Should().Be(0);
        captured.Should().ContainSingle().Which.Key.Should().Be(GuidUnknown);
    }

    [Fact]
    public void WorkspaceOption_BareGuid_OfDisabledEntry_IsQueriedDirectly_WithItsRegisteredTenant()
    {
        IReadOnlyList<AzureResourceEntry>? captured = null;
        var registry = CreateRegistry(Workspace("law-prod", GuidA), Workspace("law-off", GuidOff, enabled: false, tenant: "t9"));
        var query = CreateQueryService(new() { [GuidOff] = Response(("vm-off", "1")) }, t => captured = t);
        var cmd = new KustoCommand(registry.Object, query.Object, Wide());

        var result = cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat", Workspace = GuidOff });

        result.Should().Be(0);
        var target = captured.Should().ContainSingle().Subject;
        target.Key.Should().Be(GuidOff);
        target.TenantId.Should().Be("t9");
        target.Name.Should().Be("law-off");
    }

    // ── Output ───────────────────────────────────────────────────────────────

    [Fact]
    public void Json_IsOneFlatArray_WithWorkspaceFirstOnEveryRow()
    {
        var query = CreateQueryService(BothAnswer());
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, Wide());

        var json = CaptureStdout(() => cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat", Format = "Json" })).Trim();

        json.Should().StartWith("[");
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json)!;
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Keys.First() == "workspace");
        rows.Select(r => r["workspace"].GetString()).Should().Equal("law-prod", "law-staging");
        rows.Select(r => r["Computer"].GetString()).Should().Equal("vm-prod", "vm-staging");
        rows[0]["Count"].GetInt32().Should().Be(3);
    }

    [Fact]
    public void Json_SingleTarget_StillCarriesWorkspace()
    {
        var query = CreateQueryService(BothAnswer());
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, Wide());

        var json = CaptureStdout(() => cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat", Format = "Json", Workspace = "law-prod" })).Trim();

        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json)!;
        rows.Should().ContainSingle();
        rows[0].Keys.First().Should().Be("workspace");
        rows[0]["workspace"].GetString().Should().Be("law-prod");
    }

    [Fact]
    public void Csv_HeaderStartsWithWorkspace_AndRowsFromBothWorkspaces()
    {
        var query = CreateQueryService(BothAnswer());
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, Wide());

        var csv = CaptureStdout(() => cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat", Format = "Csv" }));

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        lines.Should().HaveCount(3);
        lines[0].Should().Be("workspace,Computer,Count");
        lines[1].Should().Be("law-prod,vm-prod,3");
        lines[2].Should().Be("law-staging,vm-staging,5");
    }

    [Fact]
    public void Table_HasOneHeadingPerWorkspace_WhenSeveralTargets()
    {
        var query = CreateQueryService(BothAnswer());
        var console = Wide();
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, console);

        var result = cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat" });

        result.Should().Be(0);
        console.Output.Should().Contain("Workspace law-prod").And.Contain("Workspace law-staging");
        console.Output.Should().Contain("vm-prod").And.Contain("vm-staging");
    }

    [Fact]
    public void Table_HasNoHeading_WhenSingleTarget()
    {
        var query = CreateQueryService(BothAnswer());
        var console = Wide();
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, console);

        cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat", Workspace = "law-prod" });

        console.Output.Should().Contain("vm-prod");
        console.Output.Should().NotContain("Workspace law-prod");
    }

    [Fact]
    public void Verbose_ListsTargets()
    {
        var query = CreateQueryService(BothAnswer());
        var console = Wide();
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, console);

        cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat", Verbose = true, Since = "2h" });

        console.Output.Should().Contain("law-prod").And.Contain(GuidA).And.Contain("law-staging").And.Contain(GuidB);
        console.Output.Should().Contain("PT2H");
    }

    // ── Failures ─────────────────────────────────────────────────────────────

    [Fact]
    public void ExitsZero_AndReportsFailureOnStderr_WhenOneWorkspaceFails()
    {
        var query = CreateQueryService(new()
        {
            [GuidA] = new LogAnalyticsQueryException("Log Analytics query failed (HTTP 500): boom"),
            [GuidB] = Response(("vm-staging", "5"))
        });
        var console = Wide();
        var errorConsole = Wide();
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, console) { ErrorConsole = errorConsole };

        var json = CaptureStdout(() => cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat", Format = "Json" })
            .Should().Be(0)).Trim();

        var rows = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json)!;
        rows.Should().ContainSingle().Which["workspace"].GetString().Should().Be("law-staging");
        errorConsole.Output.Should().Contain("law-prod").And.Contain("HTTP 500");
    }

    [Fact]
    public void ExitsOne_WhenEveryWorkspaceFails_AndNamesReauthForExpiredTenant()
    {
        var query = CreateQueryService(new()
        {
            [GuidA] = new AzureAuthExpiredException("t1"),
            [GuidB] = new LogAnalyticsQueryException("down")
        });
        var console = Wide();
        var errorConsole = Wide();
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, console) { ErrorConsole = errorConsole };

        var result = cmd.Execute(Ctx(), new KustoCommand.Settings { Query = "Heartbeat" });

        result.Should().Be(1);
        errorConsole.Output.Should().Contain("law-prod").And.Contain("pks loganalytics init --reauth t1");
        errorConsole.Output.Should().Contain("law-staging").And.Contain("down");
    }

    [Fact]
    public void ReturnsOne_WhenNoQueryGiven()
    {
        var query = CreateQueryService(BothAnswer());
        var console = Wide();
        var cmd = new KustoCommand(CreateRegistry(TwoEnabled).Object, query.Object, console);

        var result = cmd.Execute(Ctx(), new KustoCommand.Settings { File = "/nonexistent/query.kql" });

        result.Should().Be(1);
        console.Output.Should().Contain("not found");
        query.VerifyNoOtherCalls();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    [Fact]
    public void CsvEscape_QuotesOnlyWhenNeeded()
    {
        KustoCommand.CsvEscape("plain").Should().Be("plain");
        KustoCommand.CsvEscape("a,b").Should().Be("\"a,b\"");
        KustoCommand.CsvEscape("say \"hi\"").Should().Be("\"say \"\"hi\"\"\"");
    }
}
