using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Commands.Writing;

public class WritingLintSettings : WritingSettings
{
    [CommandArgument(0, "<path>")]
    [Description("File or folder to lint. Folders recurse over *.md.")]
    public string Path { get; set; } = "";

    [CommandOption("--quiet")]
    [Description("Suppress the per-finding table; print only summary.")]
    public bool Quiet { get; set; }
}

public class WritingLintCommand : AsyncCommand<WritingLintSettings>
{
    private readonly IWritingPathResolver _paths;
    private readonly IWritingProfileStore _store;
    private readonly IWritingLinter _linter;

    public WritingLintCommand(
        IWritingPathResolver paths,
        IWritingProfileStore store,
        IWritingLinter linter)
    {
        _paths = paths;
        _store = store;
        _linter = linter;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WritingLintSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            AnsiConsole.MarkupLine("[red]error:[/] path argument required.");
            return 1;
        }

        var fullPath = System.IO.Path.GetFullPath(settings.Path);
        var files = ResolveTargets(fullPath);
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]![/] No markdown files found at [cyan]{fullPath}[/]");
            return 0;
        }

        var projectRoot = _paths.ResolveProjectRoot(
            System.IO.Directory.Exists(fullPath) ? fullPath : System.IO.Path.GetDirectoryName(fullPath)!);
        var anglicisms = await _store.LoadAnglicismsAsync(projectRoot);
        var allowlist = await _store.LoadAllowlistAsync();
        var channel = (await _store.LoadChannelConfigAsync(projectRoot)).DefaultChannel;

        if (anglicisms.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]![/] Anglicism list is empty. Run [bold]pks writing init[/] first.");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold magenta]pks writing lint[/] [grey]({files.Count} file{(files.Count == 1 ? "" : "s")}, {anglicisms.Count} rules)[/]")
            .RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        int totalFindings = 0;
        foreach (var file in files)
        {
            var content = await System.IO.File.ReadAllTextAsync(file);
            var findings = await _linter.LintAsync(content, anglicisms, allowlist);

            if (findings.Count == 0)
            {
                // Nothing to report — drop any stale sidecar so post folders stay clean.
                await _store.DeleteReportSidecarsAsync(file);
            }
            else
            {
                var report = new WritingReport
                {
                    SourcePath = file,
                    Channel = channel,
                    Findings = findings.ToList(),
                };
                await _store.SaveReportAsync(file, report);
            }

            totalFindings += findings.Count;
            RenderFileSummary(file, findings, settings.Quiet);
        }

        AnsiConsole.WriteLine();
        var color = totalFindings == 0 ? "green" : totalFindings < 10 ? "yellow" : "red";
        AnsiConsole.MarkupLine($"[{color}]{totalFindings}[/] finding{(totalFindings == 1 ? "" : "s")} across {files.Count} file{(files.Count == 1 ? "" : "s")}.");
        return totalFindings == 0 ? 0 : 0; // lint is informational; don't break CI by default
    }

    private static List<string> ResolveTargets(string path)
    {
        if (System.IO.File.Exists(path))
            return new List<string> { path };
        if (System.IO.Directory.Exists(path))
        {
            var sep = System.IO.Path.DirectorySeparatorChar;
            return System.IO.Directory.EnumerateFiles(path, "*.md", System.IO.SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{sep}node_modules{sep}"))
                .Where(p => !p.Contains($"{sep}_review{sep}"))     // our own output
                .Where(p => !p.Contains($"{sep}.pks{sep}"))         // project layer
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }
        return new List<string>();
    }

    private static void RenderFileSummary(string file, IReadOnlyList<WritingFinding> findings, bool quiet)
    {
        var rel = System.IO.Path.GetRelativePath(System.IO.Directory.GetCurrentDirectory(), file);

        if (findings.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] [grey]{Markup.Escape(rel)}[/]");
            return;
        }

        var color = findings.Count < 5 ? "yellow" : "red";
        AnsiConsole.MarkupLine($"[{color}]●[/] [bold]{Markup.Escape(rel)}[/]  [{color}]{findings.Count}[/] finding{(findings.Count == 1 ? "" : "s")}");

        if (quiet) return;

        var table = new Table().Border(TableBorder.Minimal).ShowHeaders();
        table.AddColumn(new TableColumn("Line").RightAligned());
        table.AddColumn("Match");
        table.AddColumn("Suggestion");
        table.AddColumn(new TableColumn("Note").NoWrap());

        foreach (var f in findings.OrderBy(f => f.Line).ThenBy(f => f.Column).Take(20))
        {
            table.AddRow(
                f.Line.ToString(),
                $"[red]{Markup.Escape(f.Match)}[/]",
                f.Suggestions.Count > 0
                    ? $"[green]{Markup.Escape(string.Join(" / ", f.Suggestions))}[/]"
                    : "[grey]—[/]",
                Markup.Escape(f.Message));
        }
        AnsiConsole.Write(table);

        if (findings.Count > 20)
            AnsiConsole.MarkupLine($"[grey]…and {findings.Count - 20} more — see the sidecar report.[/]");
    }
}
