using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Foundry;

/// <summary>
/// Retrieves an access token for Azure AI Foundry API calls.
/// Outputs the raw token to stdout for piping into other tools.
/// With --json, outputs token + endpoint + model as JSON for apps.
/// </summary>
[Description("Get an Azure AI Foundry access token")]
public class FoundryTokenCommand : Command<FoundryTokenCommand.Settings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;

    public FoundryTokenCommand(IAzureFoundryAuthService authService, AzureFoundryAuthConfig config)
    {
        _authService = authService;
        _config = config;
    }

    public class Settings : FoundrySettings
    {
        [CommandOption("-s|--scope")]
        [Description("Token scope (defaults to cognitive services)")]
        public string? Scope { get; set; }

        [CommandOption("--json")]
        [Description("Output as JSON including token, endpoint, and default model")]
        public bool Json { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!await _authService.IsAuthenticatedAsync())
        {
            AnsiConsole.MarkupLine("[red]Not authenticated with Azure AI Foundry.[/]");
            AnsiConsole.MarkupLine("[dim]Run [bold]pks foundry init[/] to authenticate first.[/]");
            return 1;
        }

        var scope = settings.Scope ?? _config.CognitiveScope;
        var token = await _authService.GetAccessTokenAsync(scope);

        if (string.IsNullOrEmpty(token))
        {
            AnsiConsole.MarkupLine("[red]Failed to obtain access token. Try re-authenticating with [bold]pks foundry init --force[/].[/]");
            return 1;
        }

        if (settings.Json)
        {
            var credentials = await _authService.GetStoredCredentialsAsync();
            var json = JsonSerializer.Serialize(new
            {
                token,
                endpoint = credentials?.SelectedResourceEndpoint ?? string.Empty,
                model = credentials?.DefaultModel ?? string.Empty,
                resource = credentials?.SelectedResourceName ?? string.Empty,
                subscription = credentials?.SelectedSubscriptionName ?? string.Empty,
            }, new JsonSerializerOptions { WriteIndented = false });

            // Gzip + base64 for compact copy-paste into browser apps.
            // Browser decodes with: JSON.parse(pako.ungzip(Uint8Array.from(atob(value), c => c.charCodeAt(0)), {to:'string'}))
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            using var ms = new MemoryStream();
            using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(jsonBytes);
            }
            Console.Write(Convert.ToBase64String(ms.ToArray()));
        }
        else
        {
            // Write raw token to stdout for piping (no markup, no newline formatting)
            Console.Write(token);
        }

        return 0;
    }
}
