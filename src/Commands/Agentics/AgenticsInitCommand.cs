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

        var candidates = ResolveKeycloakBases(settings.Keycloak, settings.Server, settings.Realm);

        // Ask each candidate where its endpoints are before assuming Keycloak's
        // layout. Discovery is also how the right host is picked: every Keycloak
        // serves it, so the first candidate that answers is the identity
        // provider and the others are subdomains that do not exist.
        OidcEndpoints? endpoints = null;
        foreach (var candidate in candidates)
        {
            endpoints = await _discovery.EndpointsAsync(candidate);
            if (endpoints is not null) break;
        }
        // Nothing answered. Guess the paths on the first candidate so the error
        // names the host we would have used, not the last one we tried.
        endpoints ??= KeycloakConvention(candidates[0]);

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
            // Recorded so refresh does not have to re-derive it — and it is the
            // discovered issuer, not a guessed host. Without this, refresh falls
            // back to the login.<server> convention, which holds for agentics.dk
            // and nowhere else; a self-hosted instance that passed --keycloak
            // would silently lose its refresh path.
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

    /// Where the realm might live, best guess first.
    ///
    /// An explicit --keycloak is the only answer; otherwise we probe, because
    /// there is no one convention. Ours is `login.agentics.dk` — `keycloak.`
    /// never resolved, which surfaced as a TLS handshake error rather than a
    /// 404 and read like a broken server instead of a wrong hostname. It stays
    /// in the list for installs that do use it, and the bare host is last for
    /// a server that fronts its own identity provider.
    private static string[] ResolveKeycloakBases(string? explicitUrl, string serverHost, string realm)
    {
        static bool IsUrl(string value)
            => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        string Realm(string host) => $"{host.TrimEnd('/')}/realms/{realm}";

        if (!string.IsNullOrWhiteSpace(explicitUrl)) return [Realm(explicitUrl)];
        if (IsUrl(serverHost)) return [Realm(serverHost)];

        return
        [
            Realm($"https://login.{serverHost}"),
            Realm($"https://keycloak.{serverHost}"),
            Realm($"https://{serverHost}"),
        ];
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
