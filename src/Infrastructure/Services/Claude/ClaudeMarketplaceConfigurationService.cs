using System.Text.Json;

namespace PKS.Infrastructure.Services.Claude;

public class ClaudeMarketplaceConfigurationService : IClaudeMarketplaceConfigurationService
{
    private readonly string _configPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ClaudeMarketplaceConfigurationService(string? configPath = null)
    {
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli", "claude-marketplace.json");
    }

    public async Task<ClaudeMarketplaceConfiguration> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await LoadUnlockedAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(ClaudeMarketplaceConfiguration config)
    {
        await _lock.WaitAsync();
        try
        {
            await SaveUnlockedAsync(config);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ClaudeMarketplaceConfiguration> LoadUnlockedAsync()
    {
        try
        {
            if (!File.Exists(_configPath))
                return new ClaudeMarketplaceConfiguration();

            var json = await File.ReadAllTextAsync(_configPath);
            return JsonSerializer.Deserialize<ClaudeMarketplaceConfiguration>(json, JsonOptions)
                ?? new ClaudeMarketplaceConfiguration();
        }
        catch (JsonException)
        {
            return new ClaudeMarketplaceConfiguration();
        }
    }

    private async Task SaveUnlockedAsync(ClaudeMarketplaceConfiguration config)
    {
        config.LastModified = DateTime.UtcNow;
        var dir = Path.GetDirectoryName(_configPath);
        if (dir != null) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(_configPath, json);
    }

    public async Task<ClaudeMarketplace> AddOrUpdateMarketplaceAsync(ClaudeMarketplace marketplace)
    {
        await _lock.WaitAsync();
        try
        {
            var config = await LoadUnlockedAsync();
            config.Marketplaces.RemoveAll(m =>
                string.Equals(m.Id, marketplace.Id, StringComparison.OrdinalIgnoreCase));
            config.Marketplaces.Add(marketplace);
            await SaveUnlockedAsync(config);
            return marketplace;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveMarketplaceAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            var config = await LoadUnlockedAsync();
            config.Marketplaces.RemoveAll(m =>
                string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            await SaveUnlockedAsync(config);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<ClaudeMarketplace>> ListMarketplacesAsync()
    {
        var config = await LoadAsync();
        return config.Marketplaces;
    }

    public async Task<ClaudeMarketplace?> FindMarketplaceAsync(string id)
    {
        var config = await LoadAsync();
        return config.Marketplaces.FirstOrDefault(m =>
            string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
