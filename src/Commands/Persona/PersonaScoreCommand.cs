using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Persona;

namespace PKS.Commands.Persona;

public class PersonaScoreSettings : PersonaSettings
{
    [CommandArgument(0, "<content>")]
    [Description("Content file to score (markdown).")]
    public string Content { get; set; } = "";

    [CommandOption("--persona")]
    [Description("Persona id to score against.")]
    public string Persona { get; set; } = "";

    [CommandOption("--rubric")]
    [Description("Rubric id (e.g. relevance).")]
    public string Rubric { get; set; } = "";

    [CommandOption("--locale")]
    [Description("Persona locale. Default: da.")]
    public string Locale { get; set; } = "da";

    [CommandOption("--model")]
    [Description("Model id to call via pks agent. Default: claude-opus-4-7.")]
    public string Model { get; set; } = "claude-opus-4-7";

    [CommandOption("--max-output-tokens")]
    [Description("Cap on output tokens. Default 1500.")]
    public int MaxOutputTokens { get; set; } = 1500;

    [CommandOption("--per-model")]
    [Description("Scope the sidecar to the scoring model: _review/<locale>.PERSONA-SCORES.<model>.json. Lets scores from different --model values coexist instead of overwriting each other.")]
    public bool PerModel { get; set; }
}

/// <summary>
/// One-shot scoring: prompt + agent-call + validate + persist. Same sidecar
/// shape as <c>pks persona accept</c>.
/// </summary>
public class PersonaScoreCommand : AsyncCommand<PersonaScoreSettings>
{
    private readonly IPersonaPathResolver _paths;
    private readonly IPersonaStore _personas;
    private readonly IRubricStore _rubrics;
    private readonly IPersonaScoresStore _scores;
    private readonly PersonaScoreRunner _runner;

    public PersonaScoreCommand(
        IPersonaPathResolver paths,
        IPersonaStore personas,
        IRubricStore rubrics,
        IPersonaScoresStore scores,
        PersonaScoreRunner runner)
    {
        _paths = paths;
        _personas = personas;
        _rubrics = rubrics;
        _scores = scores;
        _runner = runner;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, PersonaScoreSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Content)) { Console.Error.WriteLine("error: content argument required."); return 2; }
        if (string.IsNullOrWhiteSpace(settings.Persona)) { Console.Error.WriteLine("error: --persona <id> required."); return 2; }
        if (string.IsNullOrWhiteSpace(settings.Rubric)) { Console.Error.WriteLine("error: --rubric <name> required."); return 2; }

        var full = System.IO.Path.GetFullPath(settings.Content);
        if (!System.IO.File.Exists(full)) { Console.Error.WriteLine($"error: content not found: {full}"); return 2; }

        var root = _paths.ResolvePersonasRoot(System.IO.Path.GetDirectoryName(full)!)
                   ?? _paths.ResolvePersonasRoot(System.IO.Directory.GetCurrentDirectory());
        if (root is null) { Console.Error.WriteLine("error: no personas/ directory found."); return 2; }

        var persona = await _personas.LoadByIdAsync(root, settings.Locale, settings.Persona);
        if (persona is null) { Console.Error.WriteLine($"error: persona '{settings.Persona}' not found."); return 2; }
        var rubric = await _rubrics.LoadAsync(root, settings.Rubric);
        if (rubric is null) { Console.Error.WriteLine($"error: rubric '{settings.Rubric}' not found."); return 2; }

        var content = await System.IO.File.ReadAllTextAsync(full);
        var result = await _runner.RunAsync(persona, rubric, full, content, settings.Model, settings.MaxOutputTokens);

        if (!result.Ok || result.Score is null)
        {
            var fail = new
            {
                ok = false,
                personaId = persona.Id,
                rubric = rubric.Id,
                model = settings.Model,
                errors = result.Errors.Select(e => new { e.Field, e.Code, e.Message }).ToList(),
                rawReplyHead = result.RawReply.Length > 500 ? result.RawReply.Substring(0, 500) + "…" : result.RawReply,
            };
            Console.WriteLine("RESULT: " + JsonSerializer.Serialize(fail,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return 1;
        }

        var modelTag = settings.PerModel ? settings.Model : null;
        await _scores.SaveScoreAsync(full, settings.Locale, result.Score, modelTag);
        var sidecar = _paths.ScoresSidecarPath(full, settings.Locale, modelTag);

        var ok = new
        {
            ok = true,
            personaId = persona.Id,
            rubric = rubric.Id,
            model = settings.Model,
            score = result.Score.Score,
            sidecarPath = sidecar,
        };
        Console.WriteLine("RESULT: " + JsonSerializer.Serialize(ok,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }
}
