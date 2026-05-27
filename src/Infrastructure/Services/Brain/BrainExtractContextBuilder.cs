using System.Text.Json;
using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

public sealed class BrainExtractContextBuilder : IBrainExtractContextBuilder
{
    // Caps: keep the per-extract prompt small so each AI call stays cheap.
    private const int MaxPromptsVerbatim = 20;
    private const int MaxPromptCharsEach = 4000;
    private const int MaxErrors = 10;
    private const int MaxErrorSnippetChars = 500;

    private readonly IBrainPathResolver _paths;
    private readonly IFirehoseReader _firehose;

    public BrainExtractContextBuilder(IBrainPathResolver paths, IFirehoseReader firehose)
    {
        _paths = paths;
        _firehose = firehose;
    }

    public async Task<ExtractContext?> BuildAsync(string sessionId, string projectSlug, CancellationToken ct = default)
    {
        var metaPath = _paths.GlobalSessionFile(projectSlug, sessionId);
        if (!File.Exists(metaPath)) return null;

        SessionMetadata? meta;
        try
        {
            var json = await File.ReadAllTextAsync(metaPath, ct);
            meta = JsonSerializer.Deserialize<SessionMetadata>(json, BrainIndexStore.JsonOptions);
        }
        catch (JsonException) { return null; }
        if (meta is null) return null;

        var promptsList = new List<PromptRow>();
        await foreach (var p in _firehose.ReadAsync<PromptRow>(BrainFirehose.Prompts, sessionId, ct))
            promptsList.Add(p);
        var prompts = promptsList
            .OrderBy(p => p.TimestampUtc)
            .Take(MaxPromptsVerbatim)
            .ToList();
        foreach (var p in prompts) p.Text = Truncate(p.Text, MaxPromptCharsEach);

        var errorsList = new List<ErrorRow>();
        await foreach (var e in _firehose.ReadAsync<ErrorRow>(BrainFirehose.Errors, sessionId, ct))
            errorsList.Add(e);
        var errors = errorsList
            .OrderBy(e => e.TimestampUtc)
            .Take(MaxErrors)
            .ToList();
        foreach (var e in errors) e.Snippet = Truncate(e.Snippet, MaxErrorSnippetChars);

        // Plan bodies are stored in tools.jsonl when ExitPlanMode was called —
        // pull the plan text out of inputPreview (parser already truncated to 200 chars,
        // so this is best-effort; the full body is in the source JSONL).
        var toolsList = new List<ToolCallRow>();
        await foreach (var t in _firehose.ReadAsync<ToolCallRow>(BrainFirehose.Tools, sessionId, ct))
            toolsList.Add(t);
        var plans = toolsList
            .Where(t => t.ToolName == "ExitPlanMode" && t.InputPreview is { Length: > 0 })
            .Select(t => new PlanBody { ToolUseId = t.ToolUseId, Body = t.InputPreview! })
            .ToList();

        return new ExtractContext
        {
            Meta = meta,
            Prompts = prompts,
            TopTools = meta.TopTools,
            TopFiles = meta.TopFiles,
            Errors = errors,
            Plans = plans,
            Subagents = meta.Subagents,
            Skills = meta.Skills,
        };
    }

    private static string Truncate(string? s, int max)
    {
        if (s is null) return "";
        if (s.Length <= max) return s;
        return s[..max] + "…";
    }
}
