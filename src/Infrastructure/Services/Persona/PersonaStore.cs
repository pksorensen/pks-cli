namespace PKS.Infrastructure.Services.Persona;

using PersonaModel = PKS.Infrastructure.Services.Persona.Models.Persona;

public sealed class PersonaStore : IPersonaStore
{
    private readonly IPersonaPathResolver _paths;

    public PersonaStore(IPersonaPathResolver paths)
    {
        _paths = paths;
    }

    public async Task<PersonaModel?> LoadFromPathAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;
        var raw = await File.ReadAllTextAsync(path, ct);
        var parsed = FrontmatterParser.Parse(raw);
        var fields = parsed.Fields;

        var id = FrontmatterParser.GetString(fields, "id");
        var name = FrontmatterParser.GetString(fields, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;

        return new PersonaModel
        {
            Id = id!,
            Name = name!,
            Segment = FrontmatterParser.GetString(fields, "segment") ?? "",
            Bucket = FrontmatterParser.GetString(fields, "bucket") ?? "",
            Lang = FrontmatterParser.GetString(fields, "lang") ?? "",
            Body = parsed.Body.TrimEnd(),
            SourcePath = Path.GetFullPath(path),
            Sections = FrontmatterParser.ParseSections(parsed.Body),
        };
    }

    public Task<PersonaModel?> LoadByIdAsync(string personasRoot, string locale, string id, CancellationToken ct = default)
    {
        var p = _paths.PersonaFilePath(personasRoot, locale, id);
        return LoadFromPathAsync(p, ct);
    }

    public async Task<IReadOnlyList<PersonaModel>> ListAsync(string personasRoot, string locale, CancellationToken ct = default)
    {
        var dir = _paths.PersonasLocaleDir(personasRoot, locale);
        if (!Directory.Exists(dir)) return Array.Empty<PersonaModel>();

        var list = new List<PersonaModel>();
        foreach (var slugDir in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var slug = Path.GetFileName(slugDir);
            if (string.IsNullOrEmpty(slug) || slug.StartsWith("_", StringComparison.Ordinal)) continue;
            var file = Path.Combine(slugDir, slug + ".md");
            var p = await LoadFromPathAsync(file, ct);
            if (p is not null) list.Add(p);
        }

        // Sort by bucket priority then id, mirroring the TS lib in
        // src/apps/www-site/src/lib/personas.ts so the CLI + web agree.
        var bucketOrder = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["developer"] = 0,
            ["decision-maker"] = 1,
            ["builder"] = 2,
            ["in-transition"] = 3,
            ["executive"] = 4,
        };
        list.Sort((a, b) =>
        {
            var ai = bucketOrder.TryGetValue(a.Bucket, out var x) ? x : 99;
            var bi = bucketOrder.TryGetValue(b.Bucket, out var y) ? y : 99;
            if (ai != bi) return ai.CompareTo(bi);
            return string.CompareOrdinal(a.Id, b.Id);
        });
        return list;
    }
}
