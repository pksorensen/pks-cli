using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Infrastructure.Services.Persona;

public interface IPersonaLinter
{
    /// <summary>
    /// Validates a single persona file: frontmatter shape, required body
    /// sections per locale, bullet counts, card-variant disk presence.
    /// Pure-ish (touches disk only for card-asset checks).
    /// </summary>
    Task<PersonaLintResult> LintAsync(string personaPath, CancellationToken ct = default);
}
