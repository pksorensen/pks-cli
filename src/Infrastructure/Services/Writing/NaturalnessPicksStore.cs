using System.Text.Json;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public sealed class NaturalnessPicksStore : INaturalnessPicksStore
{
    private readonly IWritingPathResolver _paths;

    public NaturalnessPicksStore(IWritingPathResolver paths) { _paths = paths; }

    public async Task<NaturalnessCandidatesFile?> LoadCandidatesAsync(string sourceFilePath, CancellationToken ct = default)
    {
        var p = _paths.NaturalnessCandidatesSidecarPath(sourceFilePath);
        if (!File.Exists(p)) return null;
        var json = await File.ReadAllTextAsync(p, ct);
        return JsonSerializer.Deserialize<NaturalnessCandidatesFile>(json, WritingProfileStore.JsonOptions);
    }

    public async Task SaveCandidatesAsync(string sourceFilePath, NaturalnessCandidatesFile file, CancellationToken ct = default)
    {
        var p = _paths.NaturalnessCandidatesSidecarPath(sourceFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var json = JsonSerializer.Serialize(file, WritingProfileStore.JsonOptions);
        await File.WriteAllTextAsync(p, json, ct);
    }

    public async Task<NaturalnessCandidatesFile?> LoadCandidatesByCriticAsync(string sourceFilePath, string critic, CancellationToken ct = default)
    {
        var p = _paths.NaturalnessCandidatesSidecarPath(sourceFilePath, critic);
        if (!File.Exists(p)) return null;
        var json = await File.ReadAllTextAsync(p, ct);
        return JsonSerializer.Deserialize<NaturalnessCandidatesFile>(json, WritingProfileStore.JsonOptions);
    }

    public async Task SaveCandidatesAsync(string sourceFilePath, string critic, NaturalnessCandidatesFile file, CancellationToken ct = default)
    {
        var p = _paths.NaturalnessCandidatesSidecarPath(sourceFilePath, critic);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var json = JsonSerializer.Serialize(file, WritingProfileStore.JsonOptions);
        await File.WriteAllTextAsync(p, json, ct);
    }

    public async Task<IReadOnlyDictionary<string, NaturalnessCandidatesFile>> LoadAllPerCriticCandidatesAsync(string sourceFilePath, CancellationToken ct = default)
    {
        var result = new Dictionary<string, NaturalnessCandidatesFile>(StringComparer.Ordinal);
        foreach (var (critic, path) in _paths.NaturalnessCandidatesPerCriticPaths(sourceFilePath))
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var parsed = JsonSerializer.Deserialize<NaturalnessCandidatesFile>(json, WritingProfileStore.JsonOptions);
            if (parsed is null) continue;
            result[critic] = parsed;
        }
        return result;
    }

    public async Task<NaturalnessPicksFile?> LoadPicksAsync(string sourceFilePath, CancellationToken ct = default)
    {
        var p = _paths.NaturalnessPicksSidecarPath(sourceFilePath);
        if (!File.Exists(p)) return null;
        var json = await File.ReadAllTextAsync(p, ct);
        return JsonSerializer.Deserialize<NaturalnessPicksFile>(json, WritingProfileStore.JsonOptions);
    }

    public async Task SavePicksAsync(string sourceFilePath, NaturalnessPicksFile file, CancellationToken ct = default)
    {
        var p = _paths.NaturalnessPicksSidecarPath(sourceFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var json = JsonSerializer.Serialize(file, WritingProfileStore.JsonOptions);
        await File.WriteAllTextAsync(p, json, ct);
    }
}
