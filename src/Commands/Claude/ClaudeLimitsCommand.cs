using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Attributes;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Claude;

public class ClaudeLimitsSettings : ClaudeSettings
{
    [CommandOption("--json")]
    [Description("Emit structured JSON to stdout (auto-on when stdout is not a TTY).")]
    public bool Json { get; set; }

    [CommandOption("--llm")]
    [Description("Force the MCP round-trip fallback (spawn claude with pks mcp + report_session_limits) instead of the deterministic parser.")]
    public bool Llm { get; set; }

    [CommandOption("--timeout")]
    [Description("Whole-capture timeout in seconds (boot + /usage render + kill).")]
    [DefaultValue(60)]
    public int TimeoutSeconds { get; set; } = 60;

    [CommandOption("--model")]
    [Description("Model id used for the --llm fallback round-trip (cheap model recommended).")]
    [DefaultValue("claude-haiku-4-5-20251001")]
    public string Model { get; set; } = "claude-haiku-4-5-20251001";

    [CommandOption("--debug")]
    [Description("Dump the raw captured tmux pane to stderr before parsing.")]
    public bool Debug { get; set; }
}

/// <summary>
/// Reports Claude Code SESSION/WEEK usage limits + reset times + pace-vs-clock as structured
/// data, so it can be polled hourly (cron / a cheap model) to tell whether we are AHEAD
/// (can spend more) or BEHIND (will run out). Drives the interactive `/usage` panel inside a
/// detached tmux session (<see cref="UsageTmuxDriver"/>), deterministically parses it
/// (<see cref="UsagePanelParser"/>), and falls back to an MCP round-trip through a spawned
/// claude (<see cref="Infrastructure.Services.MCP.Tools.LimitsReportToolService"/>) when the
/// parse fails or `--llm` is forced.
/// </summary>
[ToolRegistryExport(
    "pks/claude/limits",
    Title = "pks claude limits",
    Description = "Report Claude Code session/week usage limits, reset times, and pace/burn as structured data (parses the interactive /usage panel via tmux).",
    Tags = ["claude", "limits", "usage", "quota", "pace", "session"],
    Status = "stable",
    Icon = "gauge",
    Usage = "pks claude limits [--json] [--llm] [--timeout <s>] [--model <id>] [--debug]",
    Examples = [
        "pks claude limits",
        "pks claude limits --json",
        "pks claude limits --llm --model claude-haiku-4-5-20251001"
    ]
)]
public class ClaudeLimitsCommand : AsyncCommand<ClaudeLimitsSettings>
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Relaxed escaping so a timestamp offset renders as "+00:00", not the escaped
        // "+00:00" — this feed is read by humans/models hourly, and stays valid JSON.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions SinkReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public override async Task<int> ExecuteAsync(CommandContext context, ClaudeLimitsSettings settings)
    {
        var jsonMode = settings.Json || Console.IsOutputRedirected;

        // In JSON mode ONLY the JSON document may hit stdout — route status/spinner text
        // to a stderr-targeted console instead of the ambient (stdout) AnsiConsole.
        var statusConsole = jsonMode
            ? AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) })
            : AnsiConsole.Console;

        var capturedAt = DateTime.UtcNow;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)));

        string? rawPane = null;

        try
        {
            rawPane = await CaptureWithStatusAsync(statusConsole, cts.Token);

            if (settings.Debug)
            {
                await Console.Error.WriteLineAsync("----- raw /usage pane -----");
                await Console.Error.WriteLineAsync(rawPane);
            }

            var blocks = settings.Llm
                ? new List<ParsedBlock>()
                : UsagePanelParser.TryParse(rawPane, DateTimeOffset.UtcNow);
            var source = "parse";

            if (settings.Llm || blocks.Count == 0)
            {
                if (!settings.Llm)
                {
                    statusConsole.MarkupLine("[yellow]Deterministic parse found no usage blocks — falling back to --llm.[/]");
                }

                (blocks, source) = await RunLlmFallbackAsync(rawPane, settings, cts.Token, statusConsole);
            }

            var report = UsagePanelParser.BuildReport(blocks, capturedAt, DateTime.UtcNow, source);

            if (jsonMode)
            {
                Console.Out.Write(JsonSerializer.Serialize(report, JsonOutputOptions));
                return 0;
            }

            RenderPretty(report, rawPane, settings.Debug);
            return 0;
        }
        catch (Exception ex)
        {
            return EmitError(jsonMode, capturedAt, ex);
        }
    }

    private static async Task<string> CaptureWithStatusAsync(IAnsiConsole statusConsole, CancellationToken ct)
    {
        string? pane = null;
        await statusConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Capturing Claude Code /usage panel via tmux…", async _ =>
            {
                pane = await UsageTmuxDriver.CaptureUsagePanelAsync(ct);
            });

        return pane ?? throw new InvalidOperationException("tmux capture returned no pane text.");
    }

    private static int EmitError(bool jsonMode, DateTime capturedAt, Exception ex)
    {
        if (jsonMode)
        {
            var errorReport = new LimitsReport(
                CapturedAt: capturedAt.ToUniversalTime().ToString("o"),
                Source: "error",
                Session: null,
                Week: null,
                WeekByModel: [],
                Error: ex.Message);

            Console.Out.Write(JsonSerializer.Serialize(errorReport, JsonOutputOptions));
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Error: {ex.Message}[/]");
        }

        return 1;
    }

    // ---------------------------------------------------------------------
    // --llm fallback: spawn claude with --mcp-config so it can call
    // report_session_limits on our `pks mcp --transport stdio` server, then
    // read the sink file it wrote and resolve reset times ourselves.
    // ---------------------------------------------------------------------

    private static async Task<(List<ParsedBlock> Blocks, string Source)> RunLlmFallbackAsync(
        string rawPane, ClaudeLimitsSettings settings, CancellationToken ct, IAnsiConsole statusConsole)
    {
        var sink = Path.Combine(Path.GetTempPath(), $"pks-limits-{Guid.NewGuid():N}.json");
        var mcpConfigPath = Path.Combine(Path.GetTempPath(), $"pks-mcp-{Guid.NewGuid():N}.json");

        try
        {
            var mcpConfig = new
            {
                mcpServers = new
                {
                    pks = new
                    {
                        command = "pks",
                        args = new[] { "mcp", "--transport", "stdio" },
                        env = new Dictionary<string, string> { ["PKS_LIMITS_SINK"] = sink }
                    }
                }
            };

            await File.WriteAllTextAsync(mcpConfigPath, JsonSerializer.Serialize(mcpConfig), ct);

            var prompt = BuildPasteBackPrompt(rawPane);

            await statusConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Round-tripping /usage panel through claude + MCP…", async _ =>
                {
                    await SpawnClaudeMcpRoundTripAsync(mcpConfigPath, sink, prompt, settings.Model, ct);
                });

            if (!File.Exists(sink))
            {
                throw new InvalidOperationException("--llm fallback timed out: report_session_limits was never called.");
            }

            var sinkJson = await File.ReadAllTextAsync(sink, ct);
            var payload = JsonSerializer.Deserialize<SinkPayload>(sinkJson, SinkReadOptions)
                ?? throw new InvalidOperationException("--llm fallback produced an unreadable sink payload.");

            var blocks = MapSinkPayloadToBlocks(payload, DateTimeOffset.UtcNow);
            return (blocks, "llm");
        }
        finally
        {
            TryDelete(sink);
            TryDelete(mcpConfigPath);
        }
    }

    private static string BuildPasteBackPrompt(string rawPane) =>
        "You are a parser. Below is the raw text of Claude Code's /usage panel.\n" +
        "Read the numbers and call the report_session_limits tool EXACTLY ONCE.\n" +
        "Pass sessionUsedPct + sessionResetsAt from the \"Current session\" block,\n" +
        "weekUsedPct + weekResetsAt from the \"Current week (all models)\" block,\n" +
        "and weekByModelJson as a JSON array of {model,usedPct,resetsAt} for every\n" +
        "\"Current week (<Model>)\" block. Use the reset strings verbatim (e.g. \"7:19pm\",\n" +
        "\"Jul 20, 3:59am\"). Do not explain. Panel:\n\n" +
        "<<<\n" + rawPane + "\n>>>\n";

    private static async Task SpawnClaudeMcpRoundTripAsync(string mcpConfigPath, string sink, string prompt, string model, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--dangerously-skip-permissions");
        psi.ArgumentList.Add("--mcp-config");
        psi.ArgumentList.Add(mcpConfigPath);
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--allowedTools");
        psi.ArgumentList.Add("mcp__pks__report_session_limits");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.Environment["PKS_LIMITS_SINK"] = sink;

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Drain stdout/stderr concurrently with the wait. The output is unused, but if we
        // leave the redirected pipes unread a chatty child (MCP tool-discovery logging on
        // stderr, or a model that ignores "Do not explain") fills the ~64 KB OS pipe buffer,
        // blocks on write, and never exits — stalling the round-trip to the full timeout and
        // misreporting an already-written sink as a failure.
        var drainOut = process.StandardOutput.ReadToEndAsync(ct);
        var drainErr = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
            try { await Task.WhenAll(drainOut, drainErr); } catch { /* drain best-effort */ }
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup only
        }
    }

    private static List<ParsedBlock> MapSinkPayloadToBlocks(SinkPayload payload, DateTimeOffset now)
    {
        var blocks = new List<ParsedBlock>();

        var sessionReset = UsagePanelParser.ResolveReset(LimitKind.Session, payload.SessionResetsAt, now);
        if (sessionReset != null)
        {
            blocks.Add(new ParsedBlock(LimitKind.Session, null, payload.SessionUsedPct, sessionReset.Value));
        }

        var weekReset = UsagePanelParser.ResolveReset(LimitKind.Week, payload.WeekResetsAt, now);
        if (weekReset != null)
        {
            blocks.Add(new ParsedBlock(LimitKind.Week, null, payload.WeekUsedPct, weekReset.Value));
        }

        if (!string.IsNullOrWhiteSpace(payload.WeekByModelJson))
        {
            try
            {
                var perModel = JsonSerializer.Deserialize<List<SinkModelEntry>>(payload.WeekByModelJson, SinkReadOptions);
                if (perModel != null)
                {
                    foreach (var entry in perModel)
                    {
                        var reset = UsagePanelParser.ResolveReset(LimitKind.Week, entry.ResetsAt, now);
                        if (reset != null)
                        {
                            blocks.Add(new ParsedBlock(LimitKind.Week, entry.Model, entry.UsedPct, reset.Value));
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed weekByModelJson from the model — ignore per-model rows, the
                // session/week-all rows above still stand.
            }
        }

        return blocks;
    }

    private sealed record SinkPayload(
        int SessionUsedPct,
        string SessionResetsAt,
        int WeekUsedPct,
        string WeekResetsAt,
        string? WeekByModelJson,
        DateTime ReceivedAt);

    private sealed record SinkModelEntry(string Model, int UsedPct, string ResetsAt);

    // ---------------------------------------------------------------------
    // Pretty TTY render
    // ---------------------------------------------------------------------

    private static void RenderPretty(LimitsReport report, string? rawPane, bool debug)
    {
        AnsiConsole.Write(new Rule("[bold cyan]Claude Code — Usage Limits[/]").RuleStyle("cyan dim"));

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Limit");
        table.AddColumn("Used");
        table.AddColumn("Time");
        table.AddColumn("Pace");
        table.AddColumn("Used vs time");
        table.AddColumn("Resets (UTC)");
        table.AddColumn("in");

        var behindLimits = new List<(string Label, LimitEntry Entry)>();
        var anyEntry = false;

        if (report.Session != null)
        {
            AddRow(table, "Current session", report.Session);
            anyEntry = true;
            if (report.Session.Pace == "behind") behindLimits.Add(("Current session", report.Session));
        }

        if (report.Week != null)
        {
            AddRow(table, "Week (all models)", report.Week);
            anyEntry = true;
            if (report.Week.Pace == "behind") behindLimits.Add(("Week (all models)", report.Week));
        }

        foreach (var entry in report.WeekByModel)
        {
            var label = $"Week ({entry.Model})";
            AddRow(table, label, entry);
            anyEntry = true;
            if (entry.Pace == "behind") behindLimits.Add((label, entry));
        }

        if (!anyEntry)
        {
            AnsiConsole.MarkupLine("[yellow]No usage blocks parsed.[/]");
        }
        else
        {
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine("[dim]  █ quota used · ┃ where the clock is — fill past ┃ = spending faster than time (slow down).[/]");
        }

        if (behindLimits.Count > 0)
        {
            foreach (var (label, entry) in behindLimits)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[red]⚠ {label}: at this rate you'll hit ~{entry.UsedAtReset:F0}% by reset.[/]");
            }
        }
        else if (anyEntry)
        {
            AnsiConsole.MarkupLine("[green]On pace — you can spend more.[/]");
        }

        if (debug)
        {
            AnsiConsole.Write(new Panel(rawPane ?? "(no pane captured)").Header("raw /usage pane"));
        }
    }

    private static void AddRow(Table table, string label, LimitEntry entry)
    {
        var paceColor = entry.Pace switch
        {
            "ahead" => "green",
            "behind" => "red",
            _ => "yellow"
        };
        var paceLabel = entry.Pace == "behind" ? "behind ⚠" : entry.Pace;
        var paceMarkup = $"[{paceColor}]{paceLabel}[/] [dim](x{entry.BurnRatio:F2})[/]";

        var resetsAt = DateTimeOffset.Parse(entry.ResetsAt).ToUniversalTime();

        table.AddRow(
            label,
            $"{entry.UsedPct}%",
            $"[dim]{entry.ElapsedPct:F0}%[/]",
            paceMarkup,
            RenderBar(entry.UsedPct, entry.ElapsedPct, paceColor),
            resetsAt.ToString("MMM d, h:mmtt 'UTC'"),
            FormatDuration(entry.ResetsInSeconds));
    }

    /// <summary>
    /// A usage bar (█ = quota used) with a pace marker (┃) at the point in the bar the time
    /// clock has reached. If the fill runs past the marker you've used more quota than time —
    /// slow down; if it trails the marker you're ahead and can spend more.
    /// </summary>
    private static string RenderBar(int usedPct, double elapsedPct, string paceColor)
    {
        const int barWidth = 20;
        var filled = Math.Clamp((int)Math.Round(usedPct * barWidth / 100.0), 0, barWidth);
        var marker = Math.Clamp((int)Math.Round(elapsedPct * barWidth / 100.0), 0, barWidth - 1);

        var sb = new StringBuilder();
        for (var i = 0; i < barWidth; i++)
        {
            if (i == marker)
                sb.Append("[white bold]┃[/]");        // where the clock is
            else if (i < filled)
                sb.Append($"[{paceColor}]█[/]");       // quota consumed
            else
                sb.Append("[grey]░[/]");
        }
        return sb.ToString();
    }

    private static string FormatDuration(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{span.Minutes}m {span.Seconds}s";
    }
}
