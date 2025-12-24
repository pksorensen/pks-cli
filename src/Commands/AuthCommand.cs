using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;

namespace PKS.Commands;

/// <summary>
/// Command for GitHub authentication management
/// </summary>
[Description("Manage GitHub authentication for PKS CLI")]
public class AuthCommand : Command<AuthCommand.Settings>
{
    private readonly IGitHubAuthenticationService _authService;

    public AuthCommand(IGitHubAuthenticationService authService)
    {
        _authService = authService;
    }

    public class Settings : CommandSettings
    {
        [Description("Authentication action to perform")]
        [CommandArgument(0, "[action]")]
        public string? Action { get; set; }

        [Description("GitHub personal access token (for token command)")]
        [CommandOption("-t|--token")]
        public string? Token { get; set; }

        [Description("OAuth scopes to request (comma-separated)")]
        [CommandOption("-s|--scopes")]
        public string? Scopes { get; set; }

        [Description("Force re-authentication even if already authenticated")]
        [CommandOption("-f|--force")]
        public bool Force { get; set; }

        [Description("Associated user/project identifier")]
        [CommandOption("-u|--user")]
        public string? User { get; set; }

        [Description("Show detailed authentication information")]
        [CommandOption("-v|--verbose")]
        public bool Verbose { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var action = settings.Action?.ToLowerInvariant();

            return action switch
            {
                "login" or "auth" or null => await HandleLoginAsync(settings),
                "logout" or "clear" => await HandleLogoutAsync(settings),
                "status" or "check" => await HandleStatusAsync(settings),
                "token" => await HandleTokenAsync(settings),
                "validate" => await HandleValidateAsync(settings),
                _ => await ShowHelpAsync()
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            return 1;
        }
    }

