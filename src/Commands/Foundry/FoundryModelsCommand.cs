using System.ComponentModel;
using System.Net.Http;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Foundry;

/// <summary>
/// <c>pks foundry models</c>: the model catalog of the selected Foundry resource, the same list
/// the exec credential proxy serves to tools at <c>/pks/v1/models</c>. <c>--json</c> prints that
/// document. (Not <c>pks models</c>: <c>pks model</c> is the on-device speech models.)
/// </summary>
[Description("List the model deployments on the selected Foundry resource (the catalog pks exec serves at /pks/v1/models)")]
public class FoundryModelsCommand : AsyncCommand<FoundryModelsCommand.Settings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAnsiConsole _console;

    public FoundryModelsCommand(
        IAzureFoundryAuthService authService,
        AzureFoundryAuthConfig config,
        IHttpClientFactory httpClientFactory,
        IAnsiConsole console)
    {
        _authService = authService;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _console = console;
    }

    public class Settings : FoundrySettings
    {
        [CommandOption("--json")]
        [Description("Print the catalog as JSON (OpenAI list shape with a pks object per model)")]
        public bool Json { get; set; }

        [CommandOption("--role <ROLE>")]
        [Description("Only deployments with this pks role (text, decision)")]
        public string? Role { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var creds = await _authService.GetStoredCredentialsAsync();
        if (creds == null || string.IsNullOrEmpty(creds.SelectedResourceEndpoint))
        {
            _console.MarkupLine("[yellow]No Foundry resource selected.[/] [dim]Run [bold]pks foundry init[/] first.[/]");
            return 1;
        }
        var token = await _authService.GetAccessTokenAsync(_config.CognitiveScope);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to obtain a Foundry token.[/] [dim]Try [bold]pks foundry init --force[/].[/]");
            return 1;
        }

        ModelCatalog.List catalog;
        try
        {
            catalog = await ModelCatalog.BuildAsync(
                _httpClientFactory.CreateClient(), creds.SelectedResourceEndpoint, SecretValue.From(token), creds, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _console.MarkupLine($"[red]Could not read the catalog:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (!string.IsNullOrEmpty(settings.Role))
            catalog = new ModelCatalog.List { Data = catalog.Data.Where(e => e.Pks.Roles.Contains(settings.Role, StringComparer.OrdinalIgnoreCase)).ToList() };

        if (settings.Json)
        {
            Console.WriteLine(ModelCatalog.ToJson(catalog));
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .Title($"[bold cyan]{Markup.Escape(creds.SelectedResourceName)}[/] [dim]· {catalog.Data.Count} deployments[/]");
        table.AddColumn("[bold]#[/]");
        table.AddColumn("[bold]Deployment[/]");
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
                Markup.Escape(e.Architecture.Modality),
                Markup.Escape(e.Pks.Status),
                e.Pks.Enabled ? "[green]✓[/]" : "[dim]–[/]",
                Markup.Escape(string.Join(", ", e.Pks.Roles)));
        }
        _console.Write(table);
        _console.MarkupLine("[dim]★ default · roles come from [bold]pks foundry select[/] · tools read this list at {proxy:endpoint}/pks/v1/models[/]");
        return 0;
    }
}
