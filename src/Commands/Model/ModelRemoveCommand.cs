using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Model;

[Description("Uninstall an AI model and free disk space")]
public class ModelRemoveCommand : AsyncCommand<ModelSettings>
{
    private readonly IModelRegistryService _registry;
    private readonly IAnsiConsole _console;

    public ModelRemoveCommand(IModelRegistryService registry, IAnsiConsole console)
    {
        _registry = registry;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ModelSettings settings)
    {
        var modelName = context.Data as string ?? "";
        var installed = await _registry.GetAsync(modelName);
        if (installed == null)
        {
            _console.MarkupLine($"[dim]{Markup.Escape(modelName)} is not installed.[/]");
            return 0;
        }

        _console.MarkupLine($"[yellow]This will delete {Markup.Escape(installed.InstallPath)} ({installed.SizeBytes / (1024.0 * 1024):F0} MiB).[/]");
        if (!_console.Confirm("Continue?", defaultValue: false))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        try
        {
            if (Directory.Exists(installed.InstallPath))
                Directory.Delete(installed.InstallPath, recursive: true);
            await _registry.UnregisterAsync(modelName);
            _console.MarkupLine($"[green]✓ Removed {Markup.Escape(modelName)}.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Remove failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
