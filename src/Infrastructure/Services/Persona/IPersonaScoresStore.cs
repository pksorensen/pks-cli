using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Infrastructure.Services.Persona;

public interface IPersonaScoresStore
{
    /// <summary>
    /// Reads the persona scores sidecar for the given content + locale.
    /// Returns an empty file model when missing. When <paramref name="modelTag"/>
    /// is non-empty, reads the model-scoped sidecar instead of the shared one.
    /// </summary>
    Task<PersonaScoresFile> LoadAsync(string contentFilePath, string locale, string? modelTag = null, CancellationToken ct = default);

    /// <summary>
    /// Upserts a single score by (PersonaId, Rubric), preserving the rest.
    /// Persists immediately. When <paramref name="modelTag"/> is non-empty,
    /// writes the model-scoped sidecar instead of the shared one.
    /// </summary>
    Task SaveScoreAsync(string contentFilePath, string locale, PersonaScore score, string? modelTag = null, CancellationToken ct = default);
}
