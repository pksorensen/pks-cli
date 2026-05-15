using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;

namespace PKS.Commands.Brain;

public class BrainInitSettings : BrainSettings
{
    [CommandOption("--dry-run")]
    [Description("Show what would be created without touching the filesystem.")]
    public bool DryRun { get; set; }
}

public class BrainInitCommand : AsyncCommand<BrainInitSettings>
{
    private readonly IBrainPathResolver _paths;
    private readonly IBrainIndexStore _store;

    public BrainInitCommand(IBrainPathResolver paths, IBrainIndexStore store)
    {
        _paths = paths;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainInitSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain init[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        var cwd = Directory.GetCurrentDirectory();
        var projectRoot = _paths.ResolveProjectRoot(cwd);

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine($"[grey](dry-run)[/] Global root: [cyan]{_paths.GlobalRoot}[/]");
            AnsiConsole.MarkupLine(projectRoot is null
                ? "[grey](dry-run)[/] No git repo at cwd — skipping per-project layout."
                : $"[grey](dry-run)[/] Project root: [cyan]{projectRoot}[/]");
            return 0;
        }

        await _store.EnsureGlobalLayoutAsync();
        AnsiConsole.MarkupLine($"[green]✓[/] Global layout ready at [cyan]{_paths.GlobalRoot}[/]");

        if (projectRoot is null)
        {
            AnsiConsole.MarkupLine("[yellow]![/] Current directory is not inside a git repo — skipped per-project layout.");
            AnsiConsole.MarkupLine("    Run [bold]pks brain init[/] from inside a project to also create [cyan].pks/brain/[/].");
        }
        else
        {
            await _store.EnsureProjectLayoutAsync(projectRoot);
            AnsiConsole.MarkupLine($"[green]✓[/] Project layout ready at [cyan]{projectRoot}[/]");
            AnsiConsole.MarkupLine($"[green]✓[/] Added [cyan].pks/brain/[/] to nearest [bold].gitignore[/] (if not already present)");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Next: run [bold]pks brain ingest[/] to populate the global raw layer from your Claude session history.");
        return 0;
    }
}
