using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Agentics;
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
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IAgenticsAuthConfigurationService _authConfig;
    private readonly IAnsiConsole _console;

    public AgenticsInitCommand(IAgenticsAuthConfigurationService authConfig, IAnsiConsole console)
        : base(console)
    {
        _authConfig = authConfig;
        _console = console;
    }

    public class Settings : AgenticsSettings
    {
        [CommandOption("--server <SERVER>")]
        [Description("Agentics server host (default: agentics.dk)")]
        public string Server { get; set; } = "agentics.dk";

        [CommandOption("--realm <REALM>")]
        [Description("Keycloak realm (default: agentics)")]
        public string Realm { get; set; } = "agentics";

        [CommandOption("--client-id <ID>")]
        [Description("OAuth client_id (default: pks-cli)")]
        public string ClientId { get; set; } = "pks-cli";

        [CommandOption("--no-browser")]
        [Description("Don't try to open a browser; just print the verification URL")]
        public bool NoBrowser { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(context, settings).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(CommandContext _context, Settings settings)
    {
        DisplayBanner("Login");

        var keycloakBase = ResolveKeycloakBase(settings.Server, settings.Realm);
        var deviceUrl = $"{keycloakBase}/protocol/openid-connect/auth/device";
        var tokenUrl = $"{keycloakBase}/protocol/openid-connect/token";

        // 1. Initiate the device flow.
        DeviceCodeResponse? device = null;
        string? initError = null;
        await _console.Status().Spinner(Spinner.Known.Dots).StartAsync("Requesting device code...", async _ =>
        {
            try
            {
                using var http = new HttpClient();
                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", settings.ClientId),
                    new KeyValuePair<string, string>("scope", "openid offline_access"),
                });
                using var resp = await http.PostAsync(deviceUrl, form);
                if (!resp.IsSuccessStatusCode)
                {
                    initError = $"Server returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}";
                    return;
                }
                device = await resp.Content.ReadFromJsonAsync<DeviceCodeResponse>(JsonOptions);
            }
            catch (Exception ex) { initError = ex.Message; }
        });

        if (initError != null || device == null)
        {
            DisplayError($"Failed to start device login: {initError ?? "no response"}");
            return 1;
        }

        // 2. Show the user the code + verification URL, optionally open browser.
        _console.WriteLine();
        var verificationUri = device.VerificationUriComplete ?? device.VerificationUri ?? "";
        var panel = new Panel(
            $"Visit:  [cyan]{verificationUri}[/]\n" +
            $"Code:   [bold yellow]{device.UserCode}[/]")
            .Header("[bold]Authorize PKS CLI[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 1);
        _console.Write(panel);
        _console.WriteLine();

        if (!settings.NoBrowser && !string.IsNullOrEmpty(verificationUri))
        {
            TryOpenBrowser(verificationUri);
        }

        // 3. Poll for the token.
        var interval = TimeSpan.FromSeconds(Math.Max(5, device.Interval));
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, device.ExpiresIn));
        TokenResponse? token = null;

        await _console.Status().Spinner(Spinner.Known.Dots).StartAsync("Waiting for authorization...", async _ =>
        {
            using var http = new HttpClient();
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(interval);
                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                    new KeyValuePair<string, string>("device_code", device.DeviceCode!),
                    new KeyValuePair<string, string>("client_id", settings.ClientId),
                });
                using var resp = await http.PostAsync(tokenUrl, form);
                var body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    token = JsonSerializer.Deserialize<TokenResponse>(body, JsonOptions);
                    return;
                }

                // RFC 8628 status codes: authorization_pending = keep polling.
                // slow_down = keep polling but add 5 s.
                try
                {
                    var err = JsonSerializer.Deserialize<TokenErrorResponse>(body, JsonOptions);
                    if (err?.Error == "authorization_pending") continue;
                    if (err?.Error == "slow_down") { interval += TimeSpan.FromSeconds(5); continue; }
                    if (err?.Error == "expired_token" || err?.Error == "access_denied")
                    {
                        return; // token stays null → handled below
                    }
                }
                catch
                {
                    // Non-JSON error body — bail.
                    return;
                }
            }
        });

        if (token == null || string.IsNullOrEmpty(token.AccessToken))
        {
            DisplayError("Authorization not completed before the device code expired.");
            return 1;
        }

        // 4. Persist credentials.
        await _authConfig.SaveAsync(new AgenticsAuthCredentials
        {
            Server = settings.Server,
            Realm = settings.Realm,
            ClientId = settings.ClientId,
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            IdToken = token.IdToken,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + token.ExpiresIn,
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

    // ─── DTOs ───────────────────────────────────────────────────────────────

    private class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
        [JsonPropertyName("user_code")] public string? UserCode { get; set; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
        [JsonPropertyName("verification_uri_complete")] public string? VerificationUriComplete { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; } = 5;
    }

    private class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    }

    private class TokenErrorResponse
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }
}
