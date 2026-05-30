using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Persona;

namespace PKS.Commands.Persona;

public class PersonaScoreAllSettings : PersonaSettings
{
    [CommandArgument(0, "<content>")]
    [Description("Content file to score.")]
    public string Content { get; set; } = "";

    [CommandOption("--locale")]
    [Description("Persona locale to iterate. Default: da.")]
    public string Locale { get; set; } = "da";

    [CommandOption("--rubric")]
    [Description("Limit to a single rubric id. Default: iterate all rubrics in personas/_rubrics/.")]
    public string? Rubric { get; set; }

    [CommandOption("--persona")]
    [Description("Limit to a single persona id. Default: iterate all personas in the locale.")]
    public string? Persona { get; set; }

    [CommandOption("--model")]
    [Description("Model id to call via pks agent. Default: claude-opus-4-7.")]
    public string Model { get; set; } = "claude-opus-4-7";

    [CommandOption("--screen-with")]
    [Description("Cheap pre-pass model (e.g. claude-haiku-4-5). When set, each (persona, rubric) is screened first; only candidates scoring ≥ 3 get the deep --model pass.")]
    public string? ScreenWith { get; set; }

    [CommandOption("--max-output-tokens")]
    [Description("Cap on output tokens per call. Default 1500.")]
    public int MaxOutputTokens { get; set; } = 1500;

    [CommandOption("--only-missing")]
    [Description("Skip (persona, rubric) cells that are already present in the sidecar. Useful for resuming after an aborted batch.")]
    public bool OnlyMissing { get; set; }

    [CommandOption("--per-model")]
    [Description("Scope the sidecar to the scoring model: _review/<locale>.PERSONA-SCORES.<model>.json. Lets scores from different --model values coexist instead of overwriting each other (per-post model A/B).")]
    public bool PerModel { get; set; }
}

/// <summary>
/// Bulk persona × rubric matrix scoring with an optional Haiku pre-screen
/// pass to filter obvious mismatches before the expensive Opus deep pass.
/// </summary>
public class PersonaScoreAllCommand : AsyncCommand<PersonaScoreAllSettings>
{
    private readonly IPersonaPathResolver _paths;
    private readonly IPersonaStore _personas;
    private readonly IRubricStore _rubrics;
    private readonly IPersonaScoresStore _scores;
    private readonly PersonaScoreRunner _runner;

    public PersonaScoreAllCommand(
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

    public override async Task<int> ExecuteAsync(CommandContext context, PersonaScoreAllSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Content)) { Console.Error.WriteLine("error: content argument required."); return 2; }
        var full = System.IO.Path.GetFullPath(settings.Content);
        if (!System.IO.File.Exists(full)) { Console.Error.WriteLine($"error: content not found: {full}"); return 2; }

        var root = _paths.ResolvePersonasRoot(System.IO.Path.GetDirectoryName(full)!)
                   ?? _paths.ResolvePersonasRoot(System.IO.Directory.GetCurrentDirectory());
        if (root is null) { Console.Error.WriteLine("error: no personas/ directory found."); return 2; }

        var personas = await _personas.ListAsync(root, settings.Locale);
        if (!string.IsNullOrWhiteSpace(settings.Persona))
            personas = personas.Where(p => string.Equals(p.Id, settings.Persona, StringComparison.Ordinal)).ToList();
        if (personas.Count == 0) { AnsiConsole.MarkupLine("[yellow]![/] No personas matched."); return 0; }

        var rubrics = await _rubrics.ListAsync(root);
        if (!string.IsNullOrWhiteSpace(settings.Rubric))
            rubrics = rubrics.Where(r => string.Equals(r.Id, settings.Rubric, StringComparison.Ordinal)).ToList();
        if (rubrics.Count == 0) { AnsiConsole.MarkupLine("[yellow]![/] No rubrics matched."); return 0; }

        var content = await System.IO.File.ReadAllTextAsync(full);

