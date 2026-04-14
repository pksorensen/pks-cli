using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub;

[Description("Show GitHub authentication status")]
public class GitHubStatusCommand : Command<GitHubSettings>
{
    private readonly IGitHubAuthenticationService _authService;
    private readonly IAnsiConsole _console;

    public GitHubStatusCommand(IGitHubAuthenticationService authService, IAnsiConsole console)
    {
        _authService = authService;
        _console = console;
    }

    public override int Execute(CommandContext context, GitHubSettings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(GitHubSettings settings)
    {
        var isAuthenticated = await _authService.IsAuthenticatedAsync();

        if (!isAuthenticated)
        {
            _console.MarkupLine("[red]✗ Not authenticated with GitHub[/]");
            _console.MarkupLine("[dim]Run 'pks github init' to authenticate.[/]");
            _console.MarkupLine("[dim]Runner will NOT have git:push capability until authenticated.[/]");
            return 1;
        }

        _console.MarkupLine("[green]✓ Authenticated with GitHub[/]");
        _console.MarkupLine("[dim]Runner will announce git:push capability on next start.[/]");
        _console.MarkupLine("[dim]If git push fails with 403, re-authenticate with a PAT: pks github init --token ghp_xxxx[/]");

        if (settings.Verbose)
        {
            var storedToken = await _authService.GetStoredTokenAsync();
            if (storedToken != null)
            {
                _console.WriteLine();
                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Green)
                    .AddColumn("[yellow]Property[/]")
                    .AddColumn("[cyan]Value[/]");

                var tokenType = storedToken.AccessToken.StartsWith("ghp_") ? "PAT (ghp_)" : "OAuth (gho_)";
                table.AddRow("Type", tokenType);
                table.AddRow("Scopes", storedToken.Scopes.Count() > 0 ? string.Join(", ", storedToken.Scopes) : "[dim]not set (PAT)[/]");
                table.AddRow("Created", storedToken.CreatedAt.ToString("yyyy-MM-dd HH:mm UTC"));
                table.AddRow("Expires", storedToken.ExpiresAt?.ToString("yyyy-MM-dd HH:mm UTC") ?? "Never");
                table.AddRow("Valid", storedToken.IsValid ? "[green]Yes[/]" : "[red]No[/]");
                _console.Write(table);
            }
        }

        return 0;
    }
}
