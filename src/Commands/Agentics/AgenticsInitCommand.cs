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
        [Description("Keycloak base URL. Default: https://keycloak.<server>. Needed when the "
                   + "identity provider does not sit on that subdomain — self-hosted and local dev.")]
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

        var keycloakBase = ResolveKeycloakBase(settings.Keycloak ?? settings.Server, settings.Realm);

        // Ask the issuer where its endpoints are before assuming Keycloak's
        // layout. The convention below is right for our realm and wrong for
        // anything else, and a self-hosted server that answers discovery should
        // not need `--keycloak` to be a Keycloak at all.
        var endpoints = await _discovery.EndpointsAsync(keycloakBase)
                        ?? KeycloakConvention(keycloakBase);

        OidcLoginResult result = default!;
        await _console.Status().Spinner(Spinner.Known.Dots).StartAsync("Requesting device code...", async ctx =>
        {
            result = await _deviceLogin.LoginAsync(new DeviceLoginRequest(
                endpoints,
                settings.ClientId,
                "openid offline_access",
                // No `resource`: this credential is the CLI's general-purpose
                // login, not a token bound to one API.
                null,
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
                }));
        });

        if (!result.Ok)
        {
            DisplayError($"Login failed: {result.Error ?? "no access token"}");

            return 1;
        }

        // 4. Persist credentials.
        await _authConfig.SaveAsync(new AgenticsAuthCredentials
        {
            Server = settings.Server,
            // Recorded so refresh does not have to re-derive it. The
            // keycloak.<server> convention holds for agentics.dk and nowhere
            // else; a self-hosted instance that passed --keycloak would silently
            // lose its refresh path without this.
            Issuer = endpoints.Issuer,
            Realm = settings.Realm,
            ClientId = settings.ClientId,
            AccessToken = result.AccessToken!,
            RefreshToken = result.RefreshToken,
            IdToken = result.IdToken,
            ExpiresAt = result.ExpiresAtUnix,
        });

        _console.WriteLine();
        DisplaySuccess($"Logged in to {settings.Server}.");
        DisplayInfo("Credentials saved to ~/.pks-cli/agentics-auth.json (mode 0600).");
        DisplayInfo("`pks agentics task submit` and `runner register` will now authenticate as you.");
        return 0;
    }

    private static string ResolveKeycloakBase(string serverHost, string realm)
    {
        // Convention: Keycloak lives at https://keycloak.<server>/realms/<realm>.
        var host = serverHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   serverHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? serverHost
            : $"https://keycloak.{serverHost}";
        return $"{host.TrimEnd('/')}/realms/{realm}";
    }

    /// What Keycloak's paths are, for an issuer that serves no discovery
    /// document. Our own dev realm has answered `/.well-known/openid-configuration`
    /// all along, so this is the fallback for someone else's install.
    private static OidcEndpoints KeycloakConvention(string issuer) => new(
        issuer,
        $"{issuer}/protocol/openid-connect/token",
        $"{issuer}/protocol/openid-connect/auth/device",
        $"{issuer}/protocol/openid-connect/auth");

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
