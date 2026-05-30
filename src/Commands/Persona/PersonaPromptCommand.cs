using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Persona;

namespace PKS.Commands.Persona;

public class PersonaPromptSettings : PersonaSettings
{
    [CommandArgument(0, "<content>")]
    [Description("Content file to score (markdown).")]
    public string Content { get; set; } = "";

    [CommandOption("--persona")]
    [Description("Persona id to score against.")]
    public string Persona { get; set; } = "";

    [CommandOption("--rubric")]
    [Description("Rubric id (e.g. relevance, resonance, quality).")]
    public string Rubric { get; set; } = "";

    [CommandOption("--locale")]
    [Description("Persona locale. Default: da.")]
    public string Locale { get; set; } = "da";

    [CommandOption("--model")]
    [Description("Model id hint embedded in the bundle's meta. Default: claude-opus-4-7.")]
    public string Model { get; set; } = "claude-opus-4-7";

    [CommandOption("--format")]
    [Description("Output format: json (default; { system, user, schema, meta }) or markdown.")]
    public string Format { get; set; } = "json";
}

/// <summary>
/// Emits the persona-score prompt bundle to stdout. The agent reads this,
/// calls its OWN LLM, and submits the reply via <c>pks persona accept</c>.
/// </summary>
public class PersonaPromptCommand : AsyncCommand<PersonaPromptSettings>
{
    private readonly IPersonaPathResolver _paths;
    private readonly IPersonaStore _personas;
    private readonly IRubricStore _rubrics;

    public PersonaPromptCommand(IPersonaPathResolver paths, IPersonaStore personas, IRubricStore rubrics)
    {
        _paths = paths;
        _personas = personas;
        _rubrics = rubrics;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, PersonaPromptSettings settings)
    {
        if (!ValidateArgs(settings, out var err)) { Console.Error.WriteLine(err); return 2; }

        var contentFull = System.IO.Path.GetFullPath(settings.Content);
        if (!System.IO.File.Exists(contentFull))
        {
            Console.Error.WriteLine($"error: content not found: {contentFull}");
            return 2;
        }

        var root = _paths.ResolvePersonasRoot(System.IO.Path.GetDirectoryName(contentFull)!)
                   ?? _paths.ResolvePersonasRoot(System.IO.Directory.GetCurrentDirectory());
        if (root is null) { Console.Error.WriteLine("error: no personas/ directory found."); return 2; }

        var persona = await _personas.LoadByIdAsync(root, settings.Locale, settings.Persona);
        if (persona is null) { Console.Error.WriteLine($"error: persona '{settings.Persona}' not found under personas/{settings.Locale}/."); return 2; }

        var rubric = await _rubrics.LoadAsync(root, settings.Rubric);
        if (rubric is null) { Console.Error.WriteLine($"error: rubric '{settings.Rubric}' not found under personas/_rubrics/."); return 2; }

        var content = await System.IO.File.ReadAllTextAsync(contentFull);
        var bundle = PersonaScorePrompt.Build(new PersonaScorePrompt.Request
        {
            ContentPath = contentFull,
            Content = content,
            Persona = persona,
            Rubric = rubric,
            ModelHint = settings.Model,
        });

        if (settings.Format.Equals("markdown", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("# SYSTEM PROMPT\n");
            Console.WriteLine(bundle.System);
            Console.WriteLine("\n---\n\n# USER PROMPT\n");
            Console.WriteLine(bundle.User);
            return 0;
        }

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
        return 0;
    }

    private static bool ValidateArgs(PersonaPromptSettings s, out string error)
    {
        if (string.IsNullOrWhiteSpace(s.Content)) { error = "error: content argument required."; return false; }
        if (string.IsNullOrWhiteSpace(s.Persona)) { error = "error: --persona <id> required."; return false; }
        if (string.IsNullOrWhiteSpace(s.Rubric)) { error = "error: --rubric <name> required."; return false; }
        error = ""; return true;
    }
}
