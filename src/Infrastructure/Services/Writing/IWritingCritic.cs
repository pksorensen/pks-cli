using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// LLM-based critic that scores a piece of writing along multiple dimensions
/// (Naturalness, Tone, Terminology, Hook, Value) and returns per-line findings.
///
/// Design notes (from the BGA pattern — Julian Bent Singh, 2024):
///   - Pattern-matching against a rubric, not deliberation → use a fast
///     non-reasoning model (Haiku) rather than an extended-thinking model.
///   - Reference corpus injected as few-shot via the system prompt so prompt
///     caching kicks in on repeated runs.
public interface IWritingCritic
{
    Task<CritiqueResult> CritiqueAsync(CritiqueRequest request, CancellationToken ct = default);
}

public sealed class CritiqueRequest
{
    public required string SourcePath { get; init; }
    public required string Content { get; init; }
    public required string Channel { get; init; }

    /// Resolved writer profile (~/.pks-cli/writing/profile.md).
    public string? Profile { get; init; }

    /// Channel rubric (~/.pks-cli/writing/channels/<channel>.md).
    public string? ChannelRubric { get; init; }

    public IReadOnlyList<ReferenceSample> References { get; init; } = Array.Empty<ReferenceSample>();
    public IReadOnlyList<AnglicismEntry> Anglicisms { get; init; } = Array.Empty<AnglicismEntry>();
    public IReadOnlySet<string> Allowlist { get; init; } = new HashSet<string>();

    /// "haiku" / "sonnet" / "opus" / explicit model id. Defaults to "haiku".
    public string Model { get; init; } = "haiku";

    /// Hard dollar cap. Defaults to $0.50 per critique.
    public double MaxBudgetUsd { get; init; } = 0.50;
}

public sealed class CritiqueResult
{
    public required bool Success { get; init; }
    public required string? ErrorKind { get; init; }
    public required string? ErrorMessage { get; init; }

    public Dictionary<string, int> DimensionScores { get; init; } = new();
    public List<WritingFinding> Findings { get; init; } = new();
    public string? Notes { get; init; }

    public string? Model { get; init; }
    public double CostUsd { get; init; }
    public TimeSpan Duration { get; init; }
}
