using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;

namespace PKS.Commands.Writing;

public class WritingInitSettings : WritingSettings
{
    [CommandOption("--dry-run")]
    [Description("Show what would be created without touching the filesystem.")]
    public bool DryRun { get; set; }
}

public class WritingInitCommand : AsyncCommand<WritingInitSettings>
{
    private readonly IWritingPathResolver _paths;
    private readonly IWritingProfileStore _store;

    public WritingInitCommand(IWritingPathResolver paths, IWritingProfileStore store)
    {
        _paths = paths;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WritingInitSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks writing init[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        var cwd = Directory.GetCurrentDirectory();
        var projectRoot = _paths.ResolveProjectRoot(cwd);

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine($"[grey](dry-run)[/] Global root: [cyan]{_paths.GlobalRoot}[/]");
            AnsiConsole.MarkupLine(projectRoot is null
                ? "[grey](dry-run)[/] No git repo at cwd — would skip per-project layout."
                : $"[grey](dry-run)[/] Project root: [cyan]{projectRoot}[/]");
            return 0;
        }

        var globalFresh = !File.Exists(_paths.GlobalProfilePath);
        await _store.EnsureGlobalLayoutAsync();
        AnsiConsole.MarkupLine($"[green]✓[/] Global layout ready at [cyan]{_paths.GlobalRoot}[/]");
        if (globalFresh)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Seeded profile template at [cyan]{_paths.GlobalProfilePath}[/]");
            AnsiConsole.MarkupLine("    Next: run [bold]pks writing profile author[/] to fill it in.");
        }

        if (projectRoot is null)
        {
            AnsiConsole.MarkupLine("[yellow]![/] Current directory is not inside a git repo — skipped per-project layout.");
            AnsiConsole.MarkupLine("    Run [bold]pks writing init[/] from inside a project to also create [cyan].pks/writing/[/].");
        }
        else
        {
            await _store.EnsureProjectLayoutAsync(projectRoot);
            AnsiConsole.MarkupLine($"[green]✓[/] Project layout ready at [cyan]{projectRoot}[/]");
            AnsiConsole.MarkupLine($"[green]✓[/] Added [cyan].pks/writing/[/] to nearest [bold].gitignore[/] (if not already present)");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Next: [bold]pks writing lint <path>[/] (fast, deterministic) or [bold]pks writing score <path>[/] (full critic).");
        return 0;
    }
}
