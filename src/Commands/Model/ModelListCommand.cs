using System.ComponentModel;
using PKS.Infrastructure.Models;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Model;

[Description("List known and installed AI models")]
public class ModelListCommand : AsyncCommand<ModelSettings>
{
    private readonly IModelRegistryService _registry;
    private readonly IAnsiConsole _console;

    public ModelListCommand(IModelRegistryService registry, IAnsiConsole console)
    {
        _registry = registry;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ModelSettings settings)
    {
        var installed = await _registry.ListInstalledAsync();
        var byName = installed.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Name").AddColumn("Status").AddColumn("Version")
            .AddColumn("Size").AddColumn("Capabilities").AddColumn("Languages");

        foreach (var entry in ModelCatalog.Known)
        {
            byName.TryGetValue(entry.Name, out var inst);
            var status = inst == null
                ? "[dim]not installed[/]"
                : (inst.Version == entry.Version ? "[green]installed[/]" : "[yellow]update available[/]");
            var version = inst?.Version ?? entry.Version;
            var size = inst != null ? FormatBytes(inst.SizeBytes) : $"~{FormatBytes(entry.ExpectedSizeBytes)}";
            var caps = string.Join(", ", entry.Capabilities);
            var langCount = entry.Languages.Count;
            var langs = langCount <= 4
                ? string.Join(", ", entry.Languages)
                : $"{string.Join(", ", entry.Languages.Take(4))} +{langCount - 4} more";
            table.AddRow(entry.Name, status, version, size, caps, langs);
        }

        _console.Write(table);
        return 0;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KiB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MiB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GiB",
    };
}
