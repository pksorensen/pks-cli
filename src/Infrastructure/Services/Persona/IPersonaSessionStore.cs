using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Infrastructure.Services.Persona;

public interface IPersonaSessionStore
{
    /// <summary>
    /// Reads the session sidecar for the given content + locale. Returns an
    /// empty file model when missing.
    /// </summary>
    Task<PersonaSessionFile> LoadAsync(string contentFilePath, string locale, string? modelTag = null, CancellationToken ct = default);

    /// <summary>
    /// Appends one call to the session log and rolls the running totals.
    /// Persists immediately so a killed batch keeps whatever it already
    /// spent, not just the calls that made it into the final summary.
    /// </summary>
    Task AppendCallAsync(string contentFilePath, string locale, PersonaSessionCall call, string? modelTag = null, CancellationToken ct = default);
}
