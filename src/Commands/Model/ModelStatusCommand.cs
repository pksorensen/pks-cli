using System.ComponentModel;
using PKS.Infrastructure.Models;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Model;

[Description("Show install status of an AI model")]
public class ModelStatusCommand : AsyncCommand<ModelSettings>
{
    private readonly IModelRegistryService _registry;
    private readonly IAnsiConsole _console;

    public ModelStatusCommand(IModelRegistryService registry, IAnsiConsole console)
    {
        _registry = registry;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ModelSettings settings)
    {
        var modelName = context.Data as string ?? "";
        var entry = ModelCatalog.Find(modelName);
        var installed = await _registry.GetAsync(modelName);

        if (entry == null && installed == null)
        {
            _console.MarkupLine($"[red]Unknown model: {Markup.Escape(modelName)}[/]");
            return 1;
        }

        if (installed == null)
        {
            _console.MarkupLine($"[yellow]{Markup.Escape(modelName)} is not installed.[/]");
            _console.MarkupLine($"[dim]Run [bold]pks model {Markup.Escape(modelName)} init[/] to install.[/]");
            return 0;
        }

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Name:[/]",          Markup.Escape(installed.Name));
        grid.AddRow("[bold]Display:[/]",       Markup.Escape(installed.DisplayName));
        grid.AddRow("[bold]Version:[/]",       Markup.Escape(installed.Version));
        grid.AddRow("[bold]Install path:[/]",  Markup.Escape(installed.InstallPath));
        grid.AddRow("[bold]Installed:[/]",     installed.InstalledAt.ToString("u"));
        grid.AddRow("[bold]Size:[/]",          $"{installed.SizeBytes / (1024.0 * 1024):F1} MiB");
        grid.AddRow("[bold]Capabilities:[/]",  string.Join(", ", installed.Capabilities));
        grid.AddRow("[bold]Languages:[/]",     string.Join(", ", installed.Languages));

        if (entry != null && entry.Version != installed.Version)
        {
            _console.MarkupLine($"[yellow]Update available: {Markup.Escape(installed.Version)} → {Markup.Escape(entry.Version)}[/]");
        }
        _console.Write(grid);
        return 0;
    }
}
