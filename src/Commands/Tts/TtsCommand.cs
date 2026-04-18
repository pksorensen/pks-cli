using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Tts;

/// <summary>
/// Generate speech audio from text using Azure AI Foundry TTS.
/// Mirrors the pks image command pattern: positional text arg, optional output path,
/// prints the output file path to stdout for agent piping.
/// </summary>
[Description("Generate speech audio from text using Azure AI Foundry TTS")]
public class TtsCommand : AsyncCommand<TtsCommand.Settings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IAnsiConsole _console;
    private readonly IHttpClientFactory _httpClientFactory;

    public TtsCommand(
        IAzureFoundryAuthService authService,
        AzureFoundryAuthConfig config,
        IAnsiConsole console,
        IHttpClientFactory httpClientFactory)
    {
        _authService = authService;
        _config = config;
        _console = console;
        _httpClientFactory = httpClientFactory;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[text]")]
        [Description("The text to synthesize into speech")]
        public string? Text { get; set; }

        [CommandOption("--text-file")]
        [Description("Path to a text file containing the input (alternative to positional argument)")]
        public string? TextFile { get; set; }

        [CommandOption("--voice|-v")]
        [Description("Voice name: alloy, echo, fable, onyx, nova, shimmer (default: alloy)")]
        public string Voice { get; set; } = "alloy";

        [CommandOption("--model|-m")]
        [Description("TTS model name (default: tts-hd)")]
        public string Model { get; set; } = "tts-hd";

        [CommandOption("--deployment|-d")]
        [Description("Foundry deployment name (default: from stored credentials)")]
        public string? Deployment { get; set; }

        [CommandOption("--output|-o")]
        [Description("Output file path (default: speech-<timestamp>.mp3)")]
        public string? Output { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!await _authService.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated with Azure AI Foundry.[/]");
            _console.MarkupLine("[dim]Run [bold]pks foundry init[/] first.[/]");
            return 1;
        }

        // Resolve input text
        string? text = null;
        if (!string.IsNullOrEmpty(settings.Text))
        {
            text = settings.Text;
        }
        else if (!string.IsNullOrEmpty(settings.TextFile))
        {
            if (!File.Exists(settings.TextFile))
            {
                _console.MarkupLine($"[red]Text file not found:[/] {Markup.Escape(settings.TextFile)}");
                return 1;
            }
            text = await File.ReadAllTextAsync(settings.TextFile);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _console.MarkupLine("[red]Provide text via argument or --text-file.[/]");
            _console.MarkupLine("[dim]Example: pks tts \"Hello world\"[/]");
            _console.MarkupLine("[dim]Example: pks tts --text-file script.txt --voice nova --output speech.mp3[/]");
            return 1;
        }

        // Resolve deployment from stored credentials if not specified
        var creds = await _authService.GetStoredCredentialsAsync();
        if (creds == null || string.IsNullOrEmpty(creds.SelectedResourceEndpoint))
        {
            _console.MarkupLine("[red]No Foundry resource configured.[/]");
            _console.MarkupLine("[dim]Run [bold]pks foundry init[/] to select a resource.[/]");
            return 1;
        }

        var deployment = settings.Deployment
            ?? creds.DefaultModel
            ?? "tts-hd";

        var endpoint = creds.SelectedResourceEndpoint.TrimEnd('/') +
                       $"/openai/deployments/{deployment}/audio/speech?api-version=2025-03-01-preview";

        // Acquire real Azure token
        var token = await _authService.GetAccessTokenAsync(_config.CognitiveScope);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to acquire Azure access token. Try [bold]pks foundry init --force[/].[/]");
            return 1;
        }

        // Resolve output path
        var outputPath = settings.Output
            ?? $"speech-{DateTime.Now:yyyyMMdd-HHmmss}.mp3";
        outputPath = Path.GetFullPath(outputPath);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Call TTS API
        byte[]? audioBytes = null;
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = settings.Model,
                input = text,
                voice = settings.Voice,
            });

            audioBytes = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Generating speech with [bold]{Markup.Escape(deployment)}[/] ({Markup.Escape(settings.Voice)})...", async _ =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    var client = _httpClientFactory.CreateClient("foundry-tts");
                    var response = await client.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync();
                });
        }
        catch (HttpRequestException ex)
        {
            _console.MarkupLine($"[red]API error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        await File.WriteAllBytesAsync(outputPath, audioBytes);

        // Print absolute path to stdout for agent piping (same as pks image)
        Console.WriteLine(outputPath);

        _console.MarkupLine($"[green]Speech saved:[/] {Markup.Escape(outputPath)} [dim]({audioBytes.Length / 1024} KB, voice: {Markup.Escape(settings.Voice)})[/]");

        return 0;
    }
}
