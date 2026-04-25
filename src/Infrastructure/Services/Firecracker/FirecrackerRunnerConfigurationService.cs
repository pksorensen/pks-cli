using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Firecracker;

public class FirecrackerRunnerConfigurationService : IFirecrackerRunnerConfigurationService
{
    private readonly string _configFilePath;
    private readonly ILogger<FirecrackerRunnerConfigurationService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FirecrackerRunnerConfigurationService(ILogger<FirecrackerRunnerConfigurationService> logger)
        : this(logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli",
            "firecracker-runners.json"))
    {
    }

    public FirecrackerRunnerConfigurationService(ILogger<FirecrackerRunnerConfigurationService> logger, string configFilePath)
    {
        _logger = logger;
        _configFilePath = configFilePath;
    }

    public async Task<FirecrackerRunnerConfiguration> LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_configFilePath))
            {
                _logger.LogDebug("Configuration file not found at {Path}, returning defaults", _configFilePath);
                return new FirecrackerRunnerConfiguration();
            }

            var json = await File.ReadAllTextAsync(_configFilePath);
            var config = JsonSerializer.Deserialize<FirecrackerRunnerConfiguration>(json, JsonOptions);

            _logger.LogDebug("Loaded configuration with {Count} registrations from {Path}",
                config?.Registrations.Count ?? 0, _configFilePath);

            return config ?? new FirecrackerRunnerConfiguration();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize configuration from {Path}, returning defaults", _configFilePath);
            return new FirecrackerRunnerConfiguration();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(FirecrackerRunnerConfiguration configuration)
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

    public async Task<FirecrackerRunnerRegistration> AddRegistrationAsync(FirecrackerRunnerRegistration registration)
    {
        var config = await LoadAsync();

        var idx = config.Registrations.FindIndex(r =>
            string.Equals(r.Owner, registration.Owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Project, registration.Project, StringComparison.OrdinalIgnoreCase));

        if (idx >= 0)
        {
            config.Registrations[idx] = registration;
            _logger.LogInformation("Updated firecracker runner registration {Id} for {Owner}/{Project}",
                registration.Id, registration.Owner, registration.Project);
        }
        else
        {
            config.Registrations.Add(registration);
            _logger.LogInformation("Added firecracker runner registration {Id} for {Owner}/{Project}",
                registration.Id, registration.Owner, registration.Project);
        }

        await SaveAsync(config);
        return registration;
    }

    public async Task<List<FirecrackerRunnerRegistration>> ListRegistrationsAsync()
    {
        var config = await LoadAsync();
        return config.Registrations;
    }

    public async Task<FirecrackerRunnerRegistration?> GetRegistrationAsync(string registrationId)
    {
        var config = await LoadAsync();
        return config.Registrations.FirstOrDefault(r => r.Id == registrationId);
    }
}