    private async Task<int> HandleLoginAsync(Settings settings)
    {
        AnsiConsole.MarkupLine("[cyan]PKS CLI GitHub Authentication[/]");
        AnsiConsole.WriteLine();

        // Check if already authenticated (unless force flag is used)
        if (!settings.Force)
        {
            var isAuthenticated = await _authService.IsAuthenticatedAsync(settings.User);
            if (isAuthenticated)
            {
                AnsiConsole.MarkupLine("[green]✓[/] Already authenticated with GitHub");

                if (!AnsiConsole.Confirm("Re-authenticate anyway?"))
                {
                    return 0;
                }
            }
        }

        // Parse scopes
        var scopes = ParseScopes(settings.Scopes);

        AnsiConsole.MarkupLine("[yellow]Starting GitHub device code flow authentication...[/]");
        AnsiConsole.WriteLine();

        // Create progress display
        var progressColumns = new ProgressColumn[]
        {
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn()
        };

        await AnsiConsole.Progress()
            .Columns(progressColumns)
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Authenticating with GitHub[/]", maxValue: 100);

                var progress = new Progress<GitHubAuthProgress>(p =>
                {
                    task.Description = $"[green]{p.StatusMessage}[/]";

                    var percentage = p.CurrentStep switch
                    {
                        GitHubAuthStep.Initializing => 10,
                        GitHubAuthStep.RequestingDeviceCode => 20,
                        GitHubAuthStep.WaitingForUserAuthorization => 30,
                        GitHubAuthStep.PollingForToken => 60,
                        GitHubAuthStep.ValidatingToken => 90,
                        GitHubAuthStep.Complete => 100,
                        GitHubAuthStep.Error => 100,
                        _ => task.Value
                    };

                    task.Value = percentage;

                    // Show user code and verification URL
                    if (p.CurrentStep == GitHubAuthStep.WaitingForUserAuthorization && !string.IsNullOrEmpty(p.UserCode))
                    {
                        ctx.Refresh();
                        AnsiConsole.WriteLine();

                        var panel = new Panel($"""
                            [bold cyan]Please complete authentication in your web browser:[/]
                            
                            [yellow]1. Visit:[/] [link]{p.VerificationUrl}[/]
                            [yellow]2. Enter code:[/] [bold white on blue] {p.UserCode} [/]
                            
                            [dim]Waiting for you to authorize PKS CLI...[/]
                            """)
                            .Header("[blue] GitHub Authentication Required [/]")
                            .Border(BoxBorder.Rounded)
                            .BorderColor(Color.Blue);

                        AnsiConsole.Write(panel);
                        AnsiConsole.WriteLine();
                    }
                });

                var result = await _authService.AuthenticateAsync(scopes, progress);

                if (result.IsAuthenticated)
                {
                    task.Description = "[green]✓ Authentication completed successfully![/]";
                    task.Value = 100;
                }
                else
                {
                    task.Description = $"[red]✗ Authentication failed: {result.ErrorDescription}[/]";
                    task.Value = 100;
                    throw new InvalidOperationException($"Authentication failed: {result.ErrorDescription}");
                }
            });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]✓ Successfully authenticated with GitHub![/]");

        // Show token info if verbose
        if (settings.Verbose)
        {
            var storedToken = await _authService.GetStoredTokenAsync(settings.User);
            if (storedToken != null)
            {
                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Grey)
                    .AddColumn("[yellow]Property[/]")
                    .AddColumn("[cyan]Value[/]");

                table.AddRow("Token Type", "GitHub Personal Access Token");
                table.AddRow("Scopes", string.Join(", ", storedToken.Scopes));
                table.AddRow("Created", storedToken.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
                table.AddRow("Expires", storedToken.ExpiresAt?.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "Never");
                table.AddRow("Last Validated", storedToken.LastValidated.ToString("yyyy-MM-dd HH:mm:ss UTC"));

                AnsiConsole.WriteLine();
                AnsiConsole.Write(table);
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]You can now use GitHub-integrated PKS CLI commands.[/]");

        return 0;
    }

    private async Task<int> HandleLogoutAsync(Settings settings)
    {
        AnsiConsole.MarkupLine("[yellow]Clearing GitHub authentication...[/]");

        var isAuthenticated = await _authService.IsAuthenticatedAsync(settings.User);
        if (!isAuthenticated)
        {
            AnsiConsole.MarkupLine("[yellow]No GitHub authentication found.[/]");
            return 0;
        }

        var cleared = await _authService.ClearStoredTokenAsync(settings.User);

        if (cleared)
        {
            AnsiConsole.MarkupLine("[green]✓ GitHub authentication cleared successfully.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[red]✗ Failed to clear GitHub authentication.[/]");
            return 1;
        }

        return 0;
    }

    private async Task<int> HandleStatusAsync(Settings settings)
    {
        AnsiConsole.MarkupLine("[cyan]GitHub Authentication Status[/]");
        AnsiConsole.WriteLine();

        var isAuthenticated = await _authService.IsAuthenticatedAsync(settings.User);

        if (!isAuthenticated)
        {
            AnsiConsole.MarkupLine("[red]✗ Not authenticated with GitHub[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Run 'pks auth login' to authenticate with GitHub.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[green]✓ Authenticated with GitHub[/]");

        if (settings.Verbose)
        {
            var storedToken = await _authService.GetStoredTokenAsync(settings.User);
            if (storedToken != null)
            {
                AnsiConsole.WriteLine();

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Green)
                    .AddColumn("[yellow]Property[/]")
                    .AddColumn("[cyan]Value[/]");

                table.AddRow("Status", "[green]Active[/]");
                table.AddRow("Scopes", string.Join(", ", storedToken.Scopes));
                table.AddRow("Created", storedToken.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
                table.AddRow("Expires", storedToken.ExpiresAt?.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "Never");
                table.AddRow("Last Validated", storedToken.LastValidated.ToString("yyyy-MM-dd HH:mm:ss UTC"));
                table.AddRow("Valid", storedToken.IsValid ? "[green]Yes[/]" : "[red]No[/]");

                AnsiConsole.Write(table);
            }
        }

        return 0;
    }

    private async Task<int> HandleTokenAsync(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Token))
        {
            AnsiConsole.MarkupLine("[red]Error: Token is required. Use --token option.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[yellow]Validating and storing GitHub token...[/]");

        // Validate the token
        var validation = await _authService.ValidateTokenAsync(settings.Token);

        if (!validation.IsValid)
        {
            AnsiConsole.MarkupLine($"[red]✗ Invalid token: {validation.ErrorMessage}[/]");
            return 1;
        }

        // Store the token
        var storedToken = new GitHubStoredToken
        {
            AccessToken = settings.Token,
            Scopes = validation.Scopes,
            CreatedAt = DateTime.UtcNow,
            IsValid = true,
            LastValidated = DateTime.UtcNow,
            AssociatedUser = settings.User
        };

        var stored = await _authService.StoreTokenAsync(storedToken, settings.User);

        if (stored)
        {
            AnsiConsole.MarkupLine("[green]✓ Token validated and stored successfully![/]");

            if (settings.Verbose)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[dim]Token scopes: {string.Join(", ", validation.Scopes)}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[red]✗ Failed to store token.[/]");
            return 1;
        }

        return 0;
    }

    private async Task<int> HandleValidateAsync(Settings settings)
    {
        AnsiConsole.MarkupLine("[yellow]Validating GitHub authentication...[/]");

        var storedToken = await _authService.GetStoredTokenAsync(settings.User);

        if (storedToken == null)
        {
            AnsiConsole.MarkupLine("[red]✗ No stored token found.[/]");
            return 1;
        }

        var validation = await _authService.ValidateTokenAsync(storedToken.AccessToken);

        if (validation.IsValid)
        {
            AnsiConsole.MarkupLine("[green]✓ Token is valid[/]");

            if (settings.Verbose)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[dim]Validated at: {validation.ValidatedAt:yyyy-MM-dd HH:mm:ss UTC}[/]");
                AnsiConsole.MarkupLine($"[dim]Token scopes: {string.Join(", ", validation.Scopes)}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ Token validation failed: {validation.ErrorMessage}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Run 'pks auth login' to re-authenticate.[/]");
            return 1;
        }

        return 0;
    }

    private async Task<int> ShowHelpAsync()
    {
        var panel = new Panel("""
            [bold cyan]PKS CLI GitHub Authentication Commands[/]
            
            [yellow]pks auth login[/]     - Authenticate with GitHub using device code flow
            [yellow]pks auth logout[/]    - Clear stored GitHub authentication
            [yellow]pks auth status[/]    - Check current authentication status
            [yellow]pks auth token[/]     - Store a GitHub personal access token
            [yellow]pks auth validate[/]  - Validate current authentication
            
            [bold]Options:[/]
            [dim]--token[/]       Personal access token (for token command)
            [dim]--scopes[/]      OAuth scopes (comma-separated)
            [dim]--force[/]       Force re-authentication
            [dim]--user[/]        Associate with specific user/project
            [dim]--verbose[/]     Show detailed information
            
            [bold]Examples:[/]
            [dim]pks auth login[/]                           # Interactive authentication
            [dim]pks auth login --scopes repo,user:email[/]  # Request specific scopes
            [dim]pks auth token --token ghp_xxxxx[/]         # Store existing token
            [dim]pks auth status --verbose[/]                # Show detailed status
            """)
            .Header("[blue] GitHub Authentication Help [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue);

        AnsiConsole.Write(panel);
        return 0;
    }

    private static string[] ParseScopes(string? scopesString)
    {
        if (string.IsNullOrEmpty(scopesString))
        {
            return new[] { "repo", "user:email", "write:packages" }; // Default scopes
        }

        return scopesString.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToArray();
    }
}