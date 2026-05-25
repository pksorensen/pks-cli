using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Commands.Writing;

public class WritingScoreSettings : WritingSettings
{
    [CommandArgument(0, "<path>")]
    [Description("Markdown file to score. Folders are not supported (score one post at a time).")]
    public string Path { get; set; } = "";

    [CommandOption("--model")]
    [Description("Claude model: haiku | sonnet | opus. Default: haiku (BGA-style critic — pattern matching, not deliberation).")]
    public string Model { get; set; } = "haiku";

    [CommandOption("--budget")]
    [Description("Max USD per critique. Default: 0.50.")]
    public double MaxBudgetUsd { get; set; } = 0.50;

    [CommandOption("--lint-only")]
    [Description("Skip the LLM critic; lint deterministically only.")]
    public bool LintOnly { get; set; }
}

public class WritingScoreCommand : AsyncCommand<WritingScoreSettings>
{
    private readonly IWritingPathResolver _paths;
    private readonly IWritingProfileStore _store;
    private readonly IWritingLinter _linter;
    private readonly IWritingCritic _critic;

    public WritingScoreCommand(
        IWritingPathResolver paths,
        IWritingProfileStore store,
        IWritingLinter linter,
        IWritingCritic critic)
    {
        _paths = paths;
        _store = store;
        _linter = linter;
        _critic = critic;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WritingScoreSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            AnsiConsole.MarkupLine("[red]error:[/] path argument required.");
            return 1;
        }
        var fullPath = System.IO.Path.GetFullPath(settings.Path);
        if (!System.IO.File.Exists(fullPath))
        {
            AnsiConsole.MarkupLine($"[red]error:[/] file not found: [cyan]{fullPath}[/]");
            return 1;
        }

        var projectRoot = _paths.ResolveProjectRoot(System.IO.Path.GetDirectoryName(fullPath)!);
        var anglicisms = await _store.LoadAnglicismsAsync(projectRoot);
        var allowlist = await _store.LoadAllowlistAsync();
        var channel = (await _store.LoadChannelConfigAsync(projectRoot)).DefaultChannel;
        var content = await System.IO.File.ReadAllTextAsync(fullPath);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold magenta]pks writing score[/] [grey]({System.IO.Path.GetFileName(fullPath)}, channel: {channel})[/]")
            .RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        // 1) Lint pass — deterministic.
        var lintFindings = await _linter.LintAsync(content, anglicisms, allowlist);
        AnsiConsole.MarkupLine($"[grey]lint:[/] {lintFindings.Count} terminology finding{(lintFindings.Count == 1 ? "" : "s")}");

        var report = new WritingReport
        {
            SourcePath = fullPath,
            Channel = channel,
            Findings = lintFindings.ToList(),
        };

        // 2) Critic pass (unless --lint-only).
        if (!settings.LintOnly)
        {
            var profile = await _store.LoadProfileAsync();
            var rubric = await _store.LoadChannelRubricAsync(channel);
            var references = await _store.LoadReferenceSamplesAsync(channel);

            AnsiConsole.MarkupLine($"[grey]critic:[/] calling [cyan]{settings.Model}[/]" +
                (references.Count > 0 ? $" with [cyan]{references.Count}[/] reference sample{(references.Count == 1 ? "" : "s")}…" : " (no reference samples — drop *.md files into ~/.pks-cli/writing/reference/{channel}/ to teach it your voice)…"));

            var critique = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("scoring…", _ => _critic.CritiqueAsync(new CritiqueRequest
                {
                    SourcePath = fullPath,
                    Content = content,
                    Channel = channel,
                    Profile = profile,
                    ChannelRubric = rubric,
                    References = references,
                    Anglicisms = anglicisms,
                    Allowlist = allowlist,
                    Model = settings.Model,
                    MaxBudgetUsd = settings.MaxBudgetUsd,
                }));

            if (!critique.Success)
            {
                AnsiConsole.MarkupLine($"[red]critic failed:[/] {critique.ErrorKind} — {Markup.Escape(critique.ErrorMessage ?? "(no message)")}");
                AnsiConsole.MarkupLine("[yellow]continuing with lint-only report.[/]");
            }
            else
            {
                report.DimensionScores = critique.DimensionScores;
                report.Findings.AddRange(critique.Findings);
                report.CriticNotes = critique.Notes;
                report.CriticModel = critique.Model;

                if (critique.DimensionScores.Count > 0)
                {
                    var avg = critique.DimensionScores.Values.Average();
                    report.Score = (int)Math.Round(avg * 20);
                }
            }
        }

        await _store.SaveReportAsync(fullPath, report);

        RenderSummary(report);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓[/] Report: [cyan]{_paths.ReportSidecarMarkdownPath(fullPath)}[/]");
        return 0;
    }

    private static void RenderSummary(WritingReport report)
    {
        AnsiConsole.WriteLine();

        if (report.Score is int s && report.DimensionScores.Count > 0)
        {
            var color = s >= 80 ? "green" : s >= 60 ? "yellow" : "red";
            var rule = new Rule($"[{color}]Score: {s}/100[/]").LeftJustified();
            AnsiConsole.Write(rule);

            var dimTable = new Table().Border(TableBorder.Minimal).ShowHeaders();
            dimTable.AddColumn("Dimension");
            dimTable.AddColumn(new TableColumn("Score").RightAligned());
            dimTable.AddColumn("Bar");
            foreach (var (name, value) in report.DimensionScores.OrderByDescending(kv => kv.Value))
            {
                var dimColor = value >= 4 ? "green" : value == 3 ? "yellow" : "red";
                var bar = new string('█', value) + new string('░', 5 - value);
                dimTable.AddRow(name, $"[{dimColor}]{value}[/]", $"[{dimColor}]{bar}[/]");
            }
            AnsiConsole.Write(dimTable);
        }

        if (report.Findings.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]{report.Findings.Count}[/] finding{(report.Findings.Count == 1 ? "" : "s")}:");
            var table = new Table().Border(TableBorder.Minimal).ShowHeaders();
            table.AddColumn(new TableColumn("Line").RightAligned());
            table.AddColumn("Source");
            table.AddColumn("Match");
            table.AddColumn("Note");

            foreach (var f in report.Findings.OrderBy(f => f.Line).Take(15))
            {
                table.AddRow(
                    f.Line.ToString(),
                    f.RuleId.StartsWith("Critic.") ? f.RuleId.Substring("Critic.".Length) : "Terminology",
                    $"[red]{Markup.Escape(Truncate(f.Match, 40))}[/]",
                    Markup.Escape(Truncate(f.Message, 80)));
            }
            AnsiConsole.Write(table);
        }

        if (!string.IsNullOrWhiteSpace(report.CriticNotes))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(Markup.Escape(report.CriticNotes.Trim()))
                .Header("[bold]Critic notes[/]")
                .Border(BoxBorder.Rounded));
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
