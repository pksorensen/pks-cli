using System.Text.Json;

namespace PKS.Infrastructure.Services;

public class RsyncTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    public int Port { get; set; } = 22;
    public string? KeyPath { get; set; }
    public string RemotePath { get; set; } = "";
    public string? Label { get; set; }
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
}

public class RsyncTargetConfiguration
{
    public List<RsyncTarget> Targets { get; set; } = new();
    public DateTime? LastModified { get; set; }
}

public interface IRsyncTargetConfigurationService
{
    Task<RsyncTargetConfiguration> LoadAsync();
    Task SaveAsync(RsyncTargetConfiguration config);
    Task<RsyncTarget> AddTargetAsync(string host, string username, int port, string? keyPath, string remotePath, string? label);
    Task RemoveTargetAsync(string id);
    Task<List<RsyncTarget>> ListTargetsAsync();
    Task<RsyncTarget?> FindTargetAsync(string hostOrLabel);
}

public class RsyncTargetConfigurationService : IRsyncTargetConfigurationService
{
    private readonly string _configPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public RsyncTargetConfigurationService(string? configPath = null)
    {
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli", "rsync-targets.json");
    }

    public async Task<RsyncTargetConfiguration> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_configPath))
                return new RsyncTargetConfiguration();

            var json = await File.ReadAllTextAsync(_configPath);
            return JsonSerializer.Deserialize<RsyncTargetConfiguration>(json, JsonOptions)
                ?? new RsyncTargetConfiguration();
        }
        catch (JsonException)
        {
            return new RsyncTargetConfiguration();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(RsyncTargetConfiguration config)
    {
        await _lock.WaitAsync();
        try
        {
            config.LastModified = DateTime.UtcNow;
            var dir = Path.GetDirectoryName(_configPath);
            if (dir != null) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            await File.WriteAllTextAsync(_configPath, json);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<RsyncTarget> AddTargetAsync(string host, string username, int port, string? keyPath, string remotePath, string? label)
    {
        var config = await LoadAsync();

        config.Targets.RemoveAll(t =>
            string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Username, username, StringComparison.OrdinalIgnoreCase) &&
            t.Port == port);

        var target = new RsyncTarget
        {
            Host = host,
            Username = username,
            Port = port,
            KeyPath = keyPath,
            RemotePath = remotePath,
            Label = label
        };
        config.Targets.Add(target);
        await SaveAsync(config);
        return target;
    }

    public async Task RemoveTargetAsync(string id)
    {
        var config = await LoadAsync();
        config.Targets.RemoveAll(t => t.Id == id);
        await SaveAsync(config);
    }

    public async Task<List<RsyncTarget>> ListTargetsAsync()
    {
        var config = await LoadAsync();
        return config.Targets;
    }

    public async Task<RsyncTarget?> FindTargetAsync(string hostOrLabel)
    {
        var config = await LoadAsync();

        var match = config.Targets.FirstOrDefault(t =>
            string.Equals(t.Host, hostOrLabel, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        match = config.Targets.FirstOrDefault(t =>
            t.Label != null && string.Equals(t.Label, hostOrLabel, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        var atIndex = hostOrLabel.IndexOf('@');
        if (atIndex > 0 && atIndex < hostOrLabel.Length - 1)
        {
            var user = hostOrLabel[..atIndex];
            var host = hostOrLabel[(atIndex + 1)..];
            match = config.Targets.FirstOrDefault(t =>
                string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Username, user, StringComparison.OrdinalIgnoreCase));
        }

        return match;
    }
}
