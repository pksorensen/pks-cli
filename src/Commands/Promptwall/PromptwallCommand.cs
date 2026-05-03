using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Promptwall;

[Description("Render a recent Claude prompt as a social-media image")]
public class PromptwallCommand : AsyncCommand<PromptwallCommand.Settings>
{
    private readonly IGoogleAiService _google;
    private readonly IAnsiConsole _console;

    public PromptwallCommand(IGoogleAiService google, IAnsiConsole console)
    {
        _google = google;
        _console = console;
    }

    public class Settings : CommandSettings
    {
        [CommandOption("-p|--project")]
        [Description("Path to the project directory to analyse (default: current directory)")]
        public string? ProjectPath { get; set; }

        [CommandOption("--all-projects")]
        [Description("Pick from prompts across every Claude project (default: current project only)")]
        public bool AllProjects { get; set; }

        [CommandOption("--pick-project")]
        [Description("Show a project picker (default: auto-pick when current directory has sessions)")]
        public bool PickProject { get; set; }

        [CommandOption("--count")]
        [Description("How many recent prompts to show in the picker (default: 10)")]
        [DefaultValue(10)]
        public int Count { get; set; } = 10;

        [CommandOption("--include-reply")]
        [Description("Skip the second prompt and always include Claude's reply")]
        public bool IncludeReply { get; set; }

        [CommandOption("-o|--output")]
        [Description("Output directory for the generated image(s) (default: current directory)")]
        public string? Output { get; set; }

        [CommandOption("-m|--model")]
        [Description("Image model (default: gemini-3.1-flash-image-preview)")]
        public string Model { get; set; } = "gemini-3.1-flash-image-preview";

        [CommandOption("--aspect-ratio")]
        [Description("Aspect ratio: 1:1, 3:4, 4:3, 9:16, 16:9 (default: 1:1)")]
        public string AspectRatio { get; set; } = "1:1";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // ── 1. discover transcripts ──────────────────────────────────────────
        List<string> jsonlFiles;
        string scope;

        if (settings.AllProjects || settings.ProjectPath is not null)
        {
            // Existing explicit paths — picker not involved.
            jsonlFiles = DiscoverSessionFiles(settings, out scope);
        }
        else
        {
            // Auto-resolve from cwd unless --pick-project forces the picker.
            jsonlFiles = settings.PickProject
                ? new List<string>()
                : DiscoverSessionFiles(settings, out _);

            if (jsonlFiles.Count == 0)
            {
                var claudeRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude", "projects");
                var projects = PromptwallTranscript.DiscoverProjects(claudeRoot);

                if (projects.Count == 0)
                {
                    var fallbackScope = settings.ProjectPath ?? Directory.GetCurrentDirectory();
                    _console.MarkupLine($"[yellow]No Claude session files found for:[/] [dim]{Markup.Escape(fallbackScope)}[/]");
                    _console.MarkupLine("[dim]Run Claude Code in this project first, then re-run this command.[/]");
                    return 1;
                }

                var picked = _console.Prompt(
                    new SelectionPrompt<PromptwallTranscript.ProjectInfo>()
                        .Title("[bold cyan]Pick a project[/]")
                        .PageSize(Math.Min(20, Math.Max(5, projects.Count)))
                        .UseConverter(p =>
                            $"{Markup.Escape(p.Cwd)}  [dim]({p.SessionCount} sessions, {Humanize(p.LastActivity)})[/]")
                        .AddChoices(projects));

                jsonlFiles = Directory.GetFiles(picked.Dir, "*.jsonl", SearchOption.TopDirectoryOnly).ToList();
                scope = picked.Cwd;
            }
            else
            {
                scope = settings.ProjectPath ?? Directory.GetCurrentDirectory();
            }
        }

        if (jsonlFiles.Count == 0)
        {
            _console.MarkupLine($"[yellow]No Claude session files found for:[/] [dim]{Markup.Escape(scope)}[/]");
            _console.MarkupLine("[dim]Run Claude Code in this project first, then re-run this command.[/]");
            return 1;
        }

