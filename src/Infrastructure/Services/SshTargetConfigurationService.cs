using System.Text.Json;

namespace PKS.Infrastructure.Services;

public class SshTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    public int Port { get; set; } = 22;
    public string KeyPath { get; set; } = "";
    public string? Label { get; set; }
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
}

public class SshTargetConfiguration
{
    public List<SshTarget> Targets { get; set; } = new();
    public DateTime? LastModified { get; set; }
}

public interface ISshTargetConfigurationService
{
    Task<SshTargetConfiguration> LoadAsync();
    Task SaveAsync(SshTargetConfiguration config);
    Task<SshTarget> AddTargetAsync(string host, string username, int port, string keyPath, string? label);
    Task RemoveTargetAsync(string id);
    Task<List<SshTarget>> ListTargetsAsync();
    Task<SshTarget?> FindTargetAsync(string hostOrLabel);
}

public class SshTargetConfigurationService : ISshTargetConfigurationService
{
    private readonly string _configPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SshTargetConfigurationService(string? configPath = null)
    {
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli", "ssh-targets.json");
    }

    public async Task<SshTargetConfiguration> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_configPath))
                return new SshTargetConfiguration();

            var json = await File.ReadAllTextAsync(_configPath);
            return JsonSerializer.Deserialize<SshTargetConfiguration>(json, JsonOptions)
                ?? new SshTargetConfiguration();
        }
        catch (JsonException)
        {
            return new SshTargetConfiguration();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(SshTargetConfiguration config)
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

    public async Task<SshTarget> AddTargetAsync(string host, string username, int port, string keyPath, string? label)
    {
        var config = await LoadAsync();

        // Remove existing registration for the same host + username + port
        config.Targets.RemoveAll(t =>
            string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Username, username, StringComparison.OrdinalIgnoreCase) &&
            t.Port == port);

        var target = new SshTarget
        {
            Host = host,
            Username = username,
            Port = port,
            KeyPath = keyPath,
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

    public async Task<List<SshTarget>> ListTargetsAsync()
    {
        var config = await LoadAsync();
        return config.Targets;
    }

    public async Task<SshTarget?> FindTargetAsync(string hostOrLabel)
    {
        var config = await LoadAsync();

        // 1. Exact host match (case-insensitive)
        var match = config.Targets.FirstOrDefault(t =>
            string.Equals(t.Host, hostOrLabel, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // 2. Exact label match (case-insensitive)
        match = config.Targets.FirstOrDefault(t =>
            t.Label != null && string.Equals(t.Label, hostOrLabel, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // 3. Match "user@host" format
        var atIndex = hostOrLabel.IndexOf('@');
        if (atIndex > 0 && atIndex < hostOrLabel.Length - 1)
        {
            var user = hostOrLabel[..atIndex];
            var host = hostOrLabel[(atIndex + 1)..];
            match = config.Targets.FirstOrDefault(t =>
                string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Username, user, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return null;
    }
}
