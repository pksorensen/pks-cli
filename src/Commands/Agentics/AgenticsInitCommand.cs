using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services.Agentics;
using PKS.Infrastructure.Services.Oidc;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agentics;

/// <summary>
/// One-time login that authenticates the user against agentics.dk via Keycloak's
/// OAuth 2.0 device authorization grant (RFC 8628), and stores the resulting
/// access/refresh tokens at ~/.pks-cli/agentics-auth.json.
///
/// After this runs, `pks agentics task submit` and `pks agentics runner register`
/// authenticate as the user — no per-host runner pre-registration required.
/// </summary>
public class AgenticsInitCommand : AgenticsCommand<AgenticsInitCommand.Settings>
{
    private readonly IAgenticsAuthConfigurationService _authConfig;
    private readonly IAnsiConsole _console;
    private readonly IOidcDiscovery _discovery;
    private readonly IDeviceCodeLogin _deviceLogin;

    public AgenticsInitCommand(
        IAgenticsAuthConfigurationService authConfig,
        IAnsiConsole console,
        IOidcDiscovery discovery,
        IDeviceCodeLogin deviceLogin)
        : base(console)
    {
        _authConfig = authConfig;
        _console = console;
        _discovery = discovery;
        _deviceLogin = deviceLogin;
    }

    public class Settings : AgenticsSettings
    {
        [CommandOption("--server <SERVER>")]
        [Description("Agentics server host (default: agentics.dk)")]
        public string Server { get; set; } = "agentics.dk";

        [CommandOption("--keycloak <URL>")]
        [Description("Keycloak base URL. Default: https://login.<server>, then https://keycloak.<server>, "
                   + "then the server itself — whichever answers discovery first. Needed when the "
                   + "identity provider sits on none of those — self-hosted and local dev.")]
        public string? Keycloak { get; set; }

        [CommandOption("--realm <REALM>")]
        [Description("Keycloak realm (default: agentics)")]
        public string Realm { get; set; } = "agentics";

        [CommandOption("--client-id <ID>")]
        [Description("OAuth client_id (default: agentics-cli)")]
        public string ClientId { get; set; } = "agentics-cli";

        [CommandOption("--no-browser")]
        [Description("Don't try to open a browser; just print the verification URL")]
        public bool NoBrowser { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(context, settings).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(CommandContext _context, Settings settings)
    {
        DisplayBanner("Login");

        OidcLoginResult result = default!;
        await _console.Status().Spinner(Spinner.Known.Dots).StartAsync("Requesting device code...", async ctx =>
        {
            result = await AgenticsSignIn.SignInAsync(
                _discovery,
                _deviceLogin,
                _authConfig,
                settings.Server,
                settings.Keycloak,
                settings.Realm,
                settings.ClientId,
                prompt =>
                {
                    // Spectre renders console writes above the running spinner,
                    // so the panel stays visible while polling continues.
                    _console.WriteLine();
                    _console.Write(new Panel(
                            $"Visit:  [cyan]{Markup.Escape(prompt.BestUri)}[/]\n" +
                            $"Code:   [bold yellow]{Markup.Escape(prompt.UserCode)}[/]")
                        .Header("[bold]Authorize PKS CLI[/]")
                        .BorderStyle(Style.Parse("cyan"))
                        .Padding(1, 1));
                    _console.WriteLine();

                    if (!settings.NoBrowser && prompt.BestUri.Length > 0) TryOpenBrowser(prompt.BestUri);
                    ctx.Status("Waiting for authorization...");
                });
        });

        if (!result.Ok)
        {
            DisplayError($"Login failed: {result.Error ?? "no access token"}");

            return 1;
        }

        _console.WriteLine();
        DisplaySuccess($"Logged in to {settings.Server}.");
        DisplayInfo("Credentials saved to ~/.pks-cli/agentics-auth.json (mode 0600).");
        DisplayInfo("`pks agentics task submit` and `runner register` will now authenticate as you.");
        return 0;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", url);
        }
        catch { /* user can copy/paste from the panel */ }
    }
}
