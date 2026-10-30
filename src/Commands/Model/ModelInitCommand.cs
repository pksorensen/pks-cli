using System.ComponentModel;
using PKS.Infrastructure.Models;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Model;

[Description("Download and install an AI model")]
public class ModelInitCommand : AsyncCommand<ModelSettings>
{
    private readonly IModelRegistryService _registry;
    private readonly IModelDownloadService _download;
    private readonly IAnsiConsole _console;

    public ModelInitCommand(IModelRegistryService registry, IModelDownloadService download, IAnsiConsole console)
    {
        _registry = registry;
        _download = download;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ModelSettings settings)
    {
        // Spectre's branch-data carries the model name. Defensive default for direct test invocation.
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

        var existing = await _registry.GetAsync(entry.Name);
        if (existing != null && existing.Version == entry.Version)
        {
            _console.MarkupLine($"[green]{Markup.Escape(entry.Name)} {existing.Version} is already installed at {Markup.Escape(existing.InstallPath)}.[/]");
            _console.MarkupLine($"[dim]Use [bold]pks model {Markup.Escape(entry.Name)} update[/] to re-download or [bold]remove[/] first.[/]");
            return 0;
        }

        _console.MarkupLine($"[cyan]Installing {Markup.Escape(entry.DisplayName)} ({entry.Version})[/]");
        _console.MarkupLine($"[dim]  Download size: ~{Math.Round(entry.ExpectedSizeBytes / (1024.0 * 1024), 0)} MiB[/]");
        _console.MarkupLine($"[dim]  Capabilities:  {string.Join(", ", entry.Capabilities)}[/]");
        _console.WriteLine();

        if (!settings.Yes && !_console.Confirm("[yellow]Proceed with download?[/]", defaultValue: true))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        var installDir = _registry.GetInstallDirectory(entry.Name);
        try
        {
            await ModelInstaller.InstallAsync(entry, installDir, _download, _registry);
            _console.MarkupLine($"[green]✓ Installed {Markup.Escape(entry.Name)} at {Markup.Escape(installDir)}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Install failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
