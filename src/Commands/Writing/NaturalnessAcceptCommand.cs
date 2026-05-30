using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;

namespace PKS.Commands.Writing;

public class NaturalnessAcceptSettings : WritingSettings
{
    [CommandArgument(0, "<file>")]
    [Description("The source markdown file the reply is about. Used to anchor the sidecar.")]
    public string File { get; set; } = "";

    [CommandOption("--from")]
    [Description("Path to the LLM reply (raw JSON or markdown with a ```json block). Reads stdin if omitted.")]
    public string? From { get; set; }

    [CommandOption("--model")]
    [Description("Model id that produced the reply (recorded in the sidecar).")]
    public string? Model { get; set; }

    [CommandOption("--max-candidates")]
    [Description("Cap how many candidates are accepted. Default 15.")]
    public int MaxCandidates { get; set; } = NaturalnessCandidatesSchema.MaxCandidates;

    [CommandOption("--critic")]
    [Description("Critic name (e.g. opus, gpt5). Sidecar lands as <stem>.NATURALNESS-CANDIDATES.<critic>.json. Default 'opus'.")]
    public string Critic { get; set; } = "opus";
}

/// `pks writing naturalness accept <file> --from reply.json` — validates the
/// critic reply against [[NaturalnessCandidatesSchema]] and persists to
/// `_review/<stem>.NATURALNESS-CANDIDATES.json`.
public class NaturalnessAcceptCommand : AsyncCommand<NaturalnessAcceptSettings>
{
    private readonly INaturalnessPicksStore _store;
    private readonly INaturalnessMerger _merger;
    private readonly IWritingPathResolver _paths;

    public NaturalnessAcceptCommand(
        INaturalnessPicksStore store,
        INaturalnessMerger merger,
        IWritingPathResolver paths)
    {
        _store = store;
        _merger = merger;
        _paths = paths;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, NaturalnessAcceptSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.File))
        {
            Console.Error.WriteLine("error: file argument required.");
            return 2;
        }
        var full = System.IO.Path.GetFullPath(settings.File);
        if (!System.IO.File.Exists(full))
        {
            Console.Error.WriteLine($"error: not found: {full}");
            return 2;
        }

        string replyText;
        if (settings.From is { Length: > 0 } from)
        {
            var fromPath = System.IO.Path.GetFullPath(from);
            if (!System.IO.File.Exists(fromPath))
            {
                Console.Error.WriteLine($"error: --from not found: {fromPath}");
                return 2;
            }
            replyText = await System.IO.File.ReadAllTextAsync(fromPath);
        }
        else
        {
            replyText = await Console.In.ReadToEndAsync();
        }

        var content = await System.IO.File.ReadAllTextAsync(full);
        var lineCount = content.Replace("\r\n", "\n").Split('\n').Length;

        var v = NaturalnessCandidatesSchema.Validate(replyText, lineCount, settings.MaxCandidates);
        if (!v.Ok || v.Parsed is null)
        {
            var summary = new
            {
                ok = false,
                errors = v.Errors.Select(e => new { e.Field, e.Code, e.Message }).ToList(),
                hint = "Re-submit a corrected JSON reply. Required fields: post, candidates[]. Each candidate needs id/line/original/issue plus exactly 3 alternatives labelled A/B/C with authorlikeness in [0,1].",
            };
            Console.WriteLine("RESULT: " + JsonSerializer.Serialize(summary,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return 1;
        }

        var file = v.Parsed;
        file.Post = full;
        if (!string.IsNullOrWhiteSpace(settings.Model))
            file.CriticModel = settings.Model;

        var critic = string.IsNullOrWhiteSpace(settings.Critic) ? "opus" : settings.Critic.Trim();
        await _store.SaveCandidatesAsync(full, critic, file);
        var canonicalPath = await _merger.MergeAsync(full);
        var criticSidecarPath = _paths.NaturalnessCandidatesSidecarPath(full, critic);

        var ok = new
        {
            ok = true,
            critic,
            candidateCount = file.Candidates.Count,
            criticSidecarPath,
            canonicalSidecarPath = canonicalPath,
        };
        Console.WriteLine("RESULT: " + JsonSerializer.Serialize(ok,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }
}
