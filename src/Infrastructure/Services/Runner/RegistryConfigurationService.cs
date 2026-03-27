using System.Text.Json;

namespace PKS.Infrastructure.Services.Runner;

public class RegistryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Hostname { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
}

public class RegistryConfiguration
{
    public List<RegistryEntry> Registries { get; set; } = new();
    public DateTime? LastModified { get; set; }
}

public interface IRegistryConfigurationService
{
    Task<RegistryEntry?> GetByHostnameAsync(string hostname);
    Task<List<RegistryEntry>> ListAsync();
    Task<RegistryEntry> AddAsync(string hostname, string username, string password);
    Task RemoveAsync(string hostname);
}

public class RegistryConfigurationService : IRegistryConfigurationService
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".pks-cli", "registries.json");

    private static readonly SemaphoreSlim Lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private async Task<RegistryConfiguration> LoadAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (!File.Exists(ConfigPath))
                return new RegistryConfiguration();

            var json = await File.ReadAllTextAsync(ConfigPath);
            return JsonSerializer.Deserialize<RegistryConfiguration>(json, JsonOptions)
                ?? new RegistryConfiguration();
        }
        finally
        {
            Lock.Release();
        }
    }

    private async Task SaveAsync(RegistryConfiguration config)
    {
        await Lock.WaitAsync();
        try
        {
            config.LastModified = DateTime.UtcNow;
            var dir = Path.GetDirectoryName(ConfigPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            await File.WriteAllTextAsync(ConfigPath, json);
        }
        finally
        {
            Lock.Release();
        }
    }

    public async Task<RegistryEntry?> GetByHostnameAsync(string hostname)
    {
        var config = await LoadAsync();
        return config.Registries.FirstOrDefault(r =>
            string.Equals(r.Hostname, hostname, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<RegistryEntry>> ListAsync()
    {
        var config = await LoadAsync();
        return config.Registries;
    }

    public async Task<RegistryEntry> AddAsync(string hostname, string username, string password)
    {
        var config = await LoadAsync();

        // Remove existing registration for same hostname
        config.Registries.RemoveAll(r =>
            string.Equals(r.Hostname, hostname, StringComparison.OrdinalIgnoreCase));

        var entry = new RegistryEntry
        {
            Hostname = hostname,
            Username = username,
            Password = password
        };
        config.Registries.Add(entry);
        await SaveAsync(config);
        return entry;
    }

    public async Task RemoveAsync(string hostname)
    {
        var config = await LoadAsync();
        config.Registries.RemoveAll(r =>
            string.Equals(r.Hostname, hostname, StringComparison.OrdinalIgnoreCase));
        await SaveAsync(config);
    }
}