        // Per-model sidecar tag: when set, all reads/writes target
        // _review/<locale>.PERSONA-SCORES.<model>.json so different models
        // don't upsert over each other.
        var modelTag = settings.PerModel ? settings.Model : null;

        // If --only-missing, load the existing sidecar so we can skip cells
        // that already have a score. Lets a resumed run after auth death do
        // only the work it needs to.
        var existing = settings.OnlyMissing
            ? new HashSet<string>((await _scores.LoadAsync(full, settings.Locale, modelTag)).Scores
                .Select(s => $"{s.PersonaId}/{s.Rubric}"), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var total = personas.Count * rubrics.Count;
        var skippedExisting = settings.OnlyMissing
            ? personas.Sum(p => rubrics.Count(r => existing.Contains($"{p.Id}/{r.Id}")))
            : 0;
        AnsiConsole.MarkupLine($"[grey]Scoring {personas.Count} personas × {rubrics.Count} rubrics = {total} call(s){(settings.ScreenWith is null ? "" : " (with " + Markup.Escape(settings.ScreenWith) + " pre-screen)")}{(skippedExisting > 0 ? $" — skipping {skippedExisting} already-scored cells" : "")}…[/]");

        // Preflight probe: one cheap call against the first persona × first
        // rubric to validate auth before kicking off a long batch. If the
        // token's dead we'd rather find out in 2 seconds than partway through
        // 60 calls.
        try
        {
            var probe = await _runner.RunAsync(personas[0], rubrics[0], full, content, settings.Model, settings.MaxOutputTokens);
            if (probe.Ok && probe.Score is not null)
            {
                await _scores.SaveScoreAsync(full, settings.Locale, probe.Score, modelTag);
                AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(personas[0].Id)}/{Markup.Escape(rubrics[0].Id)} [grey]score={probe.Score.Score} (preflight)[/]");
            }
            else if (probe.Errors.Count > 0)
            {
                AnsiConsole.MarkupLine($"  [yellow]●[/] {Markup.Escape(personas[0].Id)}/{Markup.Escape(rubrics[0].Id)} [grey](preflight validation failed; continuing)[/]");
            }
        }
        catch (Exception ex) when (IsAuthFailure(ex))
        {
            EmitAuthFailureSummary(full, settings.Locale, ex, new List<object>(), 0, modelTag);
            return 2;
        }

