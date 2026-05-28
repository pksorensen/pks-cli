using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public interface INaturalnessPicksStore
{
    Task<NaturalnessCandidatesFile?> LoadCandidatesAsync(string sourceFilePath, CancellationToken ct = default);
    Task SaveCandidatesAsync(string sourceFilePath, NaturalnessCandidatesFile file, CancellationToken ct = default);

    Task<NaturalnessPicksFile?> LoadPicksAsync(string sourceFilePath, CancellationToken ct = default);
    Task SavePicksAsync(string sourceFilePath, NaturalnessPicksFile file, CancellationToken ct = default);
}
