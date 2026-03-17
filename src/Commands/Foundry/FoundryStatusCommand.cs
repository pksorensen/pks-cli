using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Foundry;

/// <summary>
/// Displays current Azure AI Foundry authentication status including selected
/// subscription, resource, endpoint, default model, and token refresh timestamps.
/// </summary>
[Description("Show Azure AI Foundry authentication status")]
public class FoundryStatusCommand : Command<FoundrySettings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly IAnsiConsole _console;

    public FoundryStatusCommand(IAzureFoundryAuthService authService, IAnsiConsole console)
    {
        _authService = authService;
        _console = console;
    }

    public override int Execute(CommandContext context, FoundrySettings settings)
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync()
    {
        var credentials = await _authService.GetStoredCredentialsAsync();

        if (credentials == null)
        {
            _console.MarkupLine("[yellow]Not authenticated with Azure AI Foundry.[/]");
            _console.MarkupLine("[dim]Run [bold]pks foundry init[/] to authenticate.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold cyan]Azure AI Foundry Authentication[/]");

        table.AddColumn("[bold]Property[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("Tenant ID", Markup.Escape(credentials.TenantId));
        table.AddRow("Subscription", Markup.Escape(credentials.SelectedSubscriptionName));
        table.AddRow("Resource", Markup.Escape(credentials.SelectedResourceName));
        table.AddRow("Endpoint", Markup.Escape(credentials.SelectedResourceEndpoint));
        table.AddRow("Default Model", Markup.Escape(credentials.DefaultModel));
        table.AddRow("Resource Group", Markup.Escape(credentials.SelectedResourceGroup));
        table.AddRow("Authenticated", credentials.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
        table.AddRow("Last Refreshed", credentials.LastRefreshedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
        var tokenStatus = string.IsNullOrEmpty(credentials.RefreshToken) ? "[red]Missing[/]" : "[green]Present[/]";
        table.AddRow("Refresh Token", tokenStatus);

        _console.Write(table);
        return 0;
    }
}
