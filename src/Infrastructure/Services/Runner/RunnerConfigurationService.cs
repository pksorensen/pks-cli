using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Manages runner configuration persisted as JSON at a configurable path
/// (defaults to ~/.pks-cli/runners.json). Thread-safe via SemaphoreSlim.
/// </summary>
public class RunnerConfigurationService : IRunnerConfigurationService
{
    private readonly string _configFilePath;
    private readonly ILogger<RunnerConfigurationService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates a new RunnerConfigurationService with the default config path (~/.pks-cli/runners.json)
    /// </summary>
    public RunnerConfigurationService(ILogger<RunnerConfigurationService> logger)
        : this(logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli",
            "runners.json"))
    {
    }

    /// <summary>
    /// Creates a new RunnerConfigurationService with a custom config file path (useful for testing)
    /// </summary>
    public RunnerConfigurationService(ILogger<RunnerConfigurationService> logger, string configFilePath)
    {
        _logger = logger;
        _configFilePath = configFilePath;
    }

    public async Task<RunnerConfiguration> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_configFilePath))
            {
                _logger.LogDebug("Configuration file not found at {Path}, returning defaults", _configFilePath);
                return new RunnerConfiguration();
            }

            var json = await File.ReadAllTextAsync(_configFilePath);
            var config = JsonSerializer.Deserialize<RunnerConfiguration>(json, JsonOptions);

            _logger.LogDebug("Loaded configuration with {Count} registrations from {Path}",
                config?.Registrations.Count ?? 0, _configFilePath);

            return config ?? new RunnerConfiguration();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize configuration from {Path}, returning defaults", _configFilePath);
            return new RunnerConfiguration();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(RunnerConfiguration configuration)
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

    public async Task<RunnerRegistration> AddRegistrationAsync(string owner, string repository, string? labels = null)
    {
        var config = await LoadAsync();

        var registration = new RunnerRegistration
        {
            Owner = owner,
            Repository = repository,
            Labels = labels ?? "devcontainer-runner",
            RegisteredAt = DateTime.UtcNow,
            Enabled = true
        };

        config.Registrations.Add(registration);
        await SaveAsync(config);

        _logger.LogInformation("Added registration {Id} for {Owner}/{Repository}",
            registration.Id, owner, repository);

        return registration;
    }

    public async Task<bool> RemoveRegistrationAsync(string registrationId)
    {
        var config = await LoadAsync();

        var registration = config.Registrations.FirstOrDefault(r => r.Id == registrationId);
        if (registration == null)
        {
            _logger.LogDebug("Registration {Id} not found for removal", registrationId);
            return false;
        }

        config.Registrations.Remove(registration);
        await SaveAsync(config);

        _logger.LogInformation("Removed registration {Id} for {Owner}/{Repository}",
            registrationId, registration.Owner, registration.Repository);

        return true;
    }

    public async Task<List<RunnerRegistration>> ListRegistrationsAsync()
    {
        var config = await LoadAsync();
        return config.Registrations;
    }

    public async Task<RunnerRegistration?> GetRegistrationAsync(string registrationId)
    {
        var config = await LoadAsync();
        return config.Registrations.FirstOrDefault(r => r.Id == registrationId);
    }
}
