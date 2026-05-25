using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Commands.Writing;

public class WritingLearnSettings : WritingSettings
{
    [CommandArgument(0, "<path>")]
    [Description("Markdown file OR folder. Folders recurse over *.md (skipping _review/, .pks/, node_modules/).")]
    public string Path { get; set; } = "";

    [CommandOption("--filter")]
    [Description("When path is a folder, only learn files matching this glob (e.g. 'da.md', '*.da.md').")]
    public string? Filter { get; set; }
}

/// Non-interactive. Reads the last `<stem>.WRITING-REPORT.json` next to the
/// source, groups + dedupes findings, applies simple heuristics, and writes
/// two sidecars next to the post:
///   - `<stem>.LEARN.json`  — machine-readable proposal (agents edit this)
///   - `<stem>.LEARN.md`    — human/agent-readable summary
///
/// An agent then reviews, optionally edits `accept` flags, and runs
/// `pks writing apply <stem>.LEARN.json`.
public class WritingLearnCommand : AsyncCommand<WritingLearnSettings>
{
    private readonly IWritingPathResolver _paths;
    private readonly IWritingProfileStore _store;

    public WritingLearnCommand(IWritingPathResolver paths, IWritingProfileStore store)
    {
        _paths = paths;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WritingLearnSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            AnsiConsole.MarkupLine("[red]error:[/] path argument required.");
            return 1;
        }

        var fullPath = System.IO.Path.GetFullPath(settings.Path);
        var targets = ResolveTargets(fullPath, settings.Filter);
        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]![/] No matching files at [cyan]{fullPath}[/]");
            return 1;
        }

        var allowlist = await _store.LoadAllowlistAsync();
        var projectRoot = _paths.ResolveProjectRoot(System.IO.Directory.GetCurrentDirectory());
        var anglicismSet = new HashSet<string>(
            (await _store.LoadAnglicismsAsync(projectRoot)).Select(a => a.English),
            StringComparer.OrdinalIgnoreCase);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold magenta]pks writing learn[/] [grey]({targets.Count} file{(targets.Count == 1 ? "" : "s")})[/]")
            .RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        int totalActions = 0, skipped = 0;
        LearnProposal? lastProposal = null;
        string? lastJsonPath = null, lastMdPath = null;

        foreach (var file in targets)
        {
            var report = await _store.LoadReportAsync(file);
            if (report is null)
            {
                skipped++;
                continue;
            }

            var proposal = WritingLearnPlanner.Plan(report, allowlist, anglicismSet);
            var jsonPath = _paths.LearnSidecarJsonPath(file);
            var mdPath = _paths.LearnSidecarMarkdownPath(file);

            if (proposal.Actions.Count == 0)
            {
                // Nothing to propose — drop any stale LEARN files so the folder stays clean.
                try { if (System.IO.File.Exists(jsonPath)) System.IO.File.Delete(jsonPath); } catch { }
                try { if (System.IO.File.Exists(mdPath)) System.IO.File.Delete(mdPath); } catch { }
            }
            else
            {
                var json = JsonSerializer.Serialize(proposal, WritingProfileStore.JsonOptions);
                await System.IO.File.WriteAllTextAsync(jsonPath, json);
                await System.IO.File.WriteAllTextAsync(mdPath, LearnProposalRenderer.RenderMarkdown(proposal));
            }

            totalActions += proposal.Actions.Count;
            lastProposal = proposal;
            lastJsonPath = jsonPath;
            lastMdPath = mdPath;
        }

        if (targets.Count == 1 && lastProposal is not null)
        {
            // Single-file mode: keep the rich summary table.
            var p = lastProposal;
            var t = new Table().Border(TableBorder.Minimal).HideHeaders();
            t.AddColumn(""); t.AddColumn(new TableColumn("").RightAligned());
            t.AddRow("Allowlist proposals", $"[green]{p.Actions.Count(a => a.Kind == LearnActionKind.Allowlist)}[/]  ({p.Actions.Count(a => a.Kind == LearnActionKind.Allowlist && a.Accept)} accepted)");
            t.AddRow("Anglicism proposals", $"[green]{p.Actions.Count(a => a.Kind == LearnActionKind.Anglicism)}[/]  ({p.Actions.Count(a => a.Kind == LearnActionKind.Anglicism && a.Accept)} accepted)");
            t.AddRow("Lesson proposals",    $"[green]{p.Actions.Count(a => a.Kind == LearnActionKind.Lesson)}[/]  ({p.Actions.Count(a => a.Kind == LearnActionKind.Lesson && a.Accept)} accepted)");
            AnsiConsole.Write(t);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  Review:    [cyan]{lastMdPath}[/]");
            AnsiConsole.MarkupLine($"  Edit JSON: [cyan]{lastJsonPath}[/]");
            AnsiConsole.MarkupLine($"  Apply:     [bold]pks writing apply {lastJsonPath}[/]");
        }
        else
        {
            // Batch mode: terse summary.
            AnsiConsole.MarkupLine($"[green]✓[/] Generated proposals for [bold]{targets.Count - skipped}[/] file{(targets.Count - skipped == 1 ? "" : "s")} — [bold]{totalActions}[/] total actions proposed.");
            if (skipped > 0)
                AnsiConsole.MarkupLine($"[grey]  {skipped} skipped (no WRITING-REPORT.json — run lint or score first).[/]");
            AnsiConsole.MarkupLine($"[grey]  Per-file proposals at: <post-dir>/_review/<stem>.LEARN.{{md,json}}[/]");
        }

        // Machine-readable summary on the last line for scripting.
        Console.WriteLine($"RESULT: {{\"files\":{targets.Count - skipped},\"skipped\":{skipped},\"actions\":{totalActions}}}");
        return 0;
    }

    private static List<string> ResolveTargets(string path, string? filter)
    {
        if (System.IO.File.Exists(path))
            return new List<string> { path };
        if (!System.IO.Directory.Exists(path))
            return new List<string>();

        var sep = System.IO.Path.DirectorySeparatorChar;
        // Filter is matched against the file basename. Glob '*' is supported as
        // a single wildcard segment (e.g. '*.da.md' matches 'foo.da.md').
        var filterRx = filter is null ? null
            : new System.Text.RegularExpressions.Regex("^" +
                System.Text.RegularExpressions.Regex.Escape(filter)
                    .Replace("\\*", "[^/]*") + "$");

        return System.IO.Directory.EnumerateFiles(path, "*.md", System.IO.SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{sep}_review{sep}"))
            .Where(p => !p.Contains($"{sep}.pks{sep}"))
            .Where(p => !p.Contains($"{sep}node_modules{sep}"))
            .Where(p => filterRx is null || filterRx.IsMatch(System.IO.Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }
}
