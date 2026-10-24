using System.ComponentModel;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Models;

/// <summary>
/// <c>pks models</c>: the model catalog across every provider registered with pks — the list
/// tools read through <c>pks exec</c> at <c>/pks/v1/models</c>. <c>--json</c> prints that document.
/// (<c>pks model</c>, singular, is the on-device speech models.)
/// </summary>
[Description("List the models reachable through the registered providers (the catalog pks exec serves at /pks/v1/models)")]
public class ModelsCommand : AsyncCommand<ModelsCommand.Settings>
{
    private readonly IModelCatalogService _catalog;
    private readonly IAnsiConsole _console;

    public ModelsCommand(IModelCatalogService catalog, IAnsiConsole console)
    {
        _catalog = catalog;
        _console = console;
    }

    public class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Print the catalog as JSON (OpenAI list shape with a pks object per model)")]
        public bool Json { get; set; }

        [CommandOption("--role <ROLE>")]
        [Description("Only models with this pks role (text, decision)")]
        public string? Role { get; set; }

        [CommandOption("--provider <KIND>")]
        [Description("Only one provider kind: foundry, anthropic, openai, gemini, moonshot, typesafe or openai-compatible")]
        public string? Provider { get; set; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings)
        => RunAsync(_catalog, _console, settings.Provider, settings.Role, settings.Json);

    /// <summary>Shared with <c>pks foundry models</c>, which is this command pinned to one kind.</summary>
    public static async Task<int> RunAsync(IModelCatalogService service, IAnsiConsole console, string? kind, string? role, bool json)
    {
        CatalogResult result;
        try
        {
            result = await service.BuildAsync(kind, CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            console.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        var catalog = result.Catalog;
        if (!string.IsNullOrEmpty(role))
            catalog = new ProviderCatalog.List { Data = catalog.Data.Where(e => e.Pks.Roles.Contains(role, StringComparer.OrdinalIgnoreCase)).ToList() };

        if (json)
        {
            Console.WriteLine(ProviderCatalog.ToJson(catalog));
            foreach (var (k, error) in result.Failures)
                Console.Error.WriteLine($"{k}: {error}");
            return result.Failures.Count > 0 && catalog.Data.Count == 0 ? 1 : 0;
        }

        var registered = await service.RegisteredKindsAsync();
        if (registered.Count == 0)
        {
            console.MarkupLine("[yellow]No provider is registered.[/] [dim]Sign in with [bold]pks foundry init[/], [bold]pks providers init[/] (Anthropic/OpenAI), [bold]pks google init[/], [bold]pks moonshot init[/] or [bold]pks typesafe init[/], or set OPENAI_BASE_URL.[/]");
            return 1;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .Title($"[bold cyan]{Markup.Escape(string.Join(", ", kind is null ? registered : [kind]))}[/] [dim]· {catalog.Data.Count} models[/]");
        table.AddColumn("[bold]#[/]");
        table.AddColumn("[bold]Model[/]");
        table.AddColumn("[bold]Provider[/]");
        table.AddColumn("[bold]Modality[/]");
        table.AddColumn("[bold]Status[/]");
        table.AddColumn("[bold]Enabled[/]");
        table.AddColumn("[bold]Roles[/]");
        var n = 0;
        foreach (var e in catalog.Data)
        {
            n++;
            var name = e.Pks.Default ? $"[bold]{Markup.Escape(e.Id)}[/] [yellow]★[/]" : Markup.Escape(e.Id);
            table.AddRow(
                n.ToString(),
                name,
                Markup.Escape(e.Pks.Provider),
                Markup.Escape(e.Architecture.Modality),
                Markup.Escape(e.Pks.Status),
                e.Pks.Enabled ? "[green]✓[/]" : "[dim]–[/]",
                Markup.Escape(string.Join(", ", e.Pks.Roles)));
        }
        console.Write(table);
        foreach (var (k, error) in result.Failures)
            console.MarkupLine($"[yellow]{Markup.Escape(k)}:[/] [dim]{Markup.Escape(error)}[/]");
        console.MarkupLine("[dim]★ default · roles come from [bold]pks foundry select[/] · a tool started by pks exec reads this list at {proxy:endpoint}/pks/v1/models and may post any text model to {proxy:endpoint}/pks/v1/chat/completions[/]");
        return result.Failures.Count > 0 && catalog.Data.Count == 0 ? 1 : 0;
    }
}
