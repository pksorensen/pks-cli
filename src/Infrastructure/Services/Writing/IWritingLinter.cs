using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public interface IWritingLinter
{
    /// Scans the given markdown content for known anglicisms (per the merged
    /// global + project list) and returns line-anchored findings. Allowlisted
    /// terms are never reported. Pure function — no I/O beyond what the store
    /// already loaded.
    Task<IReadOnlyList<WritingFinding>> LintAsync(
        string content,
        IReadOnlyList<AnglicismEntry> anglicisms,
        IReadOnlySet<string> allowlist,
        CancellationToken ct = default);
}
