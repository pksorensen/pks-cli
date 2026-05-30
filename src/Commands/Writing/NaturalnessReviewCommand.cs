using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Commands.Writing;

public class NaturalnessReviewSettings : WritingSettings
{
    [CommandArgument(0, "<file>")]
    [Description("Markdown file whose candidates sidecar to review.")]
    public string File { get; set; } = "";
}

/// `pks writing naturalness review <file>` — interactive Spectre loop: for each
/// candidate show the original + 3 alternatives, prompt A/B/C/skip/other/quit.
/// Persists picks to `_review/<stem>.NATURALNESS-PICKS.json` (incremental save).
public class NaturalnessReviewCommand : AsyncCommand<NaturalnessReviewSettings>
{
    private readonly INaturalnessPicksStore _store;

    public NaturalnessReviewCommand(INaturalnessPicksStore store) { _store = store; }

    public override async Task<int> ExecuteAsync(CommandContext context, NaturalnessReviewSettings settings)
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
        if (candidates is null || candidates.Candidates.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]no candidates sidecar found.[/] Run `pks writing naturalness prompt … | accept …` first.");
            return 1;
        }

        // Resume existing picks file when present (idempotent).
        var picksFile = await _store.LoadPicksAsync(full) ?? new NaturalnessPicksFile
        {
            Post = full,
            ReviewedAt = DateTime.UtcNow,
        };
        var existingById = picksFile.Picks.ToDictionary(p => p.CandidateId, StringComparer.Ordinal);

        int total = candidates.Candidates.Count;
        int i = 0;
        foreach (var cand in candidates.Candidates)
        {
            i++;
            if (existingById.TryGetValue(cand.Id, out var prev) && prev.Applied)
            {
                continue; // already applied, skip
            }

            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[bold magenta]naturalness review[/] [grey]({i}/{total})[/]").RuleStyle("magenta dim"));
            AnsiConsole.WriteLine();
            var criticsBadge = (cand.CriticsFlagging is { Count: > 0 })
                ? $" · critics: {string.Join(", ", cand.CriticsFlagging)}"
                : "";
            AnsiConsole.MarkupLine($"[grey]line {cand.Line} · {Markup.Escape(cand.Id)}{Markup.Escape(criticsBadge)}[/]");
            AnsiConsole.MarkupLine($"[white]{Markup.Escape(cand.Original)}[/]");
            AnsiConsole.WriteLine();
            // Render issues (multi-source if available, fall back to single).
            if (cand.Issues is { Count: > 0 })
            {
                foreach (var iss in cand.Issues)
                {
                    AnsiConsole.MarkupLine($"[yellow]issue ({Markup.Escape(iss.Source)}):[/] {Markup.Escape(iss.Text)}");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]issue:[/] {Markup.Escape(cand.Issue)}");
            }
            AnsiConsole.WriteLine();

            // Build dynamic labels: "A-opus" / "B-gpt5" when multi-source, plain
            // "A"/"B"/"C" when a single critic (or the alt has no Source — old file).
            var sources = cand.Alternatives
                .Select(a => a.Source)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            bool multiSource = sources.Count > 1;
            var labelsByAlt = cand.Alternatives
                .Select(a => multiSource && !string.IsNullOrEmpty(a.Source)
                    ? $"{a.Label}-{a.Source}"
                    : a.Label)
                .ToList();

            var t = new Table().Border(TableBorder.Rounded).Expand();
            t.AddColumn("");
            t.AddColumn("alternative");
            t.AddColumn(new TableColumn("rationale"));
            t.AddColumn(new TableColumn("a-like").RightAligned());
            for (int k = 0; k < cand.Alternatives.Count; k++)
            {
                var alt = cand.Alternatives[k];
                t.AddRow(
                    $"[bold]{Markup.Escape(labelsByAlt[k])}[/]",
                    Markup.Escape(alt.Text),
                    $"[grey]{Markup.Escape(alt.Rationale)}[/]",
                    $"{alt.Authorlikeness:F2}");
            }
            AnsiConsole.Write(t);
            AnsiConsole.WriteLine();

            var choices = new List<string>(labelsByAlt) { "skip", "other", "quit" };
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("[bold]Pick:[/]")
                .AddChoices(choices));

            if (choice == "quit")
            {
                await _store.SavePicksAsync(full, picksFile);
                AnsiConsole.MarkupLine("[yellow]saved partial progress and exited.[/]");
                return 0;
            }

            var pick = new NaturalnessPick
            {
                CandidateId = cand.Id,
                Chosen = choice,
            };
            if (choice == "other")
            {
                var custom = AnsiConsole.Prompt(new TextPrompt<string>("[bold]Your rewrite:[/]")
                    .AllowEmpty());
                if (string.IsNullOrWhiteSpace(custom))
                {
                    AnsiConsole.MarkupLine("[grey]empty — treating as skip[/]");
                    pick.Chosen = "skip";
                }
                else
                {
                    pick.CustomText = custom;
                }
            }

            // Upsert
            if (existingById.ContainsKey(cand.Id))
            {
                var idx = picksFile.Picks.FindIndex(p => p.CandidateId == cand.Id);
                if (idx >= 0) picksFile.Picks[idx] = pick;
            }
            else
            {
                picksFile.Picks.Add(pick);
                existingById[cand.Id] = pick;
            }

            // Persist after every choice so a crash doesn't lose progress.
            await _store.SavePicksAsync(full, picksFile);
        }

        // Final summary
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[bold green]review complete[/]").RuleStyle("green dim"));
        AnsiConsole.WriteLine();
        var summary = new Table().Border(TableBorder.Minimal);
        summary.AddColumn("");
        summary.AddColumn(new TableColumn("count").RightAligned());
        var groups = picksFile.Picks.GroupBy(p => p.Chosen).ToDictionary(g => g.Key, g => g.Count());
        foreach (var k in groups.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            summary.AddRow(k, groups[k].ToString());
        }
        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        var save = AnsiConsole.Prompt(new TextPrompt<string>("[bold]Save picks?[/]")
            .AllowEmpty()
            .DefaultValue("y"));
        if (!string.IsNullOrWhiteSpace(save) && save.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("[yellow]picks NOT saved (final write skipped — incremental writes still on disk).[/]");
            return 0;
        }

        picksFile.ReviewedAt = DateTime.UtcNow;
        await _store.SavePicksAsync(full, picksFile);
        AnsiConsole.MarkupLine("[green]picks saved.[/]");
        return 0;
    }
}
