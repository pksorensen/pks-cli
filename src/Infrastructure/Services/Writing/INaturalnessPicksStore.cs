using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public interface INaturalnessPicksStore
{
    Task<NaturalnessCandidatesFile?> LoadCandidatesAsync(string sourceFilePath, CancellationToken ct = default);
    Task SaveCandidatesAsync(string sourceFilePath, NaturalnessCandidatesFile file, CancellationToken ct = default);

    /// Load the per-critic sidecar for `critic`, or null if absent.
    Task<NaturalnessCandidatesFile?> LoadCandidatesByCriticAsync(string sourceFilePath, string critic, CancellationToken ct = default);
    /// Save the per-critic sidecar for `critic`.
    Task SaveCandidatesAsync(string sourceFilePath, string critic, NaturalnessCandidatesFile file, CancellationToken ct = default);
    /// Load every per-critic sidecar present in `_review/`, keyed by critic name.
    Task<IReadOnlyDictionary<string, NaturalnessCandidatesFile>> LoadAllPerCriticCandidatesAsync(string sourceFilePath, CancellationToken ct = default);

    Task<NaturalnessPicksFile?> LoadPicksAsync(string sourceFilePath, CancellationToken ct = default);
    Task SavePicksAsync(string sourceFilePath, NaturalnessPicksFile file, CancellationToken ct = default);
}