        var rolled = new List<object>();
        var ok = 0; var fail = 0; var skipped = 0;
        var didProbe = false; // skip the probe-pair on the first inner iteration
        foreach (var p in personas)
        {
            foreach (var r in rubrics)
            {
                if (!didProbe && p.Id == personas[0].Id && r.Id == rubrics[0].Id)
                {
                    // Probe already covered this cell; don't double-charge.
                    didProbe = true;
                    ok++;
                    rolled.Add(new { personaId = p.Id, rubric = r.Id, model = settings.Model, score = "(preflight)" });
                    continue;
                }

                if (existing.Contains($"{p.Id}/{r.Id}"))
                {
                    // --only-missing: this cell already has a score.
                    skipped++;
                    rolled.Add(new { personaId = p.Id, rubric = r.Id, model = "(existing)", score = "(existing)", skipped = true });
                    continue;
                }
                // Optional cheap screen.
                if (settings.ScreenWith is { Length: > 0 } screen)
                {
                    PersonaScoreRunner.Result screenResult;
                    try { screenResult = await _runner.RunAsync(p, r, full, content, screen, settings.MaxOutputTokens); }
                    catch (Exception ex) when (IsAuthFailure(ex))
                    {
                        EmitAuthFailureSummary(full, settings.Locale, ex, rolled, ok + skipped + fail, modelTag);
                        return 2;
                    }
                    if (screenResult.Ok && screenResult.Score is not null && screenResult.Score.Score < 3)
                    {
                        // Below threshold — keep the cheap score, skip Opus.
                        screenResult.Score.Model = screen + " (screen-only)";
                        await _scores.SaveScoreAsync(full, settings.Locale, screenResult.Score, modelTag);
                        skipped++;
                        AnsiConsole.MarkupLine($"  [grey]· skipped[/] {Markup.Escape(p.Id)}/{Markup.Escape(r.Id)} [grey](screen={screenResult.Score.Score})[/]");
                        rolled.Add(new { personaId = p.Id, rubric = r.Id, model = screen, score = screenResult.Score.Score, skipped = true });
                        continue;
                    }
                }

                PersonaScoreRunner.Result result;
                try { result = await _runner.RunAsync(p, r, full, content, settings.Model, settings.MaxOutputTokens); }
                catch (Exception ex) when (IsAuthFailure(ex))
                {
                    EmitAuthFailureSummary(full, settings.Locale, ex, rolled, ok + skipped + fail, modelTag);
                    return 2;
                }
                if (!result.Ok || result.Score is null)
                {
                    fail++;
                    AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(p.Id)}/{Markup.Escape(r.Id)} [grey](validation failed)[/]");
                    rolled.Add(new
                    {
                        personaId = p.Id,
                        rubric = r.Id,
                        model = settings.Model,
                        ok = false,
                        errors = result.Errors.Select(e => new { e.Field, e.Code, e.Message }).ToList(),
                    });
                    continue;
                }

                await _scores.SaveScoreAsync(full, settings.Locale, result.Score, modelTag);
                ok++;
                AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(p.Id)}/{Markup.Escape(r.Id)} [grey]score={result.Score.Score}[/]");
                rolled.Add(new { personaId = p.Id, rubric = r.Id, model = settings.Model, score = result.Score.Score });
            }
        }

        var sidecar = _paths.ScoresSidecarPath(full, settings.Locale, modelTag);
        var summary = new
        {
            ok = fail == 0,
            content = full,
            sidecarPath = sidecar,
            counts = new { ok, fail, skipped, total },
            results = rolled,
        };
        Console.WriteLine("RESULT: " + JsonSerializer.Serialize(summary,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Recognises the family of credential-refresh failures that mean
    /// "stop the batch, ask the user to re-auth, exit cleanly." Distinct
    /// from a transient validation failure on one call.
    /// </summary>
    private static bool IsAuthFailure(Exception ex)
    {
        var msg = (ex.Message ?? "") + " " + (ex.InnerException?.Message ?? "");
        return msg.Contains("Foundry access token", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("pks foundry login", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("anthropic model needs an apiKey", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("AADSTS50196", StringComparison.Ordinal)
               || msg.Contains("AADSTS70008", StringComparison.Ordinal);
    }

    /// <summary>
    /// Persists what we already have, prints a structured summary, and
    /// makes the re-auth action obvious. The score-all caller will see
    /// exit code 2 and can resume — the upsert semantics in
    /// PersonaScoresStore mean re-running fills only the missing cells.
    /// </summary>
    private void EmitAuthFailureSummary(string contentPath, string locale, Exception ex, List<object> rolled, int completed, string? modelTag = null)
    {
        var sidecar = _paths.ScoresSidecarPath(contentPath, locale, modelTag);
        AnsiConsole.MarkupLine($"  [red]⚠[/] [bold]auth failed[/] after {completed} call(s) — partial results saved.");
        AnsiConsole.MarkupLine("    Refresh with [cyan]pks foundry login[/] (Azure Foundry) or set the relevant provider credential, then re-run.");
        var summary = new
        {
            ok = false,
            reason = "auth-failure",
            errorMessage = ex.Message,
            content = contentPath,
            sidecarPath = sidecar,
            completedCalls = completed,
            results = rolled,
            hint = "Run `pks foundry login`, then re-run the same `pks persona score-all` command — the sidecar upsert will fill in only the missing (persona, rubric) pairs.",
        };
        Console.WriteLine("RESULT: " + JsonSerializer.Serialize(summary,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
