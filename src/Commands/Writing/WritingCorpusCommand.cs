using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Commands.Writing;

public class WritingCorpusSettings : WritingSettings
{
    [CommandArgument(0, "<folder>")]
    [Description("Folder containing posts (each with _review/<stem>.LEARN.json from `pks writing learn`).")]
    public string Folder { get; set; } = "";

    [CommandOption("--min-posts")]
    [Description("Minimum number of posts a term must appear in to be proposed at corpus level. Default 2.")]
    public int MinPosts { get; set; } = 2;

    [CommandOption("--channel")]
    [Description("Channel label written into the corpus proposal. Default 'blog'.")]
    public string Channel { get; set; } = "blog";
}

/// Aggregates every per-post `_review/<stem>.LEARN.json` under a folder into
/// one corpus-level `LearnProposal`, written to `<folder>/_corpus.LEARN.json` +
/// `.md`. The proposal is consumable by `pks writing apply` like any other.
/// See [[WritingCorpusAggregator]] for the heuristic.
public class WritingCorpusCommand : AsyncCommand<WritingCorpusSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, WritingCorpusSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Folder))
        {
            AnsiConsole.MarkupLine("[red]error:[/] folder argument required.");
            return 1;
        }
        var root = System.IO.Path.GetFullPath(settings.Folder);
        if (!System.IO.Directory.Exists(root))
        {
            AnsiConsole.MarkupLine($"[red]error:[/] not a directory: [cyan]{root}[/]");
            return 1;
        }

        var (proposal, warnings) = await WritingCorpusAggregator.LoadAndAggregateAsync(root,
            new WritingCorpusAggregator.Options { MinPosts = settings.MinPosts, Channel = settings.Channel });

        var jsonPath = System.IO.Path.Combine(root, "_corpus.LEARN.json");
        var mdPath   = System.IO.Path.Combine(root, "_corpus.LEARN.md");
        var json = JsonSerializer.Serialize(proposal, WritingProfileStore.JsonOptions);
        await System.IO.File.WriteAllTextAsync(jsonPath, json);
        await System.IO.File.WriteAllTextAsync(mdPath, LearnProposalRenderer.RenderMarkdown(proposal));

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold magenta]pks writing corpus[/] [grey](min-posts={settings.MinPosts}, channel={settings.Channel})[/]")
            .RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        var allow = proposal.Actions.Count(a => a.Kind == LearnActionKind.Allowlist);
        var ang   = proposal.Actions.Count(a => a.Kind == LearnActionKind.Anglicism);

        var t = new Table().Border(TableBorder.Minimal).HideHeaders();
        t.AddColumn(""); t.AddColumn(new TableColumn("").RightAligned());
        t.AddRow("Allowlist proposals", $"[green]{allow}[/]");
        t.AddRow("Anglicism proposals", $"[green]{ang}[/]");
        if (warnings.Count > 0) t.AddRow("Warnings", $"[yellow]{warnings.Count}[/]");
        AnsiConsole.Write(t);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  Review:    [cyan]{mdPath}[/]");
        AnsiConsole.MarkupLine($"  Edit JSON: [cyan]{jsonPath}[/]");
        AnsiConsole.MarkupLine($"  Apply:     [bold]pks writing apply {jsonPath}[/]");

        if (warnings.Count > 0)
        {
            AnsiConsole.WriteLine();
            foreach (var w in warnings.Take(5))
                AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape(w)}");
            if (warnings.Count > 5)
                AnsiConsole.MarkupLine($"  [grey]…+{warnings.Count - 5} more[/]");
        }

        Console.WriteLine($"RESULT: {{\"allowlist\":{allow},\"anglicisms\":{ang},\"warnings\":{warnings.Count}}}");
        return 0;
    }
}
