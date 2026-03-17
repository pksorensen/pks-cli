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
        // Enable debug output if requested
        if (settings.Debug && _jiraService is JiraService svc)
        {
            svc.DebugWriter = msg => _console.MarkupLine(msg);
        }

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
                table.AddRow("Deployment", Markup.Escape(existing.DeploymentType.ToString()));
                if (!string.IsNullOrEmpty(existing.Email))
                    table.AddRow("Email", Markup.Escape(existing.Email));
                if (!string.IsNullOrEmpty(existing.Username))
                    table.AddRow("Username", Markup.Escape(existing.Username));
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

        // API Token flow — select deployment type
        var deploymentChoice = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select your Jira deployment type:[/]")
                .AddChoices(new[]
                {
                    "Jira Cloud (*.atlassian.net)",
                    "Jira Server / Data Center (on-premise)"
                }));

        var deploymentType = deploymentChoice.Contains("Cloud")
            ? JiraDeploymentType.Cloud
            : JiraDeploymentType.Server;

        var baseUrl = _console.Prompt(
            new TextPrompt<string>(deploymentType == JiraDeploymentType.Cloud
                    ? "[cyan]Jira base URL[/] (e.g., https://mycompany.atlassian.net):"
                    : "[cyan]Jira base URL[/] (e.g., https://jira.mycompany.com):")
                .Validate(url => Uri.TryCreate(url, UriKind.Absolute, out _)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Please enter a valid URL.[/]")));

        JiraStoredCredentials credentials;

        if (deploymentType == JiraDeploymentType.Cloud)
        {
            // Cloud flow: email + API token
            var email = _console.Prompt(
                new TextPrompt<string>("[cyan]Email:[/]"));

            var apiToken = _console.Prompt(
                new TextPrompt<string>("[cyan]API Token:[/]")
                    .Secret());

            credentials = new JiraStoredCredentials
            {
                AuthMethod = JiraAuthMethod.ApiToken,
                DeploymentType = JiraDeploymentType.Cloud,
                BaseUrl = baseUrl,
                Email = email,
                ApiToken = apiToken,
                CreatedAt = DateTime.UtcNow,
                LastRefreshedAt = DateTime.UtcNow
            };
        }
        else
        {
            // Server/DC flow: PAT or Username/Password
            var serverAuthChoice = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select Server authentication method:[/]")
                    .AddChoices(new[]
                    {
                        "Personal Access Token (PAT)",
                        "Username / Password"
                    }));

            if (serverAuthChoice.Contains("PAT"))
            {
                var pat = _console.Prompt(
                    new TextPrompt<string>("[cyan]Personal Access Token:[/]")
                        .Secret());

                credentials = new JiraStoredCredentials
                {
                    AuthMethod = JiraAuthMethod.ApiToken,
                    DeploymentType = JiraDeploymentType.Server,
                    BaseUrl = baseUrl,
                    ApiToken = pat,
                    CreatedAt = DateTime.UtcNow,
                    LastRefreshedAt = DateTime.UtcNow
                };
            }
            else
            {
                var username = _console.Prompt(
                    new TextPrompt<string>("[cyan]Username:[/]"));

                var password = _console.Prompt(
                    new TextPrompt<string>("[cyan]Password:[/]")
                        .Secret());

                credentials = new JiraStoredCredentials
                {
                    AuthMethod = JiraAuthMethod.ApiToken,
                    DeploymentType = JiraDeploymentType.Server,
                    BaseUrl = baseUrl,
                    Username = username,
                    ApiToken = password,
                    CreatedAt = DateTime.UtcNow,
                    LastRefreshedAt = DateTime.UtcNow
                };
            }
        }

        var isValid = await _jiraService.ValidateCredentialsAsync(credentials);
        if (!isValid)
        {
            _console.MarkupLine("[red]Authentication failed. Please check your credentials.[/]");
            if (deploymentType == JiraDeploymentType.Cloud)
            {
                _console.MarkupLine("[yellow]Troubleshooting for Jira Cloud:[/]");
                _console.MarkupLine("[dim]- Use an API token from id.atlassian.com (not your account password)[/]");
                _console.MarkupLine("[dim]- Use your Atlassian account email exactly as shown in your account profile[/]");
                _console.MarkupLine("[dim]- Re-paste email/token to avoid hidden leading/trailing spaces[/]");
                _console.MarkupLine("[dim]- Verify this account has Jira product access on the target site[/]");
            }
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
        successTable.AddRow("Deployment", Markup.Escape(credentials.DeploymentType.ToString()));
        if (!string.IsNullOrEmpty(credentials.Email))
            successTable.AddRow("Email", Markup.Escape(credentials.Email));
        if (!string.IsNullOrEmpty(credentials.Username))
            successTable.AddRow("Username", Markup.Escape(credentials.Username));
        successTable.AddRow("Auth Method", credentials.DeploymentType == JiraDeploymentType.Server && string.IsNullOrEmpty(credentials.Username)
            ? "Personal Access Token"
            : "API Token / Basic");

        _console.Write(successTable);

        _console.WriteLine();
        _console.Write(new Panel("[dim]Tip: Use [bold]pks jira browse[/] to browse your Jira projects and issues.[/]")
            .Border(BoxBorder.Rounded));

        return 0;
    }
}
