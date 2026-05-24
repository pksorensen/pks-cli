using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using PKS.Infrastructure.Services.Brain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Brain;

/// <summary>
/// Implements FT-12 (Brain) scan command — Session→ToolCall→File edge discovery.
/// See .pks/brain/feature-specs/FT-012-*.md (when materialized).
/// </summary>
public class BrainScanFilepathSettings : BrainSettings
{
    [CommandArgument(0, "<path>")]
    [Description("File or directory path to find tool_use entries for.")]
    public string Path { get; set; } = string.Empty;

    [CommandOption("--include-bash")]
    [Description("Also match Bash tool_use entries whose `command` contains the path as substring.")]
    public bool IncludeBash { get; set; }

    [CommandOption("--since")]
    [Description("Filter sessions by first-entry timestamp (skip sessions older than this; ISO date).")]
    public string? Since { get; set; }

    [CommandOption("--projects-dir")]
    [Description("Override the Claude projects dir (default: ~/.claude/projects).")]
    public string? ProjectsDir { get; set; }

    [CommandOption("--format")]
    [Description("Output format: text (default), json, or jsonl.")]
    public string Format { get; set; } = "text";
}

public class BrainScanFilepathCommand : AsyncCommand<BrainScanFilepathSettings>
{
    private readonly IBrainSessionScanner _scanner;

    public BrainScanFilepathCommand(IBrainSessionScanner scanner)
    {
        _scanner = scanner;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainScanFilepathSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            AnsiConsole.MarkupLine("[red]A <path> argument is required.[/]");
            return 1;
        }

        DateTime? since = null;
        if (settings.Since is { Length: > 0 } s)
        {
            if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            {
                AnsiConsole.MarkupLine($"[red]Could not parse --since:[/] {s}");
                return 1;
            }
            since = dt;
        }

        var resolved = System.IO.Path.GetFullPath(settings.Path);
        var isDir = Directory.Exists(resolved);

        var projectsDir = settings.ProjectsDir ?? ResolveDefaultProjectsDir();

        var format = (settings.Format ?? "text").ToLowerInvariant();
        if (format is not ("text" or "json" or "jsonl"))
        {
            AnsiConsole.MarkupLine($"[red]Unknown --format:[/] {settings.Format}");
            return 1;
        }

        var options = new BrainScanOptions
        {
            TargetPath = resolved,
            ProjectsDir = projectsDir,
            IncludeBash = settings.IncludeBash,
            SinceUtc = since,
            TargetIsDirectory = isDir,
        };

        var result = await _scanner.ScanAsync(options);

        switch (format)
        {
            case "json":
                WriteJson(result);
                break;
            case "jsonl":
                WriteJsonl(result);
                break;
            default:
                WriteText(result, resolved);
                break;
        }
        return 0;
    }

    private static string ResolveDefaultProjectsDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return System.IO.Path.Combine(home, ".claude", "projects");
    }

    private static void WriteText(BrainScanResult result, string target)
    {
        if (result.Edges.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]No tool_use entries found for[/] [cyan]{target}[/].");
            AnsiConsole.MarkupLine($"[grey]Scanned {result.ScannedJsonls} JSONL file(s).[/]");
            return;
        }

        var t = new Table().Border(TableBorder.MinimalHeavyHead);
        t.AddColumn("session");
        t.AddColumn("tool");
        t.AddColumn("file_path");
        t.AddColumn("timestamp");
        foreach (var e in result.Edges.OrderBy(x => x.TimestampUtc))
        {
            t.AddRow(
                Shorten(e.SessionId),
                e.ToolName,
                e.FilePath,
                e.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"[grey]{result.Edges.Count} edge(s) across {result.MatchedSessions} session(s) across {result.ScannedJsonls} JSONL(s) scanned.[/]");
    }

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;

    private static void WriteJson(BrainScanResult result)
    {
        var payload = new
        {
            scanned_jsonls = result.ScannedJsonls,
            matched_sessions = result.MatchedSessions,
            edges = result.Edges.Select(ToEdgeDto),
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteJsonl(BrainScanResult result)
    {
        var opts = new JsonSerializerOptions { WriteIndented = false };
        foreach (var e in result.Edges)
            Console.WriteLine(JsonSerializer.Serialize(ToEdgeDto(e), opts));
    }

    private static object ToEdgeDto(BrainScanEdge e) => new
    {
        session_id = e.SessionId,
        jsonl_path = e.JsonlPath,
        timestamp = e.TimestampUtc.ToString("o"),
        tool_use_id = e.ToolUseId,
        tool_name = e.ToolName,
        file_path = e.FilePath,
        match_kind = e.MatchKind,
    };
}
