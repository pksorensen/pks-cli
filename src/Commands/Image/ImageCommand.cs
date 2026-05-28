using System.ComponentModel;
using PKS.Infrastructure.Services.Images;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Image;

[Description("Generate an image from a text prompt. Provider is auto-resolved from --model (google, foundry).")]
public class ImageCommand : Command<ImageCommand.Settings>
{
    private readonly IEnumerable<IImageProvider> _providers;
    private readonly IAnsiConsole _console;

    public ImageCommand(IEnumerable<IImageProvider> providers, IAnsiConsole console)
    {
        _providers = providers;
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
        [Description("Model to use (default: gemini-3.1-flash-image-preview). Use gpt-image-2 for Azure Foundry.")]
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
        [Description("Force a specific image provider (google, foundry). Default: auto-resolve from --model.")]
        public string? Provider { get; set; }
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
            _console.MarkupLine("[dim]Example: pks image --model gpt-image-2 \"a red fox in autumn forest\"[/]");
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

        var provider = await ResolveProviderAsync(settings);
        if (provider == null)
            return 1;

        // Resolve output path
        var outputPath = settings.Output
            ?? $"image-{DateTime.Now:yyyyMMdd-HHmmss}.jpg";
        outputPath = Path.GetFullPath(outputPath);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        byte[]? imageBytes = null;
        var request = new ImageGenerationRequest(
            prompt,
            settings.Model,
            settings.AspectRatio,
            settings.Resolution,
            settings.InputImage);

        try
        {
            var spinnerLabel = settings.InputImage != null
                ? $"Augmenting image with [bold]{Markup.Escape(settings.Model)}[/] via [bold]{provider.Name}[/]..."
                : $"Generating image with [bold]{Markup.Escape(settings.Model)}[/] via [bold]{provider.Name}[/]...";

            imageBytes = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(spinnerLabel, async _ => await provider.GenerateAsync(request));
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

    private async Task<IImageProvider?> ResolveProviderAsync(Settings settings)
    {
        // Explicit --provider wins.
        if (!string.IsNullOrWhiteSpace(settings.Provider))
        {
            var forced = _providers.FirstOrDefault(p =>
                string.Equals(p.Name, settings.Provider, StringComparison.OrdinalIgnoreCase));

            if (forced == null)
            {
                _console.MarkupLine($"[red]Unknown provider:[/] {Markup.Escape(settings.Provider)}");
                _console.MarkupLine($"[dim]Available: {string.Join(", ", _providers.Select(p => p.Name))}[/]");
                return null;
            }

            if (!await forced.IsAuthenticatedAsync())
            {
                _console.MarkupLine($"[red]Provider '{forced.Name}' is not authenticated.[/]");
                _console.MarkupLine($"[dim]{Markup.Escape(forced.AuthHint)}[/]");
                return null;
            }

            return forced;
        }

        // Auto-resolve: first authenticated provider that claims the model.
        IImageProvider? unauthMatch = null;

        foreach (var p in _providers)
        {
            if (!await p.CanServeModelAsync(settings.Model))
                continue;

            if (await p.IsAuthenticatedAsync())
                return p;

            unauthMatch ??= p;
        }

        if (unauthMatch != null)
        {
            _console.MarkupLine($"[red]Model '{Markup.Escape(settings.Model)}' is served by '{unauthMatch.Name}', which is not authenticated.[/]");
            _console.MarkupLine($"[dim]{Markup.Escape(unauthMatch.AuthHint)}[/]");
            return null;
        }

        _console.MarkupLine($"[red]No image provider can serve model '{Markup.Escape(settings.Model)}'.[/]");
        var authed = new List<string>();
        foreach (var p in _providers)
            if (await p.IsAuthenticatedAsync())
                authed.Add(p.Name);

        _console.MarkupLine($"[dim]Authenticated providers: {(authed.Count == 0 ? "(none)" : string.Join(", ", authed))}[/]");
        _console.MarkupLine("[dim]Run [bold]pks image --list-models[/] to see what's available.[/]");
        return null;
    }

    private async Task<int> ListModelsAsync()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Image Generation Models[/]");

        table.AddColumn("[bold]Provider[/]");
        table.AddColumn("[bold]Model[/]");
        table.AddColumn("[bold]Display Name[/]");
        table.AddColumn("[bold]Description[/]");

        var anyAuthed = false;

        foreach (var provider in _providers)
        {
            var authed = await provider.IsAuthenticatedAsync();
            if (!authed)
                continue;

            anyAuthed = true;
            IReadOnlyList<ImageModelInfo> models;
            try
            {
                models = await _console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"Fetching {provider.Name} models...", async _ => await provider.ListModelsAsync());
            }
            catch
            {
                continue;
            }

            foreach (var m in models)
                table.AddRow(
                    Markup.Escape(provider.Name),
                    Markup.Escape(m.Name),
                    Markup.Escape(m.DisplayName),
                    Markup.Escape(m.Description));
        }

        if (!anyAuthed)
        {
            _console.MarkupLine("[yellow]No image providers are authenticated.[/]");
            foreach (var p in _providers)
                _console.MarkupLine($"  [dim]{Markup.Escape(p.Name)}: {Markup.Escape(p.AuthHint)}[/]");
            return 1;
        }

        _console.Write(table);
        _console.WriteLine();
        _console.MarkupLine("[dim]Use with: [bold]pks image --model <model-name> \"your prompt\"[/][/]");

        return 0;
    }
}
