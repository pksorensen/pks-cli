using System.ComponentModel;
using PKS.Infrastructure.Services.Brain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Brain;

public class BrainConversationSettings : BrainSettings
{
    [CommandArgument(0, "<SESSION>")]
    [Description("Claude session ID or path to a raw session JSONL file.")]
    public string Session { get; set; } = string.Empty;

    [CommandOption("-o|--output")]
    [Description("Output markdown path. Default: ./.pks/brain/conversations/<session-id>.md")]
    public string? Output { get; set; }

    [CommandOption("--max-message-chars")]
    [Description("Keep at most this many characters per visible text block; reference the remainder.")]
    [DefaultValue(12_000)]
    public int MaxMessageChars { get; set; } = 12_000;

    [CommandOption("--include-intermediate")]
    [Description("Keep assistant progress narration between tool calls; default keeps final/end-turn replies only.")]
    public bool IncludeIntermediate { get; set; }
}

public sealed class BrainConversationCommand : AsyncCommand<BrainConversationSettings>
{
    private readonly ISessionDiscoveryService _discovery;
    private readonly IBrainPathResolver _paths;
    private readonly IBrainConversationExporter _exporter;

    public BrainConversationCommand(
        ISessionDiscoveryService discovery,
        IBrainPathResolver paths,
        IBrainConversationExporter exporter)
    {
        _discovery = discovery;
        _paths = paths;
        _exporter = exporter;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainConversationSettings settings)
    {
        if (settings.MaxMessageChars < 1)
        {
            AnsiConsole.MarkupLine("[red]--max-message-chars must be positive.[/]");
            return 1;
        }

        string source;
        if (File.Exists(settings.Session))
        {
            source = Path.GetFullPath(settings.Session);
        }
        else
        {
            var matches = _discovery.Enumerate()
                .Where(x => Path.GetFileNameWithoutExtension(x.JsonlPath)
                    .Equals(settings.Session, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.JsonlPath)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (matches.Count == 0)
            {
                AnsiConsole.MarkupLine($"[red]Session not found:[/] {Markup.Escape(settings.Session)}");
                AnsiConsole.MarkupLine("Pass an exact session ID or a JSONL path.");
                return 1;
            }
            if (matches.Count > 1)
            {
                AnsiConsole.MarkupLine($"[red]Session ID is ambiguous:[/] {Markup.Escape(settings.Session)}");
                foreach (var match in matches.Take(10))
                    AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(match)}[/]");
                AnsiConsole.MarkupLine("Pass the full JSONL path.");
                return 1;
            }
            source = matches[0];
        }

        var sessionId = Path.GetFileNameWithoutExtension(source);
        var output = settings.Output;
        if (string.IsNullOrWhiteSpace(output))
        {
            var projectBrain = _paths.ResolveProjectRoot(Directory.GetCurrentDirectory());
            output = projectBrain is null
                ? Path.Combine(Directory.GetCurrentDirectory(), sessionId + ".conversation.md")
                : Path.Combine(projectBrain, "conversations", sessionId + ".md");
        }

        try
        {
            var result = await _exporter.ExportAsync(new BrainConversationExportOptions
            {
                SourcePath = source,
                OutputPath = output,
                MaxVisibleCharsPerBlock = settings.MaxMessageChars,
                IncludeIntermediateAssistantText = settings.IncludeIntermediate,
            });
            AnsiConsole.MarkupLine($"[green]✓[/] Wrote [cyan]{Markup.Escape(result.OutputPath)}[/]");
            AnsiConsole.MarkupLine(
                $"[grey]{result.HumanMessages} human messages · {result.AssistantTextBlocks} assistant text blocks · " +
                $"{result.OmittedBlocks} referenced omissions · {result.SourceBytes:N0} raw bytes[/]");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AnsiConsole.MarkupLine($"[red]Conversation export failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
