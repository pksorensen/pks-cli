using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Jira;

/// <summary>
/// Interactive Jira authentication via API Token or OAuth.
/// Prompts for credentials, validates them against Jira, and stores
/// them for use with other Jira commands.
/// </summary>
[Description("Authenticate with Jira")]
public class JiraInitCommand : Command<JiraInitCommand.Settings>
{
    private readonly IJiraService _jiraService;
    private readonly IAnsiConsole _console;

    public JiraInitCommand(IJiraService jiraService, IAnsiConsole console)
    {
        _jiraService = jiraService;
        _console = console;
    }

    public class Settings : JiraSettings
    {
        [CommandOption("--force")]
        [Description("Force re-authentication even if already authenticated")]
        public bool Force { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!settings.Force && await _jiraService.IsAuthenticatedAsync())
        {
            var existing = await _jiraService.GetStoredCredentialsAsync();
            if (existing is not null)
            {
                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[bold green]Jira Authentication[/]");

                table.AddColumn("[bold]Property[/]");
                table.AddColumn("[bold]Value[/]");

                table.AddRow("Base URL", Markup.Escape(existing.BaseUrl));
                table.AddRow("Email", Markup.Escape(existing.Email));
                table.AddRow("Auth Method", Markup.Escape(existing.AuthMethod.ToString()));

                _console.Write(table);
                _console.WriteLine();
                _console.MarkupLine("[dim]Use [bold]--force[/] to re-authenticate.[/]");
            }

            return 0;
        }

        var authMethod = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select authentication method:[/]")
                .AddChoices(new[]
                {
                    "API Token (recommended for Jira Cloud)",
                    "OAuth 2.0"
                }));

        if (authMethod.StartsWith("OAuth"))
        {
            _console.MarkupLine("[yellow]OAuth support coming soon. Please use API Token authentication.[/]");
            return 1;
        }

        // API Token flow
        var baseUrl = _console.Prompt(
            new TextPrompt<string>("[cyan]Jira base URL[/] (e.g., https://mycompany.atlassian.net):")
                .Validate(url => Uri.TryCreate(url, UriKind.Absolute, out _)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Please enter a valid URL.[/]")));

        var email = _console.Prompt(
            new TextPrompt<string>("[cyan]Email:[/]"));

        var apiToken = _console.Prompt(
            new TextPrompt<string>("[cyan]API Token:[/]")
                .Secret());

        var credentials = new JiraStoredCredentials
        {
            AuthMethod = JiraAuthMethod.ApiToken,
            BaseUrl = baseUrl,
            Email = email,
            ApiToken = apiToken,
            CreatedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow
        };

        var isValid = await _jiraService.ValidateCredentialsAsync(credentials);
        if (!isValid)
        {
            _console.MarkupLine("[red]Authentication failed. Please check your credentials.[/]");
            return 1;
        }

        await _jiraService.StoreCredentialsAsync(credentials);

        // Display success
        _console.WriteLine();
        var successTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold green]Jira Authentication Successful[/]");

        successTable.AddColumn("[bold]Property[/]");
        successTable.AddColumn("[bold]Value[/]");

        successTable.AddRow("Base URL", Markup.Escape(baseUrl));
        successTable.AddRow("Email", Markup.Escape(email));
        successTable.AddRow("Auth Method", "API Token");

        _console.Write(successTable);

        _console.WriteLine();
        _console.Write(new Panel("[dim]Tip: Use [bold]pks jira browse[/] to browse your Jira projects and issues.[/]")
            .Border(BoxBorder.Rounded));

        return 0;
    }
}
