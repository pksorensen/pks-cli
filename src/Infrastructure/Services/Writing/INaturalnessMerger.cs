using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// Merges per-critic naturalness candidate sidecars into a single canonical
/// `<stem>.NATURALNESS-CANDIDATES.json` next to the source. Group key is
/// `line` number — multiple critics flagging the same line collapse into one
/// merged candidate; disjoint lines are union'd.
public interface INaturalnessMerger
{
    /// Default cap on alternatives per merged candidate. With 6 critics × 3 alts
    /// it could grow unboundedly; we drop the lowest-`authorlikeness` ones.
    public const int DefaultMaxAlternativesPerLine = 6;

    /// Merge in-memory: takes the per-critic files keyed by critic name and
    /// produces a merged canonical file. Pure function; no I/O.
    NaturalnessCandidatesFile Merge(
        string sourceFilePath,
        IReadOnlyDictionary<string, NaturalnessCandidatesFile> perCritic,
        int maxAlternativesPerLine = DefaultMaxAlternativesPerLine,
        IList<string>? debugLog = null);

    /// Loads every per-critic sidecar for `sourceFilePath`, merges them,
    /// writes the canonical merged file, and returns its path. If no per-critic
    /// sidecars exist, deletes the canonical file (if any) and returns its path
    /// anyway for diagnostics.
    Task<string> MergeAsync(
        string sourceFilePath,
        int maxAlternativesPerLine = DefaultMaxAlternativesPerLine,
        CancellationToken ct = default);
}
