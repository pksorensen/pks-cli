using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;

namespace PKS.Commands.Writing;

public class WritingPromptSettings : WritingSettings
{
    [CommandArgument(0, "<file>")]
    [Description("Markdown file to score. The post body, profile, channel rubric, and references are bundled into the prompt.")]
    public string File { get; set; } = "";

    [CommandOption("--format")]
    [Description("Output format: 'json' (default; bundle of system+user+schema+meta) or 'markdown'.")]
    public string Format { get; set; } = "json";

    [CommandOption("--max-references")]
    [Description("Cap how many reference samples are injected. Default 10.")]
    public int MaxReferences { get; set; } = 10;

    [CommandOption("--max-findings")]
    [Description("Cap how many findings the model may return. Default 12.")]
    public int MaxFindings { get; set; } = 12;
}

/// Emits the structured score prompt for the given file. The agent reads this,
/// calls its OWN LLM, and submits the reply via `pks writing accept`. pks-cli
/// does NOT spawn an LLM in this flow — keeps tool/model coupling out of the CLI.
public class WritingPromptCommand : AsyncCommand<WritingPromptSettings>
{
    private readonly IWritingPathResolver _paths;
    private readonly IWritingProfileStore _store;

    public WritingPromptCommand(IWritingPathResolver paths, IWritingProfileStore store)
    {
        _paths = paths;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WritingPromptSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.File))
        {
            Console.Error.WriteLine("error: file argument required.");
            return 1;
        }
        var full = System.IO.Path.GetFullPath(settings.File);
        if (!System.IO.File.Exists(full))
        {
            Console.Error.WriteLine($"error: not found: {full}");
            return 1;
        }

        var projectRoot = _paths.ResolveProjectRoot(System.IO.Path.GetDirectoryName(full)!);
        var content = await System.IO.File.ReadAllTextAsync(full);
        var profile = await _store.LoadProfileAsync();
        var channel = (await _store.LoadChannelConfigAsync(projectRoot)).DefaultChannel;
        var rubric = await _store.LoadChannelRubricAsync(channel);
        var references = await _store.LoadReferenceSamplesAsync(channel);
        var anglicisms = await _store.LoadAnglicismsAsync(projectRoot);

        var bundle = WritingScorePrompt.Build(new WritingScorePrompt.Request
        {
            SourcePath = full,
            Content = content,
            Channel = channel,
            Profile = profile,
            ChannelRubric = rubric,
            References = references,
            Anglicisms = anglicisms,
            MaxReferences = settings.MaxReferences,
            MaxFindings = settings.MaxFindings,
        });

        // PLAIN stdout — no banner, no markup — so the output is pipeable.
        if (settings.Format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("# SYSTEM PROMPT\n");
            Console.WriteLine(bundle.System);
            Console.WriteLine("\n---\n\n# USER PROMPT\n");
            Console.WriteLine(bundle.User);
        }
        else
        {
            var jsonOpts = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                system = bundle.System,
                user = bundle.User,
                schema = bundle.Schema,
                meta = bundle.Meta,
            }, jsonOpts));
        }
        return 0;
    }
}
