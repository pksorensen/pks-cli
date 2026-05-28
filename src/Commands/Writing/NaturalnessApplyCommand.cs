using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Commands.Writing;

public class NaturalnessApplySettings : WritingSettings
{
    [CommandArgument(0, "<file>")]
    [Description("Markdown file whose picks to apply.")]
    public string File { get; set; } = "";

    [CommandOption("--yes")]
    [Description("Skip the confirmation prompt and apply the diff immediately.")]
    public bool Yes { get; set; }

    [CommandOption("--dry-run")]
    [Description("Print the diff but don't touch the source file or pattern store.")]
    public bool DryRun { get; set; }
}

/// `pks writing naturalness apply <file>` — reads picks, generates a unified
/// diff, confirms, applies in-place. Appends accepted patterns to the global
/// learning store, flips `applied: true` for idempotency.
public class NaturalnessApplyCommand : AsyncCommand<NaturalnessApplySettings>
{
    private readonly INaturalnessPicksStore _store;
    private readonly INaturalnessApplier _applier;
    private readonly INaturalnessPatternStore _patterns;

    public NaturalnessApplyCommand(
        INaturalnessPicksStore store,
        INaturalnessApplier applier,
        INaturalnessPatternStore patterns)
    {
        _store = store;
        _applier = applier;
        _patterns = patterns;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, NaturalnessApplySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.File))
        {
            AnsiConsole.MarkupLine("[red]error:[/] file argument required.");
            return 1;
        }
        var full = System.IO.Path.GetFullPath(settings.File);
        if (!System.IO.File.Exists(full))
        {
            AnsiConsole.MarkupLine($"[red]error:[/] not found: [cyan]{Markup.Escape(full)}[/]");
            return 1;
        }

        var candidates = await _store.LoadCandidatesAsync(full);
        var picks = await _store.LoadPicksAsync(full);
        if (candidates is null || picks is null)
        {
            AnsiConsole.MarkupLine("[yellow]nothing to apply.[/] Run `naturalness review` first.");
            return 1;
        }

        var content = await System.IO.File.ReadAllTextAsync(full);
        var plan = _applier.Plan(content, candidates, picks);

        if (plan.Edits.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]no unapplied picks; nothing to do.[/]");
            return 0;
        }

        AnsiConsole.Write(new Rule($"[bold magenta]naturalness apply[/] [grey]({plan.Edits.Count} edit(s))[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(plan.UnifiedDiff);

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine("[grey]dry-run: nothing written.[/]");
            return 0;
        }

        if (!settings.Yes)
        {
            var ok = AnsiConsole.Confirm("Apply these edits?");
            if (!ok)
            {
                AnsiConsole.MarkupLine("[grey]aborted.[/]");
                return 0;
            }
        }

        var result = _applier.Apply(content, plan);
        await System.IO.File.WriteAllTextAsync(full, result.NewContent);

        // Mark applied + persist patterns
        var appliedIds = new HashSet<string>(result.Applied.Select(e => e.CandidateId));
        foreach (var pick in picks.Picks)
        {
            if (appliedIds.Contains(pick.CandidateId)) pick.Applied = true;
        }
        await _store.SavePicksAsync(full, picks);

        foreach (var edit in result.Applied)
        {
            var pattern = new NaturalnessPattern
            {
                TriggerSummary = edit.TriggerSummary,
                AcceptedExample = edit.Replacement,
                RejectedExample = edit.Original,
                FirstSeenSource = $"{DateTime.UtcNow:yyyy-MM-dd} / {System.IO.Path.GetFileName(full)}:{edit.Line}",
                AcceptedCount = 1,
            };
            await _patterns.UpsertAsync(pattern);
        }

        var t = new Table().Border(TableBorder.Minimal).HideHeaders();
        t.AddColumn(""); t.AddColumn(new TableColumn("").RightAligned());
        t.AddRow("Applied",   $"[green]{result.Applied.Count}[/]");
        t.AddRow("Skipped",   $"[grey]{result.Skipped.Count}[/]");
        t.AddRow("Patterns",  $"[green]{result.Applied.Count}[/] upserted");
        if (result.Warnings.Count > 0)
            t.AddRow("Warnings", $"[yellow]{result.Warnings.Count}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(t);
        foreach (var w in result.Warnings)
            AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape(w)}");
        return 0;
    }
}
