namespace PKS.Infrastructure.Services.Writing.Models;

/// One author choice from the interactive review loop. Persisted by
/// `pks writing naturalness review` to `_review/<stem>.NATURALNESS-PICKS.json`.
public sealed class NaturalnessPick
{
    public string CandidateId { get; set; } = "";
    /// "A" | "B" | "C" | "skip" | "other"
    public string Chosen { get; set; } = "";
    /// When Chosen == "other", the author's free-form replacement.
    public string? CustomText { get; set; }
    /// Idempotency flag — `apply` flips this to true after edits land.
    public bool Applied { get; set; }
}

public sealed class NaturalnessPicksFile
{
    public string Post { get; set; } = "";
    public DateTime ReviewedAt { get; set; } = DateTime.UtcNow;
    public List<NaturalnessPick> Picks { get; set; } = new();
}

public sealed class NaturalnessCandidatesFile
{
    public string Post { get; set; } = "";
    public string? CriticModel { get; set; }
    public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;
    public List<NaturalnessCandidate> Candidates { get; set; } = new();

    // ── merged (canonical) extension fields ─────────────────────────────────
    /// Critic names contributing to this merged file. Null on per-critic sidecars.
    public List<string>? Critics { get; set; }
    /// When the canonical file was last merged. Null on per-critic sidecars.
    public DateTime? MergedAt { get; set; }
}
