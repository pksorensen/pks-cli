using System.ComponentModel;
using PKS.Infrastructure.Models;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Model;

[Description("Update an installed AI model to the latest catalog version")]
public class ModelUpdateCommand : AsyncCommand<ModelSettings>
{
    private readonly IModelRegistryService _registry;
    private readonly IModelDownloadService _download;
    private readonly IAnsiConsole _console;

    public ModelUpdateCommand(IModelRegistryService registry, IModelDownloadService download, IAnsiConsole console)
    {
        _registry = registry;
        _download = download;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ModelSettings settings)
    {
        var modelName = context.Data as string;
        if (string.IsNullOrEmpty(modelName))
        {
            _console.MarkupLine("[red]No model name in command context. Did Program.cs register the branch with WithData(name)?[/]");
            return 1;
        }

        var entry = ModelCatalog.Find(modelName);
        if (entry == null)
        {
            _console.MarkupLine($"[red]Unknown model: {Markup.Escape(modelName)}[/]");
            return 1;
        }

        var installed = await _registry.GetAsync(entry.Name);
        if (installed == null)
        {
            _console.MarkupLine($"[yellow]{Markup.Escape(entry.Name)} is not installed.[/]");
            _console.MarkupLine($"[dim]Run [bold]pks model {Markup.Escape(entry.Name)} init[/] to install it first.[/]");
            return 0;
        }

        if (installed.Version == entry.Version)
        {
            _console.MarkupLine($"[green]{Markup.Escape(entry.Name)} {installed.Version} is already up to date.[/]");
            return 0;
        }

        _console.MarkupLine($"[cyan]Updating {Markup.Escape(entry.DisplayName)}: {Markup.Escape(installed.Version)} → {Markup.Escape(entry.Version)}[/]");
        _console.MarkupLine($"[dim]  Download size: ~{Math.Round(entry.ExpectedSizeBytes / (1024.0 * 1024), 0)} MiB[/]");
        _console.WriteLine();

        if (!settings.Yes && !_console.Confirm("[yellow]Proceed with update?[/]", defaultValue: true))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        // Unregister and remove the old install dir before re-running install so a
        // half-done update doesn't leave inconsistent state.
        try
        {
            if (Directory.Exists(installed.InstallPath))
                Directory.Delete(installed.InstallPath, recursive: true);
            await _registry.UnregisterAsync(entry.Name);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Failed to remove existing install: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        var installDir = _registry.GetInstallDirectory(entry.Name);
        try
        {
            await ModelInstaller.InstallAsync(entry, installDir, _download, _registry);
            _console.MarkupLine($"[green]✓ Updated {Markup.Escape(entry.Name)} to {Markup.Escape(entry.Version)} at {Markup.Escape(installDir)}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Update failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
