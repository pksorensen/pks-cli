using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;

namespace PKS.Commands.Brain;

public sealed class BrainSourcesSettings : BrainSettings
{
    [CommandOption("--source <KIND>")]
    [Description("Only show one source: claude, codex or opencode.")]
    public string? Source { get; set; }

    [CommandOption("--project <FILTER>")]
    [Description("Only count sessions whose project slug contains this text.")]
    public string? Project { get; set; }

    [CommandOption("--verify")]
    [Description("Parse the newest session of each source and report the ASF event mix.")]
    public bool Verify { get; set; }
}

/// `pks brain sources` — what the brain can see on this machine.
///
/// Answers the two questions that matter before a backup exists: which of the
/// three tools are installed, and how far back their data actually goes. The
/// "oldest" column is the point of the whole exercise — opencode's spilled tool
/// outputs are deleted after 7 days, so a gap there is data already lost.
public sealed class BrainSourcesCommand : AsyncCommand<BrainSourcesSettings>
{
    private readonly IEnumerable<IAgentSessionSource> _sources;
    private readonly IBrainPathResolver _paths;

    public BrainSourcesCommand(IEnumerable<IAgentSessionSource> sources, IBrainPathResolver paths)
    {
        _sources = sources;
        _paths = paths;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainSourcesSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain sources[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.MinimalHeavyHead);
        table.AddColumn("Source");
        table.AddColumn(new TableColumn("Sessions").RightAligned());
        table.AddColumn(new TableColumn("Projects").RightAligned());
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn("Oldest");
        table.AddColumn("Newest");
        table.AddColumn("Location");

        var selected = _sources
            .Where(s => settings.Source is null ||
                        string.Equals(s.Kind, settings.Source, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Kind, StringComparer.Ordinal)
            .ToList();

        if (selected.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Unknown source '{settings.Source}'.[/] Expected claude, codex or opencode.");

            return 1;
        }

        var found = new List<(IAgentSessionSource Source, List<DiscoveredAgentSession> Sessions)>();

        foreach (var source in selected)
        {
            if (!source.IsAvailable)
            {
                table.AddRow(
                    $"[grey]{source.Kind}[/]", "[grey]—[/]", "[grey]—[/]", "[grey]—[/]",
                    "[grey]—[/]", "[grey]—[/]", $"[grey]not installed[/]");

                continue;
            }

            List<DiscoveredAgentSession> sessions;
            try
            {
                sessions = source.Discover(settings.Project).ToList();
            }
            catch (Exception ex)
            {
                table.AddRow(
                    $"[yellow]{source.Kind}[/]", "[red]error[/]", "", "", "", "",
                    $"[red]{Markup.Escape(ex.Message)}[/]");

                continue;
            }

            found.Add((source, sessions));

            if (sessions.Count == 0)
            {
                table.AddRow(
                    source.Kind, "0", "0", "0 B", "[grey]—[/]", "[grey]—[/]",
                    $"[grey]{Markup.Escape(source.Location)}[/]");

                continue;
            }

            table.AddRow(
                $"[bold]{source.Kind}[/]",
                sessions.Count.ToString("N0"),
                sessions.Select(s => s.ProjectSlug).Distinct(StringComparer.Ordinal).Count().ToString("N0"),
                FormatBytes(sessions.Sum(s => s.Bytes)),
                sessions.Min(s => s.LastModifiedUtc).ToLocalTime().ToString("yyyy-MM-dd"),
                sessions.Max(s => s.LastModifiedUtc).ToLocalTime().ToString("yyyy-MM-dd"),
                $"[grey]{Markup.Escape(source.Location)}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        WriteSpillWarning();

        if (settings.Verify)
        {
            await VerifyAsync(found);
        }
        else if (found.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]Run with [bold]--verify[/] to parse the newest session of each source.[/]");
            AnsiConsole.WriteLine();
        }

        return 0;
    }

    /// opencode deletes spilled tool outputs after 7 days. Counting what is left in
    /// the window is the honest way to say how urgent the daily job is.
    private void WriteSpillWarning()
    {
        var root = _paths.OpenCodeToolOutputRoot;
        if (!Directory.Exists(root)) return;

        var cutoff = DateTime.UtcNow.AddDays(-7);
        var files = Directory.EnumerateFiles(root, "tool_*").ToList();
        var expiring = files.Count(f => File.GetLastWriteTimeUtc(f) < cutoff.AddDays(1));

        AnsiConsole.MarkupLine(
            $"[yellow]opencode spill:[/] {files.Count:N0} file(s) in {Markup.Escape(root)}, " +
            $"{expiring:N0} within 24h of the hardcoded 7-day cleanup.");
        AnsiConsole.WriteLine();
    }

    private static async Task VerifyAsync(
        List<(IAgentSessionSource Source, List<DiscoveredAgentSession> Sessions)> found)
    {
        var masker = SecretMasker.ForProject(Directory.GetCurrentDirectory());

        foreach (var (source, sessions) in found)
        {
            var newest = sessions.OrderByDescending(s => s.LastModifiedUtc).FirstOrDefault();
            if (newest is null) continue;

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var events = 0;
            string? failure = null;

            try
            {
                await foreach (var e in source.ReadAsync(newest, masker))
                {
                    events++;
                    counts[e.Kind] = counts.GetValueOrDefault(e.Kind) + 1;
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            AnsiConsole.MarkupLine(
                $"[bold]{source.Kind}[/] [grey]{Markup.Escape(newest.ProjectSlug)}[/] " +
                $"[grey]({Markup.Escape(Path.GetFileName(newest.SourcePath))})[/]");

            if (failure is not null)
            {
                AnsiConsole.MarkupLine($"  [red]{Markup.Escape(failure)}[/]");
                AnsiConsole.WriteLine();

                continue;
            }

            AnsiConsole.MarkupLine(
                $"  {events:N0} ASF events — " +
                string.Join(", ", counts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => $"[cyan]{kv.Key}[/] {kv.Value:N0}")));
            AnsiConsole.WriteLine();
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
