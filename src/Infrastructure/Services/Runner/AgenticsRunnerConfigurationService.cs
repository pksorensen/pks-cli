using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Manages agentics runner configuration persisted as JSON at ~/.pks-cli/agentics-runners.json.
/// Thread-safe via SemaphoreSlim.
/// </summary>
public class AgenticsRunnerConfigurationService : IAgenticsRunnerConfigurationService
{
    private readonly string _configFilePath;
    private readonly ILogger<AgenticsRunnerConfigurationService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates a new AgenticsRunnerConfigurationService with the default config path (~/.pks-cli/agentics-runners.json)
    /// </summary>
    public AgenticsRunnerConfigurationService(ILogger<AgenticsRunnerConfigurationService> logger)
        : this(logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli",
            "agentics-runners.json"))
    {
    }

    /// <summary>
    /// Creates a new AgenticsRunnerConfigurationService with a custom config file path (useful for testing)
    /// </summary>
    public AgenticsRunnerConfigurationService(ILogger<AgenticsRunnerConfigurationService> logger, string configFilePath)
    {
        _logger = logger;
        _configFilePath = configFilePath;
    }

    public async Task<AgenticsRunnerConfiguration> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_configFilePath))
            {
                _logger.LogDebug("Configuration file not found at {Path}, returning defaults", _configFilePath);
                return new AgenticsRunnerConfiguration();
            }

            var json = await File.ReadAllTextAsync(_configFilePath);
            var config = JsonSerializer.Deserialize<AgenticsRunnerConfiguration>(json, JsonOptions);

            _logger.LogDebug("Loaded configuration with {Count} registrations from {Path}",
                config?.Registrations.Count ?? 0, _configFilePath);

            return config ?? new AgenticsRunnerConfiguration();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize configuration from {Path}, returning defaults", _configFilePath);
            return new AgenticsRunnerConfiguration();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AgenticsRunnerConfiguration configuration)
    {
        await _lock.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_configFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                _logger.LogDebug("Created configuration directory {Directory}", directory);
            }

            configuration.LastModified = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(configuration, JsonOptions);
            await File.WriteAllTextAsync(_configFilePath, json);

            _logger.LogDebug("Saved configuration with {Count} registrations to {Path}",
                configuration.Registrations.Count, _configFilePath);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AgenticsRunnerRegistration> AddRegistrationAsync(AgenticsRunnerRegistration registration)
    {
        var config = await LoadAsync();
        config.Registrations.Add(registration);
        await SaveAsync(config);

        _logger.LogInformation("Added agentics runner registration {Id} for {Owner}/{Project}",
            registration.Id, registration.Owner, registration.Project);

        return registration;
    }

    public async Task<List<AgenticsRunnerRegistration>> ListRegistrationsAsync()
    {
        var config = await LoadAsync();
        return config.Registrations;
    }

    public async Task<AgenticsRunnerRegistration?> GetRegistrationAsync(string registrationId)
    {
        var config = await LoadAsync();
        return config.Registrations.FirstOrDefault(r => r.Id == registrationId);
    }
}
