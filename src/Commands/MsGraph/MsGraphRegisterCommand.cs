using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.MsGraph;

[Description("Register and authenticate with Microsoft Graph via device code flow")]
public class MsGraphRegisterCommand : Command<MsGraphRegisterCommand.Settings>
{
    private readonly IMsGraphAuthenticationService _authService;
    private readonly IAnsiConsole _console;

    public MsGraphRegisterCommand(IMsGraphAuthenticationService authService, IAnsiConsole console)
    {
        _authService = authService;
        _console = console;
    }

    public class Settings : MsGraphSettings
    {
        [CommandOption("--client-id <CLIENT_ID>")]
        [Description("Azure AD app client ID")]
        public string? ClientId { get; set; }

        [CommandOption("--tenant-id <TENANT_ID>")]
        [Description("Azure AD tenant (default: common)")]
        public string? TenantId { get; set; }

        [CommandOption("--force")]
        [Description("Re-register even if already authenticated")]
        public bool Force { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!settings.Force && await _authService.IsAuthenticatedAsync())
        {
            var token = await _authService.GetStoredTokenAsync();
            if (token is not null)
            {
                _console.MarkupLine("[green]Microsoft Graph already authenticated.[/]");
                _console.MarkupLine($"  User:    [dim]{token.DisplayName}[/]");
                _console.MarkupLine($"  UPN:     [dim]{token.UserPrincipalName}[/]");
                _console.MarkupLine($"  Token:   [dim]{MaskToken(token.AccessToken ?? "")}[/]");
                _console.MarkupLine($"  Expires: [dim]{token.ExpiresAt:yyyy-MM-dd HH:mm}[/]");
                _console.WriteLine();
                _console.MarkupLine("[dim]Use [bold]--force[/] to re-authenticate.[/]");
                return 0;
            }
        }

        var clientId = settings.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            _console.Write(new Panel(
                "[dim]To use Microsoft Graph, you need an Azure AD app registration:\n\n" +
                "1. Go to https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps\n" +
                "2. Click \"New registration\"\n" +
                "3. Name: \"PKS CLI\" (or any name)\n" +
                "4. Supported account types: Choose based on your needs\n" +
                "5. Redirect URI: Leave blank (we use device code flow)\n" +
                "6. After creating, copy the Application (client) ID\n" +
                "7. Under Authentication, enable \"Allow public client flows\"[/]")
                .Header("[bold cyan]Azure AD App Registration[/]")
                .Border(BoxBorder.Rounded));
            _console.WriteLine();

            clientId = _console.Ask<string>("[cyan]Application (client) ID:[/]");
        }

        var tenantId = settings.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = _console.Prompt(
                new TextPrompt<string>("[cyan]Tenant ID:[/]")
                    .DefaultValue("common"));
        }

        await _authService.StoreConfigAsync(clientId, tenantId);

        if (settings.Verbose)
            _console.MarkupLine($"[dim]Config stored: clientId={clientId}, tenantId={tenantId}[/]");

        var scopes = new[] { "https://graph.microsoft.com/User.Read", "https://graph.microsoft.com/Mail.Read", "https://graph.microsoft.com/Mail.ReadBasic", "offline_access" };

        // Step 1: Get device code (separate from polling so we can display it)
        MsGraphDeviceCodeResponse deviceCode;
        try
        {
            deviceCode = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Requesting device code...",
                    async _ => await _authService.InitiateDeviceCodeFlowAsync(scopes));
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Failed to get device code: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        // Step 2: Display the code prominently
        _console.WriteLine();
        _console.Write(new Panel(
            $"[bold yellow]{Markup.Escape(deviceCode.UserCode)}[/]\n\n" +
            $"[dim]Open[/] [cyan underline]{Markup.Escape(deviceCode.VerificationUri ?? "https://microsoft.com/devicelogin")}[/]\n" +
            $"[dim]and enter the code above to sign in.[/]")
            .Header("[bold cyan]Device Code[/]")
            .Border(BoxBorder.Double)
            .Padding(2, 1));
        _console.WriteLine();

        // Step 3: Poll for authentication with spinner
        MsGraphDeviceAuthStatus? authResult = null;
        try
        {
            authResult = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Waiting for authorization...", async ctx =>
                {
                    var pollingDelay = TimeSpan.FromSeconds(Math.Max(deviceCode.Interval, 5));
                    var expiresAt = DateTime.UtcNow.AddSeconds(deviceCode.ExpiresIn);
                    var maxAttempts = deviceCode.ExpiresIn / deviceCode.Interval;

                    for (int attempt = 0; attempt < maxAttempts && DateTime.UtcNow < expiresAt; attempt++)
                    {
                        var status = await _authService.PollForAuthenticationAsync(deviceCode.DeviceCode);

                        if (status.IsAuthenticated)
                        {
                            ctx.Status("Validating token...");
                            var valid = await _authService.ValidateTokenAsync(status.AccessToken!);
                            if (valid)
                            {
                                var storedTok = new MsGraphStoredToken
                                {
                                    AccessToken = status.AccessToken!,
                                    RefreshToken = status.RefreshToken,
                                    Scopes = status.Scopes,
                                    ClientId = clientId,
                                    TenantId = tenantId,
                                    CreatedAt = DateTime.UtcNow,
                                    ExpiresAt = status.ExpiresAt,
                                    IsValid = true,
                                    LastValidated = DateTime.UtcNow
                                };
                                await _authService.StoreTokenAsync(storedTok);
                                return status;
                            }
                        }

                        if (status.Error == "authorization_pending")
                        {
                            var remaining = expiresAt - DateTime.UtcNow;
                            ctx.Status($"Waiting for authorization... Time remaining: {remaining:mm\\:ss}");
                            await Task.Delay(pollingDelay);
                            continue;
                        }

                        if (status.Error == "slow_down")
                        {
                            pollingDelay = pollingDelay.Add(TimeSpan.FromSeconds(5));
                            await Task.Delay(pollingDelay);
                            continue;
                        }

                        // Terminal error
                        return status;
                    }

                    return new MsGraphDeviceAuthStatus
                    {
                        IsAuthenticated = false,
                        Error = "timeout",
                        ErrorDescription = "Authentication timed out",
                        CheckedAt = DateTime.UtcNow
                    };
                });
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Authentication failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (authResult is null || !authResult.IsAuthenticated)
        {
            _console.MarkupLine("[red]Authentication failed.[/]");
            if (!string.IsNullOrEmpty(authResult?.ErrorDescription))
                _console.MarkupLine($"[dim]{Markup.Escape(authResult.ErrorDescription)}[/]");
            return 1;
        }

        _console.WriteLine();
        _console.MarkupLine("[green bold]Successfully authenticated with Microsoft Graph![/]");
        _console.WriteLine();

        // Fetch stored token to display user info
        var storedToken = await _authService.GetStoredTokenAsync();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Property[/]");
        table.AddColumn("[bold]Value[/]");
        table.AddRow("Display Name", Markup.Escape(storedToken?.DisplayName ?? "N/A"));
        table.AddRow("User Principal Name", Markup.Escape(storedToken?.UserPrincipalName ?? "N/A"));
        table.AddRow("Expires At", authResult.ExpiresAt?.ToString("yyyy-MM-dd HH:mm") ?? "N/A");
        _console.Write(table);

        return 0;
    }

    private static string MaskToken(string token)
    {
        if (token.Length <= 4) return "****";
        return token[..4] + new string('*', Math.Min(token.Length - 4, 8));
    }
}
