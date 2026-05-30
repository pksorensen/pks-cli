using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;

namespace PKS.Commands.Writing;

public class NaturalnessMergeSettings : WritingSettings
{
    [CommandArgument(0, "<file>")]
    [Description("Source markdown file whose per-critic sidecars to merge.")]
    public string File { get; set; } = "";

    [CommandOption("--max-alternatives-per-line")]
    [Description("Cap per-line alternatives in the merged file. Drops lowest authorlikeness if more arrive. Default 6.")]
    public int MaxAlternativesPerLine { get; set; } = INaturalnessMerger.DefaultMaxAlternativesPerLine;
}

/// `pks writing naturalness merge <file>` — re-merge per-critic sidecars into
/// the canonical merged file. Useful when a per-critic file was deleted or
/// hand-edited and the canonical needs to be regenerated.
public class NaturalnessMergeCommand : AsyncCommand<NaturalnessMergeSettings>
{
    private readonly INaturalnessMerger _merger;
    private readonly INaturalnessPicksStore _store;
    private readonly IWritingPathResolver _paths;

    public NaturalnessMergeCommand(
        INaturalnessMerger merger,
        INaturalnessPicksStore store,
        IWritingPathResolver paths)
    {
        _merger = merger;
        _store = store;
        _paths = paths;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, NaturalnessMergeSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.File))
        {
            Console.Error.WriteLine("error: file argument required.");
            return 2;
        }
        var full = System.IO.Path.GetFullPath(settings.File);

        var perCritic = await _store.LoadAllPerCriticCandidatesAsync(full);
        var canonicalPath = _paths.NaturalnessCandidatesSidecarPath(full);

        if (perCritic.Count == 0)
        {
            var noneResult = new
            {
                ok = false,
                reason = "no per-critic sidecars found",
                canonicalSidecarPath = canonicalPath,
            };
            Console.WriteLine("RESULT: " + JsonSerializer.Serialize(noneResult,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return 1;
        }

        var path = await _merger.MergeAsync(full, settings.MaxAlternativesPerLine);
        var merged = await _store.LoadCandidatesAsync(full);

        var ok = new
        {
            ok = true,
            critics = perCritic.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList(),
            candidateCount = merged?.Candidates.Count ?? 0,
            canonicalSidecarPath = path,
            maxAlternativesPerLine = settings.MaxAlternativesPerLine,
        };
        Console.WriteLine("RESULT: " + JsonSerializer.Serialize(ok,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }
}
