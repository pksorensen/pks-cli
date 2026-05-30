using System.Linq;

namespace PKS.Infrastructure.Services.Persona;

public sealed class PersonaPathResolver : IPersonaPathResolver
{
    public string? ResolvePersonasRoot(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        var dir = new DirectoryInfo(Path.GetFullPath(cwd));
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "personas");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public string PersonasLocaleDir(string personasRoot, string locale) =>
        Path.Combine(personasRoot, locale);

    public string PersonaFilePath(string personasRoot, string locale, string slug) =>
        Path.Combine(PersonasLocaleDir(personasRoot, locale), slug, $"{slug}.md");

    public string RubricsDir(string personasRoot) =>
        Path.Combine(personasRoot, "_rubrics");

    public string RubricFilePath(string personasRoot, string rubricId) =>
        Path.Combine(RubricsDir(personasRoot), $"{rubricId}.md");

    public string ReviewDir(string contentFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(contentFilePath))
                  ?? throw new ArgumentException("content must have a directory", nameof(contentFilePath));
        return Path.Combine(dir, "_review");
    }

    public string ScoresSidecarPath(string contentFilePath, string locale, string? modelTag = null)
    {
        var fileName = string.IsNullOrWhiteSpace(modelTag)
            ? $"{locale}.PERSONA-SCORES.json"
            : $"{locale}.PERSONA-SCORES.{ModelTagSlug(modelTag)}.json";
        return Path.Combine(ReviewDir(contentFilePath), fileName);
    }

    /// <summary>
    /// Filename-safe slug for a model id, used to scope a sidecar to one
    /// scoring model. Lowercases, replaces every non-alphanumeric run with a
    /// single dash, and trims dashes. e.g. <c>gpt-5.5</c> → <c>gpt-5-5</c>,
    /// <c>claude-opus-4-8</c> → <c>claude-opus-4-8</c>.
    /// </summary>
    public static string ModelTagSlug(string model)
    {
        var slug = new string((model ?? "").Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
