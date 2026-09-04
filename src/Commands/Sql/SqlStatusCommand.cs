using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Sql;

/// <summary>
/// Shows who `pks sql` will connect as. The account name is read out of the token itself, so it is
/// the name the database server will see — not what was typed at sign-in.
/// </summary>
[Description("Show which account and tenant `pks sql` will use")]
public class SqlStatusCommand : AsyncCommand<SqlStatusCommand.Settings>
{
    private readonly IAzureAuthService _authService;
    private readonly IConfigurationService _configuration;
    private readonly IAnsiConsole _console;

    public SqlStatusCommand(IAzureAuthService authService, IConfigurationService configuration, IAnsiConsole console)
    {
        _authService = authService;
        _configuration = configuration;
        _console = console;
    }

    public class Settings : SqlSettings { }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var credentials = await _authService.GetStoredCredentialsAsync(SqlAuth.StorageKey);
        if (credentials == null || string.IsNullOrEmpty(credentials.RefreshToken))
        {
            _console.MarkupLine("[yellow]Not signed in for Azure SQL.[/]");
            _console.MarkupLine("[dim]Run [bold]pks sqlserver init --email you@example.com[/] (or --tenant <id>).[/]");
            return 1;
        }

        _console.MarkupLine($"Tenant:      [bold]{Markup.Escape(credentials.TenantId)}[/]");
        _console.MarkupLine($"Signed in:   [dim]{credentials.CreatedAt:yyyy-MM-dd HH:mm} UTC[/]");

        var token = await _authService.GetAccessTokenAsync(SqlAuth.Scope, SqlAuth.StorageKey);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]The stored session no longer works — sign in again with --force.[/]");
            return 1;
        }

        var upn = JwtClaims.Read(token, "upn") ?? JwtClaims.Read(token, "preferred_username") ?? JwtClaims.Read(token, "unique_name");
        if (upn != null)
            _console.MarkupLine($"Account:     [bold]{Markup.Escape(upn)}[/]");

        var audience = JwtClaims.Read(token, "aud");
        if (audience != null)
            _console.MarkupLine($"Token for:   [dim]{Markup.Escape(audience)}[/]");

        var defaults = await SqlDefaults.LoadAsync(_configuration);
        if (defaults != null && !string.IsNullOrEmpty(defaults.Server))
        {
            _console.MarkupLine($"Server:      [bold]{Markup.Escape(defaults.Server)}[/]");
            if (!string.IsNullOrEmpty(defaults.Database))
                _console.MarkupLine($"Database:    [bold]{Markup.Escape(defaults.Database)}[/]");
        }
        else
        {
            _console.MarkupLine("[dim]No default server — name one on every query, or run `pks sqlserver init` again.[/]");
        }

        _console.MarkupLine("[green]Token acquired.[/]");
        return 0;
    }

}
