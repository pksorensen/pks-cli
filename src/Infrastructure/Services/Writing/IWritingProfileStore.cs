using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// Reads/writes the writer profile, anglicism list, allowlist, channel config,
/// and per-source report cache. Mirrors the contract of [[IBrainIndexStore]].
public interface IWritingProfileStore
{
    /// Creates ~/.pks-cli/writing/ + seed files if missing. Idempotent.
    Task EnsureGlobalLayoutAsync(CancellationToken ct = default);

    /// Creates <repo>/.pks/writing/ + adds it to .gitignore. No-op when projectRoot is null.
    Task EnsureProjectLayoutAsync(string? projectRoot, CancellationToken ct = default);

    Task<string?> LoadProfileAsync(CancellationToken ct = default);

    /// Returns global entries merged with per-project overrides (project wins on
    /// duplicate `english` key). projectRoot may be null.
    Task<IReadOnlyList<AnglicismEntry>> LoadAnglicismsAsync(
        string? projectRoot, CancellationToken ct = default);

    Task<IReadOnlySet<string>> LoadAllowlistAsync(CancellationToken ct = default);

    /// Calques (loan-translations) merged from global + per-project overrides.
    Task<IReadOnlyList<CalqueEntry>> LoadCalquesAsync(
        string? projectRoot, CancellationToken ct = default);

    Task AddAnglicismAsync(AnglicismEntry entry, CancellationToken ct = default);
    Task AddAllowedTermAsync(string term, CancellationToken ct = default);
    Task AddCalqueAsync(CalqueEntry entry, CancellationToken ct = default);

    /// Appends a single dated lesson to ~/.pks-cli/writing/lessons.md.
    /// Used by `pks writing learn` when accepting a non-terminology finding.
    Task AppendLessonAsync(string dimension, string lesson, string sourcePath, CancellationToken ct = default);

    Task<ChannelConfig> LoadChannelConfigAsync(
        string? projectRoot, CancellationToken ct = default);

    /// Reads the channel's rubric (~/.pks-cli/writing/channels/<channel>.md).
    /// Returns null when no rubric is configured for that channel.
    Task<string?> LoadChannelRubricAsync(string channel, CancellationToken ct = default);

    /// Reads every reference sample (`*.md`) under
    /// ~/.pks-cli/writing/reference/&lt;channel&gt;/ in stable order.
    /// These become few-shot examples for the LLM critic.
    Task<IReadOnlyList<ReferenceSample>> LoadReferenceSamplesAsync(
        string channel, CancellationToken ct = default);

    Task<WritingReport?> LoadReportAsync(
        string sourceFilePath, CancellationToken ct = default);

    Task SaveReportAsync(
        string sourceFilePath, WritingReport report, CancellationToken ct = default);

    /// Removes the sidecar report files next to a source (no-op when absent).
    /// Used to keep post folders clean when a re-run has nothing to report.
    Task DeleteReportSidecarsAsync(string sourceFilePath, CancellationToken ct = default);
}
