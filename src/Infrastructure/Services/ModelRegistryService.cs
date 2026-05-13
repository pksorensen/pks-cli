using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Models;

namespace PKS.Infrastructure.Services;

public class ModelRegistry
{
    [JsonPropertyName("models")]       public List<InstalledModel> Models { get; set; } = new();
    [JsonPropertyName("lastModified")] public DateTime? LastModified { get; set; }
}

public class ModelRegistryService : IModelRegistryService
{
    private readonly string _registryPath;
    private readonly string _installRoot;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ModelRegistryService(string? registryPath = null, string? installRoot = null)
    {
        var pksDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli");
        _registryPath = registryPath ?? Path.Combine(pksDir, "models.json");
        _installRoot = installRoot ?? Path.Combine(pksDir, "models");
    }

    public string RegistryFilePath => _registryPath;

    public string GetInstallDirectory(string name) =>
        Path.Combine(_installRoot, name);

    public async Task<IReadOnlyList<InstalledModel>> ListInstalledAsync(CancellationToken ct = default)
    {
        var registry = await LoadAsync(ct).ConfigureAwait(false);
        return registry.Models;
    }

    public async Task<InstalledModel?> GetAsync(string name, CancellationToken ct = default)
    {
        var registry = await LoadAsync(ct).ConfigureAwait(false);
        return registry.Models.FirstOrDefault(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task RegisterAsync(InstalledModel model, CancellationToken ct = default)
    {
        var registry = await LoadAsync(ct).ConfigureAwait(false);
        registry.Models.RemoveAll(m =>
            string.Equals(m.Name, model.Name, StringComparison.OrdinalIgnoreCase));
        registry.Models.Add(model);
        await SaveAsync(registry, ct).ConfigureAwait(false);
    }

    public async Task UnregisterAsync(string name, CancellationToken ct = default)
    {
        var registry = await LoadAsync(ct).ConfigureAwait(false);
        registry.Models.RemoveAll(m =>
            string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        await SaveAsync(registry, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InstalledModel>> ByCapabilityAsync(string capability, CancellationToken ct = default)
    {
        var registry = await LoadAsync(ct).ConfigureAwait(false);
        return registry.Models
            .Where(m => m.Capabilities.Any(c =>
                string.Equals(c, capability, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private async Task<ModelRegistry> LoadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_registryPath))
                return new ModelRegistry();

            var json = await File.ReadAllTextAsync(_registryPath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ModelRegistry>(json, JsonOptions)
                ?? new ModelRegistry();
        }
        catch (JsonException)
        {
            return new ModelRegistry();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveAsync(ModelRegistry registry, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            registry.LastModified = DateTime.UtcNow;
            var dir = Path.GetDirectoryName(_registryPath);
            if (dir != null) Directory.CreateDirectory(dir);

            var tmpPath = _registryPath + ".tmp";
            var json = JsonSerializer.Serialize(registry, JsonOptions);
            await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);
            File.Move(tmpPath, _registryPath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }
}