        // ── 2. parse prompts ─────────────────────────────────────────────────
        var allCandidates = new List<PromptwallTranscript.PromptCandidate>();
        await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Scanning {jsonlFiles.Count} session files…", async _ =>
            {
                foreach (var file in jsonlFiles)
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(file);
                        allCandidates.AddRange(PromptwallTranscript.ParsePrompts(content, file));
                    }
                    catch { /* skip corrupt files */ }
                }
            });

        if (allCandidates.Count == 0)
        {
            _console.MarkupLine("[yellow]No user prompts found in this scope.[/]");
            return 1;
        }

        var topN = allCandidates
            .OrderByDescending(c => c.Timestamp)
            .Take(Math.Max(1, settings.Count))
            .ToList();

        // ── 3. picker UI ─────────────────────────────────────────────────────
        var pick = _console.Prompt(
            new SelectionPrompt<PromptwallTranscript.PromptCandidate>()
                .Title("[bold cyan]Pick a prompt to render[/]")
                .PageSize(Math.Min(20, Math.Max(5, topN.Count)))
                .UseConverter(c =>
                    $"[dim]{c.Timestamp.ToLocalTime():MM-dd HH:mm}[/]  {Markup.Escape(TruncatePreview(c.Text, 80))}")
                .AddChoices(topN));

        // ── 4. choose what to include ────────────────────────────────────────
        string? reply = null;
        try { reply = PromptwallTranscript.ExtractReply(await File.ReadAllTextAsync(pick.File), pick.Uuid); }
        catch { /* leave null */ }

        var promptText = pick.Text;
        bool includeReply = settings.IncludeReply && reply is not null;

        if (!settings.IncludeReply)
        {
            var choices = new List<string> { "Prompt only" };
            if (reply is not null) choices.Add("Prompt + Claude's reply");
            choices.Add("Edit prompt text…");
            choices.Add("Cancel");

            var mode = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]What should be on the image?[/]")
                    .AddChoices(choices));

            switch (mode)
            {
                case "Cancel":
                    _console.MarkupLine("[dim]Cancelled.[/]");
                    return 0;
                case "Prompt + Claude's reply":
                    includeReply = true;
                    break;
                case "Edit prompt text…":
                    promptText = _console.Prompt(
                        new TextPrompt<string>("Prompt:").DefaultValue(promptText));
                    break;
            }
        }

        // ── 5. build image-generation specs ──────────────────────────────────
        var specs = PromptwallTranscript.BuildImagePrompts(
            promptText: promptText,
            replyText: includeReply ? reply : null);

        // ── 6. confirmation panel ────────────────────────────────────────────
        var preview = TruncatePreview(promptText, 200);
        var summary = includeReply
            ? $"[bold]Prompt[/]\n{Markup.Escape(preview)}\n\n[bold]Reply[/]\n{Markup.Escape(TruncatePreview(reply ?? "", 200))}"
            : $"[bold]Prompt[/]\n{Markup.Escape(preview)}";

        _console.WriteLine();
        _console.Write(new Panel(new Markup(summary))
            .Header(specs.Count == 1 ? " 1 image " : $" {specs.Count} images ")
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Cyan1))
            .Padding(1, 0));

        if (!_console.Prompt(new ConfirmationPrompt("[bold]Generate image?[/]") { DefaultValue = true }))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        // ── 7. auth check ────────────────────────────────────────────────────
        if (!await _google.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]No Google AI API key registered.[/]");
            _console.MarkupLine("[dim]Run [bold]pks google init[/] first.[/]");
            return 1;
        }

        // ── 8. generate & save ───────────────────────────────────────────────
        var outputDir = settings.Output ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var suffix = specs.Count == 1 ? "" : $"-{spec.Label.ToLowerInvariant()}";
            var fileName = $"promptwall-{stamp}{suffix}.jpg";
            var fullPath = Path.GetFullPath(Path.Combine(outputDir, fileName));

            byte[] bytes;
            try
            {
                bytes = await _console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(
                        $"Rendering [bold]{spec.Label}[/] with [bold]{Markup.Escape(settings.Model)}[/]…",
                        async _ => await _google.GenerateImageAsync(
                            spec.ImagePrompt, settings.Model, settings.AspectRatio,
                            resolution: "1024", inputImagePath: null));
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

            await File.WriteAllBytesAsync(fullPath, bytes);
            await File.WriteAllTextAsync(Path.ChangeExtension(fullPath, ".prompt"), spec.ImagePrompt);
            await File.WriteAllTextAsync(Path.ChangeExtension(fullPath, ".source.txt"), spec.SourceText);

            // stdout for agent piping
            Console.WriteLine(fullPath);
            _console.MarkupLine($"[green]Saved {spec.Label}:[/] {Markup.Escape(fullPath)} [dim]({bytes.Length / 1024} KB)[/]");
        }

        return 0;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static List<string> DiscoverSessionFiles(Settings settings, out string scope)
    {
        var claudeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");

        if (settings.AllProjects)
        {
            scope = "all projects";
            if (!Directory.Exists(claudeRoot)) return [];
            return Directory.GetDirectories(claudeRoot)
                .SelectMany(d => Directory.GetFiles(d, "*.jsonl", SearchOption.TopDirectoryOnly))
                .ToList();
        }

        var projectPath = settings.ProjectPath ?? Directory.GetCurrentDirectory();
        scope = projectPath;

        // Mirror ClaudeStatsCommand encoding: replace separators with '-' and prepend '-'.
        var encoded = projectPath
            .Replace(Path.DirectorySeparatorChar, '-')
            .Replace('/', '-');
        if (!encoded.StartsWith('-')) encoded = "-" + encoded;

        var dir = Path.Combine(claudeRoot, encoded);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly).ToList();
    }

    private static string TruncatePreview(string text, int max)
    {
        var collapsed = text.Replace("\r", "").Replace("\n", " ↵ ");
        return collapsed.Length <= max ? collapsed : collapsed[..(max - 1)] + "…";
    }

    private static string Humanize(DateTime utc)
    {
        var delta = DateTime.UtcNow - utc;
        if (delta.TotalSeconds < 60) return "<1 min ago";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalHours < 24)
        {
            var h = (int)delta.TotalHours;
            return h == 1 ? "1 hour ago" : $"{h} hours ago";
        }
        var d = (int)delta.TotalDays;
        return d == 1 ? "1 day ago" : $"{d} days ago";
    }
}
