using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public interface INaturalnessApplier
{
    /// Compute a unified diff + per-pick application plan without touching the file.
    NaturalnessApplyPlan Plan(string postContent, NaturalnessCandidatesFile candidates, NaturalnessPicksFile picks);

    /// Apply the plan in-place to a content string and return the result.
    /// Picks where `Applied == true` are skipped. Picks whose line has drifted
    /// (original text no longer matches) are recorded as `Skipped`.
    NaturalnessApplyResult Apply(string postContent, NaturalnessApplyPlan plan);
}

public sealed class NaturalnessApplyPlan
{
    public List<NaturalnessApplyEdit> Edits { get; init; } = new();
    /// Unified diff in standard `--- a/x\n+++ b/x\n@@ ...` form.
    public string UnifiedDiff { get; init; } = "";
}

public sealed class NaturalnessApplyEdit
{
    public required string CandidateId { get; init; }
    public required int Line { get; init; }
    public required string Original { get; init; }
    public required string Replacement { get; init; }
    /// "A"|"B"|"C"|"other"|"A-opus"|… — what the author picked (verbatim label).
    public required string Chosen { get; init; }
    /// The accepted-example-style trigger summary, persisted to patterns when applied.
    public required string TriggerSummary { get; init; }
    /// Critic name from the chosen alternative's Source. Null when the pick
    /// came from a single-critic (back-compat) file or was a free-form rewrite.
    public string? AcceptedFromCritic { get; init; }
}

public sealed class NaturalnessApplyResult
{
    public string NewContent { get; init; } = "";
    public List<NaturalnessApplyEdit> Applied { get; init; } = new();
    public List<NaturalnessApplyEdit> Skipped { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}
