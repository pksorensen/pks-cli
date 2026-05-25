using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Commands.Writing;

public class WritingApplySettings : WritingSettings
{
    [CommandArgument(0, "<proposal>")]
    [Description("Path to the .LEARN.json proposal produced by `pks writing learn`.")]
    public string Proposal { get; set; } = "";

    [CommandOption("--dry-run")]
    [Description("Print what would be applied without touching the profile.")]
    public bool DryRun { get; set; }
}

/// Consumes a `LearnProposal` JSON file and applies every action where
/// `accept == true`. Deterministic, idempotent (the store already deduplicates),
/// and reports per-kind counts as JSON on the last stdout line for piping.
public class WritingApplyCommand : AsyncCommand<WritingApplySettings>
{
    private readonly IWritingProfileStore _store;

    public WritingApplyCommand(IWritingProfileStore store)
    {
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WritingApplySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Proposal))
        {
            AnsiConsole.MarkupLine("[red]error:[/] proposal path required.");
            return 1;
        }
        var path = System.IO.Path.GetFullPath(settings.Proposal);
        if (!System.IO.File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]error:[/] not found: [cyan]{path}[/]");
            return 1;
        }

        LearnProposal? proposal;
        try
        {
            var text = await System.IO.File.ReadAllTextAsync(path);
            proposal = JsonSerializer.Deserialize<LearnProposal>(text, WritingProfileStore.JsonOptions);
        }
        catch (JsonException jx)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] proposal JSON parse failure — {Markup.Escape(jx.Message)}");
            return 1;
        }
        if (proposal is null)
        {
            AnsiConsole.MarkupLine("[red]error:[/] proposal parsed as null.");
            return 1;
        }

        var result = new ApplyResult();

        foreach (var action in proposal.Actions)
        {
            if (!action.Accept)
            {
                result.Rejected++;
                continue;
            }

            switch (action.Kind)
            {
                case LearnActionKind.Allowlist:
                    if (string.IsNullOrWhiteSpace(action.Term))
                    {
                        result.Warnings.Add("Allowlist action missing 'term'");
                        continue;
                    }
                    if (!settings.DryRun) await _store.AddAllowedTermAsync(action.Term.Trim());
                    result.AllowlistAdded++;
                    result.Accepted++;
                    break;

                case LearnActionKind.Anglicism:
                    if (string.IsNullOrWhiteSpace(action.Term))
                    {
                        result.Warnings.Add("Anglicism action missing 'term'");
                        continue;
                    }
                    if (!settings.DryRun)
                    {
                        await _store.AddAnglicismAsync(new AnglicismEntry
                        {
                            English = action.Term.Trim(),
                            DanishAlternatives = action.DanishAlternatives,
                            Note = action.Note,
                        });
                    }
                    result.AnglicismsAdded++;
                    result.Accepted++;
                    break;

                case LearnActionKind.Lesson:
                    if (string.IsNullOrWhiteSpace(action.Lesson) || string.IsNullOrWhiteSpace(action.Dimension))
                    {
                        result.Warnings.Add("Lesson action missing 'dimension' or 'lesson'");
                        continue;
                    }
                    if (!settings.DryRun)
                    {
                        await _store.AppendLessonAsync(action.Dimension!, action.Lesson!,
                            proposal.SourcePath ?? "bundle");
                    }
                    result.LessonsAppended++;
                    result.Accepted++;
                    break;
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold magenta]pks writing apply[/] [grey]({(settings.DryRun ? "dry-run" : "live")})[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        var t = new Table().Border(TableBorder.Minimal).HideHeaders();
        t.AddColumn(""); t.AddColumn(new TableColumn("").RightAligned());
        t.AddRow("Accepted",          $"[green]{result.Accepted}[/]");
        t.AddRow("Rejected",          $"[grey]{result.Rejected}[/]");
        t.AddRow("Allowlist added",   $"[green]{result.AllowlistAdded}[/]");
        t.AddRow("Anglicisms added",  $"[green]{result.AnglicismsAdded}[/]");
        t.AddRow("Lessons appended",  $"[green]{result.LessonsAppended}[/]");
        if (result.Warnings.Count > 0)
            t.AddRow("Warnings",      $"[yellow]{result.Warnings.Count}[/]");
        AnsiConsole.Write(t);

        if (result.Warnings.Count > 0)
        {
            AnsiConsole.WriteLine();
            foreach (var w in result.Warnings) AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape(w)}");
        }

        // Machine-readable final line for scripting/agent consumption.
        var summary = JsonSerializer.Serialize(result, WritingProfileStore.JsonOptions);
        AnsiConsole.WriteLine();
        Console.WriteLine("RESULT: " + summary.Replace("\n", " "));
        return 0;
    }
}
