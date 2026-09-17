using System.Text.Json;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Azure;

/// <summary>
/// The non-secret registry of Azure resources the CLI knows about, each with an enabled flag, so a
/// vertical can have several resources switched on at once. Backed by
/// <c>~/.pks-cli/azure-resources.json</c>.
/// </summary>
public interface IAzureResourceRegistry
{
    Task<IReadOnlyList<AzureResourceEntry>> ListAsync(AzureResourceKind? kind = null);
    Task<IReadOnlyList<AzureResourceEntry>> ListEnabledAsync(AzureResourceKind kind);

    /// <summary>Case-insensitive match on <see cref="AzureResourceEntry.Key"/> or
    /// <see cref="AzureResourceEntry.Name"/> within one kind.</summary>
    Task<AzureResourceEntry?> FindAsync(AzureResourceKind kind, string nameOrKey);

    /// <summary>
    /// Adds or refreshes entries. Identity is <see cref="AzureResourceEntry.Kind"/> plus
    /// <see cref="AzureResourceEntry.ResourceId"/> when both sides have one, else
    /// <see cref="AzureResourceEntry.Key"/>, case-insensitive. An existing entry keeps its
    /// <see cref="AzureResourceEntry.Enabled"/> flag; the other fields are refreshed from the incoming
    /// entry (<see cref="AzureResourceEntry.TenantId"/> only when the incoming one is non-null).
    /// </summary>
    Task UpsertAsync(IEnumerable<AzureResourceEntry> entries);

    /// <returns><c>false</c> when no entry matches.</returns>
    Task<bool> SetEnabledAsync(AzureResourceKind kind, string nameOrKey, bool enabled);

    /// <returns><c>false</c> when no entry matches.</returns>
    Task<bool> RemoveAsync(AzureResourceKind kind, string nameOrKey);
}

public sealed class AzureResourceRegistry : IAzureResourceRegistry
{
    private const string FileShareCredentialsKey = "fileshare.azure.credentials";

    private static readonly string[] LegacyLogAnalyticsKeys =
    {
        "loganalytics.workspace_id",
        "loganalytics.workspace_name",
        "loganalytics.resource_id",
        "loganalytics.subscription_id",
        "loganalytics.registered_at"
    };

