using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;

namespace PKS.Commands.Writing;

public class NaturalnessPromptSettings : WritingSettings
{
    [CommandArgument(0, "<file>")]
    [Description("Markdown file to extract naturalness candidates from.")]
    public string File { get; set; } = "";

    [CommandOption("--format")]
    [Description("Output format: 'json' (default; bundle of system+user+schema+meta) or 'markdown'.")]
    public string Format { get; set; } = "json";

    [CommandOption("--max-candidates")]
    [Description("Cap how many candidate sentences the critic may surface. Default 15.")]
    public int MaxCandidates { get; set; } = NaturalnessCandidatesSchema.MaxCandidates;
}

/// `pks writing naturalness prompt <file>` — emits the JSON bundle the operator
/// feeds to its critic of choice. Agent-driven: pks-cli does NOT call an LLM here.
public class NaturalnessPromptCommand : AsyncCommand<NaturalnessPromptSettings>
{
    private readonly INaturalnessPromptBuilder _builder;
    private readonly IWritingProfileStore _profile;
    private readonly INaturalnessPatternStore _patterns;

    public NaturalnessPromptCommand(
        INaturalnessPromptBuilder builder,
        IWritingProfileStore profile,
        INaturalnessPatternStore patterns)
    {
        _builder = builder;
        _profile = profile;
        _patterns = patterns;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, NaturalnessPromptSettings settings)
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

        var content = await System.IO.File.ReadAllTextAsync(full);
        var profile = await _profile.LoadProfileAsync();
        var patterns = await _patterns.LoadAllAsync();

        var bundle = await _builder.BuildAsync(new NaturalnessPromptRequest
        {
            SourcePath = full,
            Content = content,
            Profile = profile,
            Patterns = patterns,
            MaxCandidates = settings.MaxCandidates,
        });

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
