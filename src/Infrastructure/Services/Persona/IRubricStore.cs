using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Infrastructure.Services.Persona;

public interface IRubricStore
{
    Task<Rubric?> LoadAsync(string personasRoot, string rubricId, CancellationToken ct = default);
    Task<IReadOnlyList<Rubric>> ListAsync(string personasRoot, CancellationToken ct = default);
}
