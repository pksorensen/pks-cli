using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Persona;

namespace PKS.Commands.Persona;

public class PersonaAcceptSettings : PersonaSettings
{
    [CommandArgument(0, "<content>")]
    [Description("The content file the reply is about. Anchors the sidecar.")]
    public string Content { get; set; } = "";

    [CommandOption("--persona")]
    [Description("Persona id the reply is scored against.")]
    public string Persona { get; set; } = "";

    [CommandOption("--rubric")]
    [Description("Rubric id the reply is scored against.")]
    public string Rubric { get; set; } = "";

    [CommandOption("--locale")]
    [Description("Locale. Default: da.")]
    public string Locale { get; set; } = "da";

    [CommandOption("--from")]
    [Description("Path to the LLM reply (JSON or markdown with a ```json block). Reads stdin if omitted.")]
    public string? From { get; set; }

    [CommandOption("--model")]
    [Description("Model id that produced the reply. Recorded in the sidecar.")]
    public string Model { get; set; } = "unknown";

    [CommandOption("--per-model")]
    [Description("Scope the sidecar to the producing model: _review/<locale>.PERSONA-SCORES.<model>.json. Lets replies from different --model values coexist instead of overwriting each other.")]
    public bool PerModel { get; set; }
}

/// <summary>
/// Validates an LLM reply against the rubric's score schema and persists it
/// into <c>_review/&lt;locale&gt;.PERSONA-SCORES.json</c> next to the content.
/// </summary>
public class PersonaAcceptCommand : AsyncCommand<PersonaAcceptSettings>
{
    private readonly IPersonaPathResolver _paths;
    private readonly IPersonaStore _personas;
    private readonly IRubricStore _rubrics;
    private readonly IPersonaScoresStore _scores;

    public PersonaAcceptCommand(
        IPersonaPathResolver paths,
        IPersonaStore personas,
        IRubricStore rubrics,
        IPersonaScoresStore scores)
    {
        _paths = paths;
        _personas = personas;
        _rubrics = rubrics;
        _scores = scores;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, PersonaAcceptSettings settings)
    {
        if (!Validate(settings, out var err)) { Console.Error.WriteLine(err); return 2; }

        var full = System.IO.Path.GetFullPath(settings.Content);
        if (!System.IO.File.Exists(full)) { Console.Error.WriteLine($"error: content not found: {full}"); return 2; }

        var root = _paths.ResolvePersonasRoot(System.IO.Path.GetDirectoryName(full)!)
                   ?? _paths.ResolvePersonasRoot(System.IO.Directory.GetCurrentDirectory());
        if (root is null) { Console.Error.WriteLine("error: no personas/ directory found."); return 2; }

        var persona = await _personas.LoadByIdAsync(root, settings.Locale, settings.Persona);
        if (persona is null) { Console.Error.WriteLine($"error: persona '{settings.Persona}' not found."); return 2; }

        var rubric = await _rubrics.LoadAsync(root, settings.Rubric);
        if (rubric is null) { Console.Error.WriteLine($"error: rubric '{settings.Rubric}' not found."); return 2; }

        string replyText;
        if (settings.From is { Length: > 0 } from)
        {
            var fromPath = System.IO.Path.GetFullPath(from);
            if (!System.IO.File.Exists(fromPath)) { Console.Error.WriteLine($"error: --from not found: {fromPath}"); return 2; }
            replyText = await System.IO.File.ReadAllTextAsync(fromPath);
        }
        else
        {
            replyText = await Console.In.ReadToEndAsync();
        }

        var v = PersonaScoreSchema.Validate(replyText, rubric, persona.Id, settings.Model);
        if (!v.Ok || v.Parsed is null)
        {
            var summary = new
            {
                ok = false,
                personaId = persona.Id,
                rubric = rubric.Id,
                errors = v.Errors.Select(e => new { e.Field, e.Code, e.Message }).ToList(),
                hint = "Re-submit a corrected JSON reply. Required fields: score (1-5), rationale, evidence[]. Subscores required if the rubric declares them.",
            };
            Console.WriteLine("RESULT: " + JsonSerializer.Serialize(summary,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return 1;
        }

        var modelTag = settings.PerModel ? settings.Model : null;
        await _scores.SaveScoreAsync(full, settings.Locale, v.Parsed, modelTag);
        var sidecar = _paths.ScoresSidecarPath(full, settings.Locale, modelTag);

        var ok = new
        {
            ok = true,
            personaId = persona.Id,
            rubric = rubric.Id,
            score = v.Parsed.Score,
            sidecarPath = sidecar,
        };
        Console.WriteLine("RESULT: " + JsonSerializer.Serialize(ok,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }

    private static bool Validate(PersonaAcceptSettings s, out string error)
    {
        if (string.IsNullOrWhiteSpace(s.Content)) { error = "error: content argument required."; return false; }
        if (string.IsNullOrWhiteSpace(s.Persona)) { error = "error: --persona <id> required."; return false; }
        if (string.IsNullOrWhiteSpace(s.Rubric)) { error = "error: --rubric <name> required."; return false; }
        error = ""; return true;
    }
}
