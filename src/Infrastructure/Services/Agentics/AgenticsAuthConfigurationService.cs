using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Agentics;

/// <summary>
/// JSON-file persistence for `pks agentics init` credentials.
/// Mirrors AgenticsRunnerConfigurationService — same SemaphoreSlim guard,
/// same ~/.pks-cli/ directory.
/// </summary>
public class AgenticsAuthConfigurationService : IAgenticsAuthConfigurationService
{
    private readonly string _configFilePath;
    private readonly ILogger<AgenticsAuthConfigurationService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AgenticsAuthConfigurationService(ILogger<AgenticsAuthConfigurationService> logger)
        : this(logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli",
            "agentics-auth.json"))
    {
    }

    public AgenticsAuthConfigurationService(ILogger<AgenticsAuthConfigurationService> logger, string configFilePath)
    {
        _logger = logger;
        _configFilePath = configFilePath;
    }

    public async Task<AgenticsAuthCredentials?> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_configFilePath)) return null;
            var json = await File.ReadAllTextAsync(_configFilePath);
            return JsonSerializer.Deserialize<AgenticsAuthCredentials>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize agentics auth from {Path}", _configFilePath);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AgenticsAuthCredentials credentials)
    {
        await _lock.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            credentials.SavedAt = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(credentials, JsonOptions);
            await File.WriteAllTextAsync(_configFilePath, json);

            // Restrict permissions on Unix — token file is sensitive.
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(_configFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { /* best effort */ }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (File.Exists(_configFilePath)) File.Delete(_configFilePath);
        }
        finally
        {
            _lock.Release();
        }
    }
}
