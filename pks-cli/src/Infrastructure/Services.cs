namespace PKS.Infrastructure;

public interface IKubernetesService
{
    Task<string[]> GetDeploymentsAsync(string namespaceName = "default");
    Task<bool> ScaleDeploymentAsync(string deploymentName, int replicas, string namespaceName = "default");
    Task<object> GetDeploymentStatusAsync(string deploymentName, string namespaceName = "default");
}

public class KubernetesService : IKubernetesService
{
    public async Task<string[]> GetDeploymentsAsync(string namespaceName = "default")
    {
        // Simulate Kubernetes API call
        await Task.Delay(100);
        return new[] { "api-service", "web-frontend", "background-worker", "redis-cache" };
    }

    public async Task<bool> ScaleDeploymentAsync(string deploymentName, int replicas, string namespaceName = "default")
    {
        // Simulate scaling operation
        await Task.Delay(500);
        return true;
    }

    public async Task<object> GetDeploymentStatusAsync(string deploymentName, string namespaceName = "default")
    {
        // Simulate status check
        await Task.Delay(200);
        return new
        {
            Name = deploymentName,
            Ready = $"{replicas}/{replicas}",
            Status = "Running",
            Age = "2d"
        };
    }

    private int replicas = 3; // Simulated current replica count
}

public interface IConfigurationService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value, bool global = false, bool encrypt = false);
    Task<Dictionary<string, string>> GetAllAsync();
    Task DeleteAsync(string key);
    Task LoadSettingsAsync();
    Task SaveSettingsAsync();
    Task<bool> IsFirstTimeWarningAcknowledgedAsync();
    Task SetFirstTimeWarningAcknowledgedAsync();
}

public class ConfigurationService : IConfigurationService
{
    private readonly Dictionary<string, string> _config;
    private readonly string _settingsFilePath;
    private readonly object _lockObject = new();

    public ConfigurationService()
    {
        // Initialize with default values
        _config = new Dictionary<string, string>
        {
            { "cluster.endpoint", "https://k8s.production.com" },
            { "namespace.default", "myapp-production" },
            { "registry.url", "registry.company.com" },
            { "auth.token", "***encrypted***" },
            { "deploy.replicas", "3" },
            { "monitoring.enabled", "true" }
        };

        // Set up settings file path
        var userHomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pksDirectory = Path.Combine(userHomeDirectory, ".pks-cli");
        _settingsFilePath = Path.Combine(pksDirectory, "settings.json");

        // Load settings from file if it exists
        LoadSettingsAsync().GetAwaiter().GetResult();
    }

    public async Task<string?> GetAsync(string key)
    {
        await Task.Delay(50);
        lock (_lockObject)
        {
            return _config.TryGetValue(key, out var value) ? value : null;
        }
    }

    public async Task SetAsync(string key, string value, bool global = false, bool encrypt = false)
    {
        await Task.Delay(100);
        lock (_lockObject)
        {
            _config[key] = encrypt ? "***encrypted***" : value;
        }
        
        // Save to file if this is a persistent setting
        if (global || key.StartsWith("cli."))
        {
            await SaveSettingsAsync();
        }
    }

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        await Task.Delay(100);
        lock (_lockObject)
        {
            return new Dictionary<string, string>(_config);
        }
    }

    public async Task DeleteAsync(string key)
    {
        await Task.Delay(50);
        lock (_lockObject)
        {
            _config.Remove(key);
        }
        await SaveSettingsAsync();
    }

    public async Task LoadSettingsAsync()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = await File.ReadAllTextAsync(_settingsFilePath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                
                if (settings != null)
                {
                    lock (_lockObject)
                    {
                        foreach (var kvp in settings)
                        {
                            _config[kvp.Key] = kvp.Value;
                        }
                    }
                }
            }
        }
        catch
        {
            // Gracefully handle file read errors - continue with defaults
        }
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Dictionary<string, string> configToSave;
            lock (_lockObject)
            {
                configToSave = new Dictionary<string, string>(_config);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(configToSave, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            
            await File.WriteAllTextAsync(_settingsFilePath, json);
        }
        catch
        {
            // Gracefully handle file write errors - warning may still display
        }
    }

    public async Task<bool> IsFirstTimeWarningAcknowledgedAsync()
    {
        var value = await GetAsync("cli.first-time-warning-acknowledged");
        return bool.TryParse(value, out var result) && result;
    }

    public async Task SetFirstTimeWarningAcknowledgedAsync()
    {
        await SetAsync("cli.first-time-warning-acknowledged", "true");
    }
}

public interface IDeploymentService
{
    Task<bool> DeployAsync(string environment, string? image = null, int replicas = 1);
    Task<object> GetDeploymentInfoAsync(string environment);
    Task<bool> RollbackAsync(string environment, string? revision = null);
}

public class DeploymentService : IDeploymentService
{
    public async Task<bool> DeployAsync(string environment, string? image = null, int replicas = 1)
    {
        // Simulate deployment process
        await Task.Delay(2000);
        return true;
    }

    public async Task<object> GetDeploymentInfoAsync(string environment)
    {
        await Task.Delay(200);
        return new
        {
            Environment = environment,
            Status = "Deployed",
            Replicas = 3,
            Image = "myapp:v1.2.3",
            Url = $"https://{environment}.myapp.com"
        };
    }

    public async Task<bool> RollbackAsync(string environment, string? revision = null)
    {
        await Task.Delay(1000);
        return true;
    }
}

// Import HooksService from separate file
// Note: IHooksService and HooksService are defined in separate files:
// - /workspace/pks-cli/src/Infrastructure/Services/IHooksService.cs  
// - /workspace/pks-cli/src/Infrastructure/Services/HooksService.cs

// Import MCP Service from separate file  
// Note: IMcpServerService and McpServerService are defined in separate files:
// - /workspace/pks-cli/src/Infrastructure/Services/IMcpServerService.cs
// - /workspace/pks-cli/src/Infrastructure/Services/McpServerService.cs

// Import Agent Framework Service from separate file
// Note: IAgentFrameworkService and AgentFrameworkService are defined in separate files:
// - /workspace/pks-cli/src/Infrastructure/Services/IAgentFrameworkService.cs
// - /workspace/pks-cli/src/Infrastructure/Services/AgentFrameworkService.cs

// First-Time Warning Service
// Note: IFirstTimeWarningService and FirstTimeWarningService are defined in separate files:
// - /workspace/pks-cli/src/Infrastructure/Services/IFirstTimeWarningService.cs
// - /workspace/pks-cli/src/Infrastructure/Services/FirstTimeWarningService.cs