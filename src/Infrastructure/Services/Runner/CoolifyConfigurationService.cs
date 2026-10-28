using System.Text.Json;

namespace PKS.Infrastructure.Services.Runner;

public class CoolifyInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// What this instance is for: <c>primary</c> (the default, and what an absent field means) or
    /// <c>standby</c> — a box that still holds copies of the applications but is not what deploys
    /// should reach.
    ///
    /// It exists because an application that has been migrated lives on two instances for a while,
    /// under the same name and in the same environment, and the resolver then has two equally good
    /// answers. Marking the old box <c>standby</c> says which one loses that tie, once, on the host
    /// that owns <c>coolify.json</c> — rather than pinning a host in every workflow that deploys.
    ///
    /// A standby is never excluded, only outranked: an application that exists *only* there still
    /// resolves, which is what keeps the not-yet-migrated applications deployable.
    /// </summary>
    public string Role { get; set; } = CoolifyInstanceRoles.Primary;
}

public static class CoolifyInstanceRoles
{
    public const string Primary = "primary";
    public const string Standby = "standby";
}

public class CoolifyConfiguration
{
    public List<CoolifyInstance> Instances { get; set; } = new();
    public DateTime? LastModified { get; set; }
}

public interface ICoolifyConfigurationService
{
    Task<CoolifyConfiguration> LoadAsync();
    Task SaveAsync(CoolifyConfiguration config);
    Task<CoolifyInstance> AddInstanceAsync(string url, string token);
    Task RemoveInstanceAsync(string id);
    Task<List<CoolifyInstance>> ListInstancesAsync();
}

public class CoolifyConfigurationService : ICoolifyConfigurationService
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".pks-cli", "coolify.json");

    private static readonly SemaphoreSlim Lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<CoolifyConfiguration> LoadAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (!File.Exists(ConfigPath))
                return new CoolifyConfiguration();

            var json = await File.ReadAllTextAsync(ConfigPath);
            return JsonSerializer.Deserialize<CoolifyConfiguration>(json, JsonOptions)
                ?? new CoolifyConfiguration();
        }
        finally
        {
            Lock.Release();
        }
    }

    public async Task SaveAsync(CoolifyConfiguration config)
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

    public async Task<CoolifyInstance> AddInstanceAsync(string url, string token)
    {
        var config = await LoadAsync();

        // Remove existing registration for the same URL
        config.Instances.RemoveAll(i =>
            string.Equals(i.Url.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

        var instance = new CoolifyInstance
        {
            Url = url.TrimEnd('/'),
            Token = token
        };
        config.Instances.Add(instance);
        await SaveAsync(config);
        return instance;
    }

    public async Task RemoveInstanceAsync(string id)
    {
        var config = await LoadAsync();
        config.Instances.RemoveAll(i => i.Id == id);
        await SaveAsync(config);
    }

    public async Task<List<CoolifyInstance>> ListInstancesAsync()
    {
        var config = await LoadAsync();
        return config.Instances;
    }
}
