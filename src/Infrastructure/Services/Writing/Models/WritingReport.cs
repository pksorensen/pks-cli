namespace PKS.Infrastructure.Services.Writing.Models;

public sealed class WritingReport
{
    public string SourcePath { get; set; } = "";
    public string Channel { get; set; } = "blog";
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    /// Overall 0-100 score (average of dimension scores × 20).
    /// Null when this report is lint-only (no critic).
    public int? Score { get; set; }

    /// Per-dimension 1-5 scores from the critic (Tone, Naturalness, …).
    /// Empty on lint-only reports. Inspired by Julian Bent Singh's BGA rubric —
    /// granular dimensions are more actionable than a single number.
    public Dictionary<string, int> DimensionScores { get; set; } = new();

    public List<WritingFinding> Findings { get; set; } = new();

    /// Free-form prose from the LLM critic (null on lint-only reports).
    public string? CriticNotes { get; set; }

    /// Model id that produced the critic output (e.g. "claude-haiku-4-5-20251001").
    public string? CriticModel { get; set; }
}

/// Standard rubric dimensions. Channel rubrics may extend this list with extras.
public static class WritingDimensions
{
    public const string Naturalness = "Naturalness";   // does it read as native Danish, not translated?
    public const string Tone        = "Tone";          // matches profile voice?
    public const string Terminology = "Terminology";   // anglicisms, on-brand vocabulary
    public const string Hook        = "Hook";          // first sentence earns the read
    public const string Value       = "Value";         // delivers what headline promises

    public static readonly IReadOnlyList<string> All = new[]
    {
        Naturalness, Tone, Terminology, Hook, Value,
    };
}

public sealed class WritingFinding
{
    public string RuleId { get; set; } = "";
    public WritingSeverity Severity { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public string Match { get; set; } = "";
    public string Message { get; set; } = "";
    public List<string> Suggestions { get; set; } = new();
}

public enum WritingSeverity
{
    Suggestion,
    Warning,
    Error,
}
