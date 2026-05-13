using PKS.Infrastructure.Models;

namespace PKS.Infrastructure.Services;

public interface IModelRegistryService
{
    Task<IReadOnlyList<InstalledModel>> ListInstalledAsync(CancellationToken ct = default);
    Task<InstalledModel?> GetAsync(string name, CancellationToken ct = default);
    Task RegisterAsync(InstalledModel model, CancellationToken ct = default);
    Task UnregisterAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<InstalledModel>> ByCapabilityAsync(string capability, CancellationToken ct = default);

    string GetInstallDirectory(string name);

    string RegistryFilePath { get; }
}