    private static readonly string[] LegacyAppInsightsKeys =
    {
        "appinsights.app_id",
        "appinsights.resource_name",
        "appinsights.subscription_id",
        "appinsights.registered_at"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class RegistryFile
    {
        public List<AzureResourceEntry> Resources { get; set; } = new();
        public DateTime? LastModified { get; set; }
    }

    private readonly IConfigurationService _config;
    private readonly ISecretResolver _secrets;
    private readonly string _configPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AzureResourceRegistry(IConfigurationService config, ISecretResolver secrets, string? configPath = null)
    {
        _config = config;
        _secrets = secrets;
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli", "azure-resources.json");
    }

    public async Task<IReadOnlyList<AzureResourceEntry>> ListAsync(AzureResourceKind? kind = null)
    {
        var file = await ReadAsync();
        return file.Resources
            .Where(e => kind == null || e.Kind == kind)
            .ToList();
    }

    public async Task<IReadOnlyList<AzureResourceEntry>> ListEnabledAsync(AzureResourceKind kind)
    {
        var file = await ReadAsync();
        return file.Resources
            .Where(e => e.Kind == kind && e.Enabled)
            .ToList();
    }

    public async Task<AzureResourceEntry?> FindAsync(AzureResourceKind kind, string nameOrKey)
    {
        var file = await ReadAsync();
        return Find(file, kind, nameOrKey);
    }

    public Task UpsertAsync(IEnumerable<AzureResourceEntry> entries)
        => MutateAsync(file =>
        {
            var now = DateTime.UtcNow;
            foreach (var incoming in entries)
            {
                var existing = file.Resources.FirstOrDefault(e => SameResource(e, incoming));
                if (existing == null)
                {
                    if (incoming.DiscoveredAt == default) incoming.DiscoveredAt = now;
                    file.Resources.Add(incoming);
                    continue;
                }

                existing.Name = incoming.Name;
                existing.Key = incoming.Key;
                existing.ResourceId = incoming.ResourceId ?? existing.ResourceId;
                existing.SubscriptionId = incoming.SubscriptionId;
                existing.SubscriptionName = incoming.SubscriptionName;
                existing.ResourceGroup = incoming.ResourceGroup;
                if (incoming.TenantId != null) existing.TenantId = incoming.TenantId;
                if (incoming.DiscoveredAt != default) existing.DiscoveredAt = incoming.DiscoveredAt;
                // Enabled is the user's decision and survives re-discovery.
            }
            return true;
        });

    public Task<bool> SetEnabledAsync(AzureResourceKind kind, string nameOrKey, bool enabled)
        => MutateAsync(file =>
        {
            var entry = Find(file, kind, nameOrKey);
            if (entry == null) return false;
            entry.Enabled = enabled;
            return true;
        });

    public Task<bool> RemoveAsync(AzureResourceKind kind, string nameOrKey)
        => MutateAsync(file =>
        {
            var entry = Find(file, kind, nameOrKey);
            if (entry == null) return false;
            file.Resources.Remove(entry);
            return true;
        });

    // ── Matching ──────────────────────────────────────────────────────────

    private static AzureResourceEntry? Find(RegistryFile file, AzureResourceKind kind, string nameOrKey)
    {
        var ofKind = file.Resources.Where(e => e.Kind == kind).ToList();
        return ofKind.FirstOrDefault(e => string.Equals(e.Key, nameOrKey, StringComparison.OrdinalIgnoreCase))
            ?? ofKind.FirstOrDefault(e => string.Equals(e.Name, nameOrKey, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameResource(AzureResourceEntry a, AzureResourceEntry b)
    {
        if (a.Kind != b.Kind) return false;
        if (!string.IsNullOrEmpty(a.ResourceId) && !string.IsNullOrEmpty(b.ResourceId))
            return string.Equals(a.ResourceId, b.ResourceId, StringComparison.OrdinalIgnoreCase);
        return string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
    }

    // ── File access ───────────────────────────────────────────────────────

    private async Task<RegistryFile> ReadAsync()
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

    /// <summary>Runs <paramref name="mutate"/> against the loaded file under the lock and saves when
    /// it returns <c>true</c>; the return value is passed through.</summary>
    private async Task<bool> MutateAsync(Func<RegistryFile, bool> mutate)
    {
        await _lock.WaitAsync();
        try
        {
            var file = await LoadUnlockedAsync();
            var changed = mutate(file);
            if (changed) await SaveUnlockedAsync(file);
            return changed;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<RegistryFile> LoadUnlockedAsync()
    {
        if (!File.Exists(_configPath))
            return await MigrateLegacyUnlockedAsync();

        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            return JsonSerializer.Deserialize<RegistryFile>(json, JsonOptions) ?? new RegistryFile();
        }
        catch (JsonException)
        {
            return new RegistryFile();
        }
    }

    private async Task SaveUnlockedAsync(RegistryFile file)
    {
        file.LastModified = DateTime.UtcNow;
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(file, JsonOptions);
        await File.WriteAllTextAsync(_configPath, json);
    }

    // ── One-shot legacy migration ─────────────────────────────────────────

    /// <summary>
    /// Runs exactly once: the first time the registry file is missing. Lifts the single-resource keys
    /// the old <c>LogAnalyticsConfigService</c> / <c>AppInsightsConfigService</c> wrote into
    /// <c>settings.json</c>, and the storage account selected by <c>pks fileshare</c>, into entries.
    /// The registry file is written before the legacy keys are deleted, so a crash in between leaves
    /// stale keys (harmless) rather than lost registrations — and it is written even when nothing was
    /// found, so the migration never runs again.
    /// </summary>
    private async Task<RegistryFile> MigrateLegacyUnlockedAsync()
    {
        var file = new RegistryFile();
        var keysToDelete = new List<string>();
        var now = DateTime.UtcNow;

        var workspaceId = await _config.GetAsync("loganalytics.workspace_id");
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            var name = await _config.GetAsync("loganalytics.workspace_name");
            file.Resources.Add(new AzureResourceEntry
            {
                Kind = AzureResourceKind.LogAnalytics,
                Key = workspaceId,
                Name = string.IsNullOrWhiteSpace(name) ? workspaceId : name,
                ResourceId = NullIfBlank(await _config.GetAsync("loganalytics.resource_id")),
                SubscriptionId = NullIfBlank(await _config.GetAsync("loganalytics.subscription_id")),
                TenantId = null,
                Enabled = true,
                DiscoveredAt = ParseOrNow(await _config.GetAsync("loganalytics.registered_at"), now)
            });
            keysToDelete.AddRange(LegacyLogAnalyticsKeys);
        }

        var appId = await _config.GetAsync("appinsights.app_id");
        if (!string.IsNullOrWhiteSpace(appId))
        {
            var name = await _config.GetAsync("appinsights.resource_name");
            file.Resources.Add(new AzureResourceEntry
            {
                Kind = AzureResourceKind.AppInsights,
                Key = appId,
                Name = string.IsNullOrWhiteSpace(name) ? appId : name,
                SubscriptionId = NullIfBlank(await _config.GetAsync("appinsights.subscription_id")),
                TenantId = null,
                Enabled = true,
                DiscoveredAt = ParseOrNow(await _config.GetAsync("appinsights.registered_at"), now)
            });
            keysToDelete.AddRange(LegacyAppInsightsKeys);
        }

        // The fileshare credential is read for its non-secret selection only. It is never rewritten
        // or deleted here: the refresh token inside it has its own lifecycle in AzureFileShareProvider.
        var storage = await ReadLegacyStorageSelectionAsync(now);
        if (storage != null)
            file.Resources.Add(storage);

        await SaveUnlockedAsync(file);

        foreach (var key in keysToDelete)
            await _config.DeleteAsync(key);

        return file;
    }

    private async Task<AzureResourceEntry?> ReadLegacyStorageSelectionAsync(DateTime now)
    {
        try
        {
            var json = await _secrets.RevealAsync(FileShareCredentialsKey);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var creds = JsonSerializer.Deserialize<FileShareStoredCredentials>(json);
            if (creds == null || string.IsNullOrWhiteSpace(creds.SelectedStorageAccountName)) return null;

            return new AzureResourceEntry
            {
                Kind = AzureResourceKind.Storage,
                Key = creds.SelectedStorageAccountName,
                Name = creds.SelectedStorageAccountName,
                ResourceGroup = NullIfBlank(creds.SelectedStorageAccountResourceGroup),
                SubscriptionId = NullIfBlank(creds.SelectedSubscriptionId),
                SubscriptionName = NullIfBlank(creds.SelectedSubscriptionName),
                TenantId = NullIfBlank(creds.TenantId),
                Enabled = true,
                DiscoveredAt = creds.CreatedAt != default ? creds.CreatedAt : now
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTime ParseOrNow(string? value, DateTime now)
        => DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : now;
}
