using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;

namespace PKS.Commands.Writing;

public class WritingProfileShowCommand : AsyncCommand<WritingSettings>
{
    private readonly IWritingPathResolver _paths;
    private readonly IWritingProfileStore _store;

    public WritingProfileShowCommand(IWritingPathResolver paths, IWritingProfileStore store)
    {
        _paths = paths;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WritingSettings settings)
    {
        var profile = await _store.LoadProfileAsync();
        if (profile is null)
        {
            AnsiConsole.MarkupLine("[yellow]![/] No profile yet. Run [bold]pks writing init[/] then [bold]pks writing profile author[/].");
            return 1;
        }

        var cwd = System.IO.Directory.GetCurrentDirectory();
        var projectRoot = _paths.ResolveProjectRoot(cwd);
        var channel = (await _store.LoadChannelConfigAsync(projectRoot)).DefaultChannel;
        var references = await _store.LoadReferenceSamplesAsync(channel);
        var anglicisms = await _store.LoadAnglicismsAsync(projectRoot);
        var allowlist = await _store.LoadAllowlistAsync();

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks writing profile[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  Profile:    [cyan]{_paths.GlobalProfilePath}[/]");
        AnsiConsole.MarkupLine($"  Anglicisms: [cyan]{_paths.GlobalAnglicismsPath}[/]  ([bold]{anglicisms.Count}[/] entries)");
        AnsiConsole.MarkupLine($"  Allowlist:  [cyan]{_paths.GlobalAllowlistPath}[/]  ([bold]{allowlist.Count}[/] terms)");
        AnsiConsole.MarkupLine($"  Channel:    [cyan]{channel}[/]  (rubric: {_paths.GlobalChannelRubricPath(channel)})");
        AnsiConsole.MarkupLine($"  References: [cyan]{_paths.GlobalReferenceChannelDir(channel)}[/]  ([bold]{references.Count}[/] samples)");
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Panel(Markup.Escape(profile.Trim()))
            .Header("[bold]profile.md[/]")
            .Border(BoxBorder.Rounded));
        return 0;
    }
}
