using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// Append-only markdown store at ~/.pks-cli/writing/naturalness-patterns.md.
/// Read by [[NaturalnessPromptBuilder]] (few-shot injection) and written by
/// `pks writing naturalness apply` when a pick lands successfully.
public interface INaturalnessPatternStore
{
    Task<IReadOnlyList<NaturalnessPattern>> LoadAllAsync(CancellationToken ct = default);

    /// Append-or-bump: if a pattern with the same trigger_summary already
    /// exists, bump its accepted_count; otherwise append a fresh entry.
    Task UpsertAsync(NaturalnessPattern pattern, CancellationToken ct = default);

    /// Raw markdown content for `pks writing naturalness patterns show`.
    Task<string> RenderMarkdownAsync(CancellationToken ct = default);
}
