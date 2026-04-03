using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Image;

[Description("Generate an image from a text prompt using Google AI")]
public class ImageCommand : Command<ImageCommand.Settings>
{
    private readonly IGoogleAiService _google;
    private readonly IAnsiConsole _console;

    public ImageCommand(IGoogleAiService google, IAnsiConsole console)
    {
        _google = google;
        _console = console;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[prompt]")]
        [Description("The image generation prompt")]
        public string? Prompt { get; set; }

        [CommandOption("--prompt-file")]
        [Description("Path to a text file containing the prompt")]
        public string? PromptFile { get; set; }

        [CommandOption("--model|-m")]
        [Description("Model to use (default: gemini-3.1-flash-image-preview)")]
        public string Model { get; set; } = "gemini-3.1-flash-image-preview";

        [CommandOption("--output|-o")]
        [Description("Output file path (default: ./image-<timestamp>.jpg)")]
        public string? Output { get; set; }

        [CommandOption("--aspect-ratio")]
        [Description("Aspect ratio: 1:1, 3:4, 4:3, 9:16, 16:9, auto (default: auto)")]
        public string AspectRatio { get; set; } = "auto";

        [CommandOption("--resolution")]
        [Description("Output resolution, e.g. 512, 1024, 2048, 4096 (default: model decides)")]
        public string? Resolution { get; set; }

        [CommandOption("--input|-i")]
        [Description("Input image to augment or edit (path to jpg/png). When set, prompt becomes an editing instruction.")]
        public string? InputImage { get; set; }

        [CommandOption("--list-models|-l")]
        [Description("List available image generation models and exit")]
        public bool ListModels { get; set; }

        [CommandOption("--provider")]
        [Description("Image generation provider (default: google)")]
        public string Provider { get; set; } = "google";
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (settings.ListModels)
            return await ListModelsAsync();

        // Resolve prompt
        string? prompt = null;

        if (!string.IsNullOrEmpty(settings.Prompt))
        {
            prompt = settings.Prompt;
        }
        else if (!string.IsNullOrEmpty(settings.PromptFile))
        {
            if (!File.Exists(settings.PromptFile))
            {
                _console.MarkupLine($"[red]Prompt file not found:[/] {Markup.Escape(settings.PromptFile)}");
                return 1;
            }

            prompt = await File.ReadAllTextAsync(settings.PromptFile);
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            _console.MarkupLine("[red]Provide a prompt via argument or --prompt-file.[/]");
            _console.MarkupLine("[dim]Example: pks image \"a dark editorial photograph\"[/]");
            _console.MarkupLine("[dim]Example: pks image --prompt-file prompt.txt[/]");
            _console.MarkupLine("[dim]Example: pks image --input bg.jpg \"Add the title 'My Book' in white serif at the top\"[/]");
            return 1;
        }

        // Validate input image if provided
        if (!string.IsNullOrEmpty(settings.InputImage) && !File.Exists(settings.InputImage))
        {
            _console.MarkupLine($"[red]Input image not found:[/] {Markup.Escape(settings.InputImage)}");
            return 1;
        }

        if (!await _google.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]No Google AI API key registered.[/]");
            _console.MarkupLine("[dim]Run [bold]pks google init[/] first.[/]");
            return 1;
        }

        // Resolve output path
        var outputPath = settings.Output
            ?? $"image-{DateTime.Now:yyyyMMdd-HHmmss}.jpg";
        outputPath = Path.GetFullPath(outputPath);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        byte[]? imageBytes = null;

        try
        {
            var spinnerLabel = settings.InputImage != null
                ? $"Augmenting image with [bold]{Markup.Escape(settings.Model)}[/]..."
                : $"Generating image with [bold]{Markup.Escape(settings.Model)}[/]...";

            imageBytes = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(spinnerLabel, async _ =>
                    await _google.GenerateImageAsync(prompt, settings.Model, settings.AspectRatio, settings.Resolution, settings.InputImage));
        }
        catch (HttpRequestException ex)
        {
            _console.MarkupLine($"[red]API error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            _console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        await File.WriteAllBytesAsync(outputPath, imageBytes);

        // Save prompt alongside the image so we always know what was asked for
        var promptPath = Path.ChangeExtension(outputPath, ".prompt");
        await File.WriteAllTextAsync(promptPath, prompt);

        // Print absolute path to stdout for agent piping
        Console.WriteLine(outputPath);

        _console.MarkupLine($"[green]Image saved:[/] {Markup.Escape(outputPath)} [dim]({imageBytes.Length / 1024} KB)[/]");

        return 0;
    }

    private async Task<int> ListModelsAsync()
    {
        List<GoogleAiModel> models;

        if (await _google.IsAuthenticatedAsync())
        {
            models = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching available models...", async _ =>
                    await _google.ListImageModelsAsync());
        }
        else
        {
            models = await _google.ListImageModelsAsync();
            _console.MarkupLine("[dim](Showing known models — run [bold]pks google init[/] for live listing)[/]");
            _console.WriteLine();
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Google AI — Image Generation Models[/]");

        table.AddColumn("[bold]Model[/]");
        table.AddColumn("[bold]Display Name[/]");
        table.AddColumn("[bold]Description[/]");

        foreach (var m in models)
            table.AddRow(
                Markup.Escape(m.Name),
                Markup.Escape(m.DisplayName),
                Markup.Escape(m.Description));

        _console.Write(table);
        _console.WriteLine();
        _console.MarkupLine("[dim]Use with: [bold]pks image --model <model-name> \"your prompt\"[/][/]");

        return 0;
    }
}
