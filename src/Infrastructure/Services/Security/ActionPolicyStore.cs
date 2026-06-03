using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Security;

/// <summary>Per-action two-factor policy: which actions require a second factor.</summary>
public interface IActionPolicyStore
{
    /// <summary>True if the action requires a second factor (explicit setting, else catalog default).</summary>
    Task<bool> IsRequiredAsync(string actionId);

    /// <summary>Every catalog action with its effective on/off state.</summary>
    Task<IReadOnlyDictionary<string, bool>> GetAllAsync();

    /// <summary>Persist the full action → required map (0600).</summary>
    Task SetAsync(IReadOnlyDictionary<string, bool> policy);
}

/// <summary>
/// JSON persistence for the action policy at <c>~/.pks-cli/actions.json</c> (0600). Unknown or
/// unset actions fall back to <see cref="ActionDefinition.DefaultRequired"/> so a new gated
/// action is protected by default even before the user has visited <c>pks actions</c>.
/// </summary>
public sealed class ActionPolicyStore : IActionPolicyStore
{
    private readonly IActionCatalog _catalog;
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ActionPolicyStore(IActionCatalog catalog) : this(catalog, SecurityFiles.PathFor("actions.json")) { }

    public ActionPolicyStore(IActionCatalog catalog, string path)
    {
        _catalog = catalog;
        _path = path;
    }

    private sealed class PolicyFile
    {
        public Dictionary<string, bool> Actions { get; set; } = new();
        public DateTime UpdatedUtc { get; set; }
    }

    private async Task<PolicyFile> LoadAsync()
    {
        if (!File.Exists(_path)) return new PolicyFile();
        try
        {
            return JsonSerializer.Deserialize<PolicyFile>(await File.ReadAllTextAsync(_path), JsonOptions) ?? new PolicyFile();
        }
        catch (JsonException) { return new PolicyFile(); }
    }

    public async Task<bool> IsRequiredAsync(string actionId)
    {
        await _lock.WaitAsync();
        try
        {
            var file = await LoadAsync();
            if (file.Actions.TryGetValue(actionId, out var required)) return required;
            return _catalog.Find(actionId)?.DefaultRequired ?? false;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var file = await LoadAsync();
            var result = new Dictionary<string, bool>();
            foreach (var def in _catalog.All)
                result[def.Id] = file.Actions.TryGetValue(def.Id, out var v) ? v : def.DefaultRequired;
            return result;
        }
        finally { _lock.Release(); }
    }

    public async Task SetAsync(IReadOnlyDictionary<string, bool> policy)
    {
        await _lock.WaitAsync();
        try
        {
            SecurityFiles.EnsureDirectory(_path);
            var file = new PolicyFile { Actions = new Dictionary<string, bool>(policy), UpdatedUtc = DateTime.UtcNow };
            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(file, JsonOptions));
            SecurityFiles.Restrict(_path);
        }
        finally { _lock.Release(); }
    }
}
