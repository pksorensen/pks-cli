using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Commands.Writing;

public class WritingAcceptSettings : WritingSettings
{
    [CommandArgument(0, "<file>")]
    [Description("The source markdown file the reply is about. Used to anchor the sidecar report.")]
    public string File { get; set; } = "";

    [CommandOption("--from")]
    [Description("Path to the LLM reply (raw JSON or markdown with a ```json block). Reads stdin if omitted.")]
    public string? From { get; set; }

    [CommandOption("--model")]
    [Description("Model id that produced the reply, recorded in the report (e.g. 'claude-haiku-4-5-20251001').")]
    public string? Model { get; set; }
}

/// Validates an LLM reply against [[WritingScoreSchema]] and, on success, writes
/// the report sidecars next to the source. On failure, exits non-zero with a
/// structured `RESULT:` JSON the agent can act on (retry with corrections).
public class WritingAcceptCommand : AsyncCommand<WritingAcceptSettings>
{
    private readonly IWritingPathResolver _paths;
    private readonly IWritingProfileStore _store;

    public WritingAcceptCommand(IWritingPathResolver paths, IWritingProfileStore store)
    {
        _paths = paths;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WritingAcceptSettings settings)
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

        var v = WritingScoreSchema.Validate(replyText, lineCount);

        if (!v.Ok)
        {
            var summary = new
            {
                ok = false,
                errors = v.Errors.Select(e => new { e.Field, e.Code, e.Message }).ToList(),
                hint = "Re-submit a corrected JSON reply. All five dimension scores (1-5) and a 'notes' field are required; finding lines must be within the source.",
            };
            Console.WriteLine("RESULT: " + JsonSerializer.Serialize(summary,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return 1;
        }

        // Compose + persist the report. Add any lint findings already on disk
        // so the saved sidecar carries both terminology + critic findings.
        var projectRoot = _paths.ResolveProjectRoot(System.IO.Path.GetDirectoryName(full)!);
        var channel = (await _store.LoadChannelConfigAsync(projectRoot)).DefaultChannel;

        var existing = await _store.LoadReportAsync(full);
        var report = new WritingReport
        {
            SourcePath = full,
            Channel = channel,
            DimensionScores = v.Dimensions,
            CriticNotes = v.Notes,
            CriticModel = settings.Model,
            Findings = (existing?.Findings ?? new List<WritingFinding>())
                .Where(f => f.RuleId.StartsWith("Writing.", StringComparison.Ordinal))  // keep lint findings
                .Concat(v.Findings)
                .ToList(),
        };
        if (v.Dimensions.Count > 0)
            report.Score = (int)Math.Round(v.Dimensions.Values.Average() * 20);

        await _store.SaveReportAsync(full, report);

        var success = new
        {
            ok = true,
            score = report.Score,
            dimensions = report.DimensionScores,
            findingCount = report.Findings.Count,
            reportPath = _paths.ReportSidecarMarkdownPath(full),
        };
        Console.WriteLine("RESULT: " + JsonSerializer.Serialize(success,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }
}
