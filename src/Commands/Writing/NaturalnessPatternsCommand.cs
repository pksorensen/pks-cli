using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;

namespace PKS.Commands.Writing;

public class NaturalnessPatternsShowSettings : WritingSettings
{
}

public class NaturalnessPatternsExportSettings : WritingSettings
{
    [CommandArgument(0, "[outfile]")]
    [Description("Destination markdown path. Prints to stdout when omitted.")]
    public string? OutFile { get; set; }
}

/// `pks writing naturalness patterns show` — render the global learning store
/// as a Spectre table for human inspection.
public class NaturalnessPatternsShowCommand : AsyncCommand<NaturalnessPatternsShowSettings>
{
    private readonly INaturalnessPatternStore _store;
    public NaturalnessPatternsShowCommand(INaturalnessPatternStore store) { _store = store; }

    public override async Task<int> ExecuteAsync(CommandContext context, NaturalnessPatternsShowSettings settings)
    {
        var patterns = await _store.LoadAllAsync();
        if (patterns.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no patterns yet — run `pks writing naturalness apply` on a reviewed post)[/]");
            return 0;
        }
        var t = new Table().Border(TableBorder.Rounded).Expand();
        t.AddColumn("trigger");
        t.AddColumn("accepted");
        t.AddColumn(new TableColumn("×").RightAligned());
        foreach (var p in patterns)
        {
            t.AddRow(
                Markup.Escape(p.TriggerSummary),
                Markup.Escape(p.AcceptedExample),
                p.AcceptedCount.ToString());
        }
        AnsiConsole.Write(t);
        return 0;
    }
}

/// `pks writing naturalness patterns export [outfile]` — copy the raw markdown.
public class NaturalnessPatternsExportCommand : AsyncCommand<NaturalnessPatternsExportSettings>
{
    private readonly INaturalnessPatternStore _store;
    public NaturalnessPatternsExportCommand(INaturalnessPatternStore store) { _store = store; }

    public override async Task<int> ExecuteAsync(CommandContext context, NaturalnessPatternsExportSettings settings)
    {
        var md = await _store.RenderMarkdownAsync();
        if (string.IsNullOrWhiteSpace(md))
        {
            Console.Error.WriteLine("(empty)");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(settings.OutFile))
        {
            Console.Write(md);
        }
        else
        {
            var path = System.IO.Path.GetFullPath(settings.OutFile);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllTextAsync(path, md);
            AnsiConsole.MarkupLine($"[green]exported[/] → [cyan]{Markup.Escape(path)}[/]");
        }
        return 0;
    }
}
