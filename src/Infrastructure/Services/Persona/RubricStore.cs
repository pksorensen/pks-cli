using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Infrastructure.Services.Persona;

public sealed class RubricStore : IRubricStore
{
    private readonly IPersonaPathResolver _paths;

    public RubricStore(IPersonaPathResolver paths)
    {
        _paths = paths;
    }

    public async Task<Rubric?> LoadAsync(string personasRoot, string rubricId, CancellationToken ct = default)
    {
        var path = _paths.RubricFilePath(personasRoot, rubricId);
        if (!File.Exists(path)) return null;
        var raw = await File.ReadAllTextAsync(path, ct);
        var parsed = FrontmatterParser.Parse(raw);
        var id = FrontmatterParser.GetString(parsed.Fields, "id") ?? rubricId;
        var name = FrontmatterParser.GetString(parsed.Fields, "name") ?? rubricId;
        var subscores = FrontmatterParser.GetStringList(parsed.Fields, "subscores") ?? new List<string>();
        return new Rubric
        {
            Id = id,
            Name = name,
            Body = parsed.Body.TrimEnd(),
            SourcePath = Path.GetFullPath(path),
            Subscores = subscores,
        };
    }

    public async Task<IReadOnlyList<Rubric>> ListAsync(string personasRoot, CancellationToken ct = default)
    {
        var dir = _paths.RubricsDir(personasRoot);
        if (!Directory.Exists(dir)) return Array.Empty<Rubric>();
        var list = new List<Rubric>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.md").OrderBy(p => p, StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var rubric = await LoadAsync(personasRoot, id, ct);
            if (rubric is not null) list.Add(rubric);
        }
        return list;
    }
}
