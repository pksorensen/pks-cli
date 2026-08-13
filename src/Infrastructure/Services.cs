using PKS.Infrastructure.Services.Security;

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

/// <summary>
/// Ordinary, non-sensitive configuration for pks.
///
/// This interface is deliberately unable to produce a credential. Keys that
/// <see cref="SecretKeys.IsSecret"/> classifies as secret are routed to the encrypted
/// <see cref="ISecretStore"/> on write and are <em>invisible</em> to <see cref="GetAsync"/> and
/// <see cref="GetAllAsync"/> — there is no flag, no override and no "just this once". That is what
/// makes a config dump (a command, an MCP tool, a support bundle, a stray <c>cat</c>) harmless.
///
/// To find out whether a credential is present, use <see cref="HasSecretAsync"/> or
/// <see cref="DescribeSecretAsync"/>. To actually use one, take an <see cref="ISecretResolver"/> —
/// which the build gate forbids in the command and MCP layers.
/// </summary>
public interface IConfigurationService
{
    /// <summary>The value for a non-secret key. Always null for secret-classified keys.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>Stores a value. Secret-classified keys (and anything written with
    /// <paramref name="encrypt"/>) go to the encrypted secret store instead of settings.json.</summary>
    Task SetAsync(string key, string value, bool global = false, bool encrypt = false);

    /// <summary>All non-secret settings. Never contains credential material.</summary>
    Task<Dictionary<string, string>> GetAllAsync();

    Task DeleteAsync(string key);
    Task LoadSettingsAsync();
    Task SaveSettingsAsync();
    Task<bool> IsFirstTimeWarningAcknowledgedAsync();
    Task SetFirstTimeWarningAcknowledgedAsync();

    /// <summary>Whether a credential is stored under <paramref name="key"/>. Safe to print.</summary>
    Task<bool> HasSecretAsync(string key);

    /// <summary>Existence, write time and a machine-local fingerprint for a stored credential —
    /// everything that can be said about it without revealing it.</summary>
    Task<SecretDescriptor?> DescribeSecretAsync(string key);
}

public class ConfigurationService : IConfigurationService
{
    private readonly Dictionary<string, string> _config;
    private readonly Dictionary<string, string?> _pendingPersistentChanges = new();
    private readonly string _settingsFilePath;
    private readonly object _lockObject = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly ISecretStore _secretStore;
    private readonly ISecretResolver _secretResolver;

    public ConfigurationService() : this(new SecretStore())
    {
    }

    public ConfigurationService(SecretStore secretStore)
        : this(GetDefaultSettingsFilePath(), secretStore)
    {
    }

    internal ConfigurationService(string settingsFilePath)
        : this(settingsFilePath, new SecretStore(
            Path.GetDirectoryName(settingsFilePath) ?? Directory.GetCurrentDirectory()))
    {
    }

    internal ConfigurationService(string settingsFilePath, SecretStore secretStore)
    {
        _config = new Dictionary<string, string>();
        _settingsFilePath = settingsFilePath;
        _secretStore = secretStore;
        _secretResolver = secretStore;

        // Load settings from file if it exists. This also performs the one-way migration of any
        // plaintext credentials still sitting in settings.json.
        LoadSettingsAsync().GetAwaiter().GetResult();
    }

    public async Task<string?> GetAsync(string key)
    {
        await Task.Delay(50);

        // Secrets are never readable through the configuration surface — not masked, not gated:
        // absent. Callers that need the value take an ISecretResolver instead.
        if (SecretKeys.IsSecret(key)) return null;

        lock (_lockObject)
        {
            return _config.TryGetValue(key, out var value) ? value : null;
        }
    }

    public async Task SetAsync(string key, string value, bool global = false, bool encrypt = false)
    {
        if (SecretKeys.IsSecret(key) || encrypt)
        {
            // The old behaviour of `encrypt: true` was to persist the literal "***encrypted***",
            // silently destroying the credential. It now means what it says.
            await _secretStore.SetAsync(key, value);
            await ForgetPlaintextAsync(key);
            return;
        }

        await Task.Delay(100);
        var persistent = global || key.StartsWith("cli.");
        lock (_lockObject)
        {
            _config[key] = value;
            if (persistent)
            {
                _pendingPersistentChanges[key] = value;
            }
        }

        // Save to file if this is a persistent setting
        if (persistent)
        {
            await SaveSettingsAsync();
        }
    }

    public Task<bool> HasSecretAsync(string key) => _secretStore.HasAsync(key);

    public Task<SecretDescriptor?> DescribeSecretAsync(string key) => _secretStore.DescribeAsync(key);

