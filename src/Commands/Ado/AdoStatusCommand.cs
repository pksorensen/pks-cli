using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ado;

/// <summary>
/// Display current Azure DevOps authentication status
/// </summary>
[Description("Show Azure DevOps authentication status")]
public class AdoStatusCommand : Command<AdoSettings>
{
    private readonly IAzureDevOpsAuthService _authService;
    private readonly IAnsiConsole _console;

    public AdoStatusCommand(IAzureDevOpsAuthService authService, IAnsiConsole console)
    {
        _authService = authService;
        _console = console;
    }

    public override int Execute(CommandContext context, AdoSettings settings)
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync()
    {
        var credentials = await _authService.GetStoredCredentialsAsync();

        if (credentials == null)
        {
            _console.MarkupLine("[yellow]Not authenticated with Azure DevOps.[/]");
            _console.MarkupLine("[dim]Run [bold]pks ado init[/] to authenticate.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold cyan]Azure DevOps Authentication[/]");

        table.AddColumn("[bold]Property[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("User", Markup.Escape(credentials.Profile.DisplayName));
        table.AddRow("Email", Markup.Escape(credentials.Profile.EmailAddress));
        table.AddRow("Organization", Markup.Escape(credentials.SelectedOrg));
        table.AddRow("Org URL", Markup.Escape($"https://dev.azure.com/{credentials.SelectedOrg}"));
        table.AddRow("Authenticated", credentials.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
        table.AddRow("Last Refreshed", credentials.LastRefreshedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));

        _console.Write(table);
        return 0;
    }
}
