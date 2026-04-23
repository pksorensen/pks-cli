using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ado;

/// <summary>
/// Interactive Azure DevOps authentication via OAuth2 authorization code + PKCE.
/// Opens browser for user consent, exchanges code for tokens, and stores
/// credentials for use with git credential helper.
/// </summary>
[Description("Authenticate with Azure DevOps")]
public class AdoInitCommand : Command<AdoInitCommand.Settings>
{
    private readonly IAzureDevOpsAuthService _authService;
    private readonly IAnsiConsole _console;

    public AdoInitCommand(IAzureDevOpsAuthService authService, IAnsiConsole console)
    {
        _authService = authService;
        _console = console;
    }

    public class Settings : AdoSettings
    {
        [CommandOption("-f|--force")]
        [Description("Force re-authentication even if already authenticated")]
        public bool Force { get; set; }

        [CommandOption("-t|--tenant")]
        [Description("Azure AD tenant ID (defaults to 'common' — prompts for email/tenant if omitted)")]
        public string? TenantId { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!settings.Force && await _authService.IsAuthenticatedAsync())
        {
            var credentials = await _authService.GetStoredCredentialsAsync();
            _console.MarkupLine($"[green]Already authenticated as [bold]{Markup.Escape(credentials!.Profile.DisplayName)}[/] ({Markup.Escape(credentials.SelectedOrg)})[/]");
            _console.MarkupLine("[dim]Use [bold]--force[/] to re-authenticate.[/]");
            return 0;
        }

        if (settings.Force)
            await _authService.ClearStoredCredentialsAsync();

        var (tenantId, loginHint) = ResolveTenant(settings.TenantId);

        _console.MarkupLine("[cyan]Starting Azure DevOps authentication...[/]");
        _console.MarkupLine("[dim]A browser window will open. If it doesn't, use the URL printed below.[/]");
        _console.WriteLine();

        AdoAuthResult result;
        try
        {
            result = await _authService.InitiateAsync(tenantId, loginHint);
        }
        catch (OperationCanceledException)
        {
            _console.MarkupLine("[red]Authentication timed out.[/]");
            return 1;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Authentication failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        // Select organization
        AdoAccount selectedOrg;
        if (result.Accounts.Count == 0)
        {
            _console.MarkupLine("[red]No Azure DevOps organizations found for this account.[/]");
            return 1;
        }
        else if (result.Accounts.Count == 1)
        {
            selectedOrg = result.Accounts[0];
            _console.MarkupLine($"[dim]Using organization: [bold]{Markup.Escape(selectedOrg.AccountName)}[/][/]");
        }
        else
        {
            var orgName = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Azure DevOps organization:[/]")
                    .AddChoices(result.Accounts.Select(a => a.AccountName)));

            selectedOrg = result.Accounts.First(a => a.AccountName == orgName);
        }

        await _authService.CompleteAsync(result, selectedOrg, tenantId);

        // Display success
        _console.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold green]Authentication Successful[/]");

        table.AddColumn("[bold]Property[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("User", Markup.Escape(result.Profile.DisplayName));
        table.AddRow("Email", Markup.Escape(result.Profile.EmailAddress));
        table.AddRow("Tenant", Markup.Escape(tenantId));
        table.AddRow("Organization", Markup.Escape(selectedOrg.AccountName));
        table.AddRow("Org URL", Markup.Escape($"https://dev.azure.com/{selectedOrg.AccountName}"));

        _console.Write(table);

        _console.WriteLine();
        _console.MarkupLine("[dim]Tip: Set GIT_ASKPASS to use these credentials with Git:[/]");
        _console.MarkupLine("[dim]  export GIT_ASKPASS=\"pks git askpass\"[/]");

        return 0;
    }

    private (string tenantId, string? loginHint) ResolveTenant(string? tenantIdOverride)
    {
        if (!string.IsNullOrWhiteSpace(tenantIdOverride))
            return (tenantIdOverride.Trim(), null);

        var input = _console.Prompt(
            new TextPrompt<string>("[cyan]Enter your email or tenant ID[/] [dim](or press Enter to sign in with 'common' tenant)[/]:")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(input))
            return ("common", null);

        var trimmed = input.Trim();
        if (Guid.TryParse(trimmed, out _))
        {
            _console.MarkupLine($"[dim]Tenant: [bold]{Markup.Escape(trimmed)}[/][/]");
            return (trimmed, null);
        }

        // Treat as email — Entra will route to the right tenant via login_hint + select_account.
        return ("common", trimmed);
    }
}