    /// <summary>Drops any lingering plaintext copy of a key from memory and from settings.json.</summary>
    private async Task ForgetPlaintextAsync(string key)
    {
        bool hadPlaintext;
        lock (_lockObject)
        {
            hadPlaintext = _config.Remove(key);
            if (hadPlaintext) _pendingPersistentChanges[key] = null;
        }

        if (hadPlaintext) await SaveSettingsAsync();
    }

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        await Task.Delay(100);
        lock (_lockObject)
        {
            // _config should never hold a secret — migration moves them out and SaveSettingsAsync
            // refuses to write them back. Filtering here too means the one method everything dumps
            // (status tools, support bundles, `pks config list`) is safe even if that ever slips.
            return _config
                .Where(kvp => !SecretKeys.IsSecret(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }
    }

    public async Task DeleteAsync(string key)
    {
        await Task.Delay(50);

        if (SecretKeys.IsSecret(key))
        {
            await _secretStore.DeleteAsync(key);
        }

        lock (_lockObject)
        {
            _config.Remove(key);
            _pendingPersistentChanges[key] = null;
        }
        await SaveSettingsAsync();
    }

    public async Task LoadSettingsAsync()
    {
        try
        {
            if (!File.Exists(_settingsFilePath)) return;

            var json = await File.ReadAllTextAsync(_settingsFilePath);
            var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (settings == null) return;

            var toRemoveFromSettings = new List<string>();

            foreach (var kvp in settings)
            {
                if (SecretKeys.IsLostValue(kvp.Value))
                {
                    // Written by the old `encrypt: true` path, which stored the sentinel instead of
                    // the value. There is nothing to migrate — the credential is already gone.
                    toRemoveFromSettings.Add(kvp.Key);
                    continue;
                }

                if (SecretKeys.IsSecret(kvp.Key))
                {
                    // Migration: an existing plaintext credential moves into the encrypted store the
                    // first time this build touches the file, so nobody has to re-authenticate
                    // anything. Runs on every load, which also cleans up after an older pks build
                    // (or `dotnet dnx pks-cli`) that wrote plaintext back during the rollout window.
                    if (!string.IsNullOrEmpty(kvp.Value))
                    {
                        await _secretStore.SetAsync(kvp.Key, kvp.Value);
                    }
                    toRemoveFromSettings.Add(kvp.Key);
                    continue;
                }

                lock (_lockObject)
                {
                    _config[kvp.Key] = kvp.Value;
                }
            }

            if (toRemoveFromSettings.Count > 0)
            {
                lock (_lockObject)
                {
                    foreach (var key in toRemoveFromSettings)
                    {
                        _config.Remove(key);
                        _pendingPersistentChanges[key] = null;
                    }
                }

                await SaveSettingsAsync();
            }
        }
        catch
        {
            // Gracefully handle file read errors - continue with defaults
        }
    }

    public async Task SaveSettingsAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(_settingsFilePath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var settingsLock = await AcquireSettingsLockAsync();

            var configToSave = await ReadPersistedSettingsAsync();
            Dictionary<string, string?> changesToSave;
            lock (_lockObject)
            {
                changesToSave = new Dictionary<string, string?>(_pendingPersistentChanges);
                if (configToSave == null)
                {
                    configToSave = new Dictionary<string, string>(_config);
                }
            }

            foreach (var (key, value) in changesToSave)
            {
                if (value == null)
                {
                    configToSave.Remove(key);
                }
                else
                {
                    configToSave[key] = value;
                }
            }

            // Invariant: settings.json never contains credential material. This also catches
            // plaintext that an older pks build wrote back into the file after we loaded it.
            foreach (var key in configToSave.Keys.Where(SecretKeys.IsSecret).ToList())
            {
                configToSave.Remove(key);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(configToSave, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });

            var temporaryPath = $"{_settingsFilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, json);
                SetOwnerOnlyPermissions(temporaryPath);
                File.Move(temporaryPath, _settingsFilePath, overwrite: true);
                SetOwnerOnlyPermissions(_settingsFilePath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }

            lock (_lockObject)
            {
                foreach (var (key, value) in configToSave)
                {
                    _config[key] = value;
                }

                foreach (var (key, savedValue) in changesToSave)
                {
                    if (_pendingPersistentChanges.TryGetValue(key, out var pendingValue) &&
                        pendingValue == savedValue)
                    {
                        _pendingPersistentChanges.Remove(key);
                    }
                }
            }
        }
        catch
        {
            // Gracefully handle file write errors - warning may still display
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static string GetDefaultSettingsFilePath()
    {
        var userHomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHomeDirectory, ".pks-cli", "settings.json");
    }

    private async Task<Dictionary<string, string>?> ReadPersistedSettingsAsync()
    {
        if (!File.Exists(_settingsFilePath)) return null;

        var json = await File.ReadAllTextAsync(_settingsFilePath);
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
    }

    private async Task<FileStream> AcquireSettingsLockAsync()
    {
        var lockPath = $"{_settingsFilePath}.lock";
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (true)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                SetOwnerOnlyPermissions(lockPath);
                return stream;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
        }
    }

    private static void SetOwnerOnlyPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
