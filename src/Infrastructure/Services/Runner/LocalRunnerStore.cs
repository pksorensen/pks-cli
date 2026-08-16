using System.Text.Json;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>How a detached local runner was started, which decides how it is inspected and stopped.</summary>
public enum LocalRunnerMode
{
    /// <summary>Inside a detached tmux session -- the preferred mode; gives <c>logs</c> for free.</summary>
    Tmux,
    /// <summary>A plain background process with stdout/stderr redirected to <see cref="LocalRunnerRecord.LogPath"/>.</summary>
    Process,
}

/// <summary>
/// One detached runner this machine started. Deliberately *not* stored on the registration in
/// agentics-runners.json: a registration is durable identity (id, token, server) while this is
/// volatile runtime state that a reboot invalidates, and mixing the two means a crash-and-restart
/// can lose a token while rewriting a pid.
/// </summary>
public sealed class LocalRunnerRecord
{
    public string Owner { get; set; } = "";
    public string Project { get; set; } = "";
    public string Server { get; set; } = "";
    public LocalRunnerMode Mode { get; set; }
    public string? TmuxSession { get; set; }
    public int? Pid { get; set; }
    public string WorkDir { get; set; } = "";
    public string? LogPath { get; set; }
    public DateTime StartedAt { get; set; }

    public string OwnerProject => $"{Owner}/{Project}";

    public bool Matches(string owner, string project) =>
        string.Equals(Owner, owner, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Project, project, StringComparison.OrdinalIgnoreCase);
}

public interface ILocalRunnerStore
{
    Task<List<LocalRunnerRecord>> ListAsync(CancellationToken ct = default);
    Task<LocalRunnerRecord?> FindAsync(string owner, string project, CancellationToken ct = default);
    Task UpsertAsync(LocalRunnerRecord record, CancellationToken ct = default);
    Task RemoveAsync(string owner, string project, CancellationToken ct = default);
}

/// <summary>
/// Persists detached-runner state as JSON at <c>~/.pks-cli/agentics-local-runners.json</c>, next to
/// the registrations it complements. One record per owner/project -- starting a second runner for
/// the same project on the same machine is a mistake (both would claim the same jobs), so the store
/// makes it representationally impossible rather than merely discouraged.
/// </summary>
public class LocalRunnerStore : ILocalRunnerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _configPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public LocalRunnerStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".pks-cli",
        "agentics-local-runners.json"))
    {
    }

    public LocalRunnerStore(string configPath) => _configPath = configPath;

    public async Task<List<LocalRunnerRecord>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await ReadAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<LocalRunnerRecord?> FindAsync(string owner, string project, CancellationToken ct = default)
    {
        var all = await ListAsync(ct);
        return all.FirstOrDefault(r => r.Matches(owner, project));
    }

    public async Task UpsertAsync(LocalRunnerRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAsync(ct);
            all.RemoveAll(r => r.Matches(record.Owner, record.Project));
            all.Add(record);
            await WriteAsync(all, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string owner, string project, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await ReadAsync(ct);
            if (all.RemoveAll(r => r.Matches(owner, project)) > 0)
                await WriteAsync(all, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<LocalRunnerRecord>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_configPath)) return new List<LocalRunnerRecord>();

        try
        {
            var json = await File.ReadAllTextAsync(_configPath, ct);
            return JsonSerializer.Deserialize<List<LocalRunnerRecord>>(json, JsonOptions)
                   ?? new List<LocalRunnerRecord>();
        }
        catch (JsonException)
        {
            // A corrupt runtime-state file must never stop the CLI: the worst case is that we
            // forget about a detached runner, which `list` then reports as untracked rather than
            // crashing on every invocation.
            return new List<LocalRunnerRecord>();
        }
    }

    private async Task WriteAsync(List<LocalRunnerRecord> records, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(_configPath, JsonSerializer.Serialize(records, JsonOptions), ct);
    }
}
