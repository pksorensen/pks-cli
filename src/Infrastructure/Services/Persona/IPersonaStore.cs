namespace PKS.Infrastructure.Services.Persona;

// Disambiguate: `Persona` is also the namespace. Always fully-qualify the
// model class via Models.Persona inside this namespace.
using PersonaModel = PKS.Infrastructure.Services.Persona.Models.Persona;

public interface IPersonaStore
{
    /// <summary>
    /// Loads the persona by absolute file path. Returns null when the file
    /// doesn't exist or the frontmatter can't be parsed cleanly.
    /// </summary>
    Task<PersonaModel?> LoadFromPathAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Loads a persona by id, walking <paramref name="personasRoot"/>
    /// /<paramref name="locale"/>/<c>&lt;id&gt;/&lt;id&gt;.md</c>.
    /// </summary>
    Task<PersonaModel?> LoadByIdAsync(string personasRoot, string locale, string id, CancellationToken ct = default);

    /// <summary>
    /// Enumerates every persona in the locale, sorted by bucket priority
    /// then id. Skips folders starting with <c>_</c> (theme assets).
    /// </summary>
    Task<IReadOnlyList<PersonaModel>> ListAsync(string personasRoot, string locale, CancellationToken ct = default);
}
