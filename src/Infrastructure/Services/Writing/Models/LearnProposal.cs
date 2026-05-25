namespace PKS.Infrastructure.Services.Writing.Models;

/// Agent-friendly review artifact produced by `pks writing learn`.
/// The agent reads the .md, optionally edits `accept` flags in the .json,
/// then runs `pks writing apply` to commit the changes to the profile.
public sealed class LearnProposal
{
    public int Version { get; set; } = 1;
    public string SourcePath { get; set; } = "";
    public string Channel { get; set; } = "";
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    /// Snapshot of the dimension scores from the report this proposal is based on.
    public Dictionary<string, int> DimensionScores { get; set; } = new();

    public List<LearnAction> Actions { get; set; } = new();
}

public enum LearnActionKind
{
    /// Allowlist a term so it's never flagged again. For tech terms the
    /// author uses intentionally.
    Allowlist,
    /// Add an entry to the anglicism list so future posts flag it.
    Anglicism,
    /// Append a non-terminology insight to lessons.md (Tone/Hook/Value/Naturalness).
    Lesson,
}

public sealed class LearnAction
{
    public LearnActionKind Kind { get; set; }

    /// Whether `apply` will execute this action. Defaults to a sensible value
    /// per heuristic; the agent flips this to override.
    public bool Accept { get; set; }

    /// Human/agent-readable explanation of why this action was proposed.
    public string Rationale { get; set; } = "";

    /// Lines in the source where the underlying findings occurred (for context).
    public List<int> EvidenceLines { get; set; } = new();

    // ── Allowlist / Anglicism payload ──────────────────────────────────────────
    /// The English term (anglicism) or allowlist entry.
    public string? Term { get; set; }
    /// Danish alternatives, for Anglicism actions only.
    public List<string> DanishAlternatives { get; set; } = new();
    /// Note from the original finding (Anglicism actions).
    public string? Note { get; set; }

    // ── Lesson payload ─────────────────────────────────────────────────────────
    public string? Dimension { get; set; }
    public string? Lesson { get; set; }
}

public sealed class ApplyResult
{
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public int AnglicismsAdded { get; set; }
    public int AllowlistAdded { get; set; }
    public int LessonsAppended { get; set; }
    public List<string> Warnings { get; set; } = new();
}
