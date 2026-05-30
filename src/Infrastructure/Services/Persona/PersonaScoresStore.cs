using System.Text.Json;
using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Infrastructure.Services.Persona;

public sealed class PersonaScoresStore : IPersonaScoresStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IPersonaPathResolver _paths;

    public PersonaScoresStore(IPersonaPathResolver paths)
    {
        _paths = paths;
    }

    public async Task<PersonaScoresFile> LoadAsync(string contentFilePath, string locale, string? modelTag = null, CancellationToken ct = default)
    {
        var path = _paths.ScoresSidecarPath(contentFilePath, locale, modelTag);
        if (!File.Exists(path))
        {
            return new PersonaScoresFile
            {
                Post = Path.GetFullPath(contentFilePath),
                UpdatedAt = DateTime.UtcNow,
                Scores = new List<PersonaScore>(),
            };
        }
        var raw = await File.ReadAllTextAsync(path, ct);
        var file = JsonSerializer.Deserialize<PersonaScoresFile>(raw, JsonOpts) ?? new PersonaScoresFile();
        file.Post = string.IsNullOrEmpty(file.Post) ? Path.GetFullPath(contentFilePath) : file.Post;
        file.Scores ??= new List<PersonaScore>();
        return file;
    }

    public async Task SaveScoreAsync(string contentFilePath, string locale, PersonaScore score, string? modelTag = null, CancellationToken ct = default)
    {
        var path = _paths.ScoresSidecarPath(contentFilePath, locale, modelTag);
        var file = await LoadAsync(contentFilePath, locale, modelTag, ct);

        // Upsert by (personaId, rubric).
        var idx = file.Scores.FindIndex(s =>
            string.Equals(s.PersonaId, score.PersonaId, StringComparison.Ordinal) &&
            string.Equals(s.Rubric, score.Rubric, StringComparison.Ordinal));
        if (idx >= 0) file.Scores[idx] = score;
        else file.Scores.Add(score);

        // Stable sort: persona id, then rubric.
        file.Scores.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.PersonaId, b.PersonaId);
            return c != 0 ? c : string.CompareOrdinal(a.Rubric, b.Rubric);
        });
        file.UpdatedAt = DateTime.UtcNow;
        file.Post = Path.GetFullPath(contentFilePath);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file, JsonOpts), ct);
    }
}
