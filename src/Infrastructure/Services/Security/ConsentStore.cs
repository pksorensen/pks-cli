using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Security;

public enum ConsentStatus
{
    Pending,
    Approved,
    Denied,
    Consumed,
    Expired
}

/// <summary>
/// A request to perform a resource-scoped sensitive action, created at the choke-point when the
/// operation could not be approved in-band (no TTY, or the caller is an agent). A human resolves
/// it out of band with <c>pks consent approve</c>, which turns it into a short-lived grant.
/// </summary>
public sealed record ConsentRequest
{
    public string Id { get; init; } = string.Empty;
    public string ActionId { get; init; } = string.Empty;

    /// <summary>Opaque resource key, e.g. <c>azure-fileshare:account/share</c>.</summary>
    public string Resource { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    /// <summary>The exact items the action will touch. Approval binds to these, not to a pattern.</summary>
    public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();

    /// <summary>Digest of <see cref="Targets"/>; re-checked at consume time so the set cannot drift.</summary>
    public string TargetsFingerprint { get; init; } = string.Empty;

    public ConsentStatus Status { get; init; } = ConsentStatus.Pending;
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>When an unapproved request stops being approvable.</summary>
    public DateTimeOffset ExpiresUtc { get; init; }

    public DateTimeOffset? ApprovedUtc { get; init; }

    /// <summary>When the approved grant stops being usable. Null until approved.</summary>
    public DateTimeOffset? GrantExpiresUtc { get; init; }

    /// <summary>How many more times the grant may be consumed. Zero once spent.</summary>
    public int RemainingUses { get; init; }

    public string? DeniedReason { get; init; }

    /// <summary>Best-effort record of who asked (the sudo caller, i.e. the agent, when applicable).</summary>
    public string? RequestedBy { get; init; }

    public bool IsUsable(DateTimeOffset now) =>
        Status == ConsentStatus.Approved &&
        RemainingUses > 0 &&
        (GrantExpiresUtc == null || GrantExpiresUtc > now);
}

public interface IConsentStore
{
    Task<ConsentRequest> CreateAsync(
        string actionId, string resource, string summary,
        IReadOnlyList<string> targets, TimeSpan pendingTtl, CancellationToken ct = default);

    Task<ConsentRequest?> GetAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<ConsentRequest>> ListAsync(bool includeResolved = false, CancellationToken ct = default);

    Task<ConsentRequest> ApproveAsync(string id, int uses, TimeSpan grantTtl, CancellationToken ct = default);

    Task<ConsentRequest> DenyAsync(string id, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Spend one use of an approved grant matching the action, resource and target fingerprint.
    /// False when nothing matches — including when a grant exists but the target set has changed.
    /// </summary>
    Task<bool> TryConsumeAsync(string actionId, string resource, string fingerprint, CancellationToken ct = default);

    /// <summary>An existing pending request for the same action/resource/targets, if any.</summary>
    Task<ConsentRequest?> FindPendingAsync(string actionId, string resource, string fingerprint, CancellationToken ct = default);
}

/// <summary>
/// JSON persistence for consent requests at <c>~/.pks-cli/consent.json</c> (0600).
/// </summary>
/// <remarks>
/// Two properties carry the security weight. Approval binds to a <em>fingerprint of the resolved
/// targets</em>, so a grant approved for three files cannot be spent on a fourth that appeared
/// afterwards. And grants are use-counted and time-boxed, so "yes, delete that" never decays into
/// a standing permission the caller can replay.
/// </remarks>
public sealed class ConsentStore : IConsentStore
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public ConsentStore() : this(SecurityFiles.PathFor("consent.json"), TimeProvider.System) { }

    public ConsentStore(string path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Stable digest of a target set, order-insensitive.</summary>
    public static string Fingerprint(IReadOnlyList<string> targets)
    {
        var canonical = string.Join("\n", targets.Select(t => t.Trim('/')).OrderBy(t => t, StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private sealed class ConsentFile
    {
        public List<ConsentRequest> Requests { get; set; } = new();
        public DateTimeOffset UpdatedUtc { get; set; }
    }

    private async Task<ConsentFile> LoadAsync()
    {
        if (!File.Exists(_path)) return new ConsentFile();
        try
        {
            return JsonSerializer.Deserialize<ConsentFile>(await File.ReadAllTextAsync(_path), JsonOptions)
                   ?? new ConsentFile();
        }
        catch (JsonException) { return new ConsentFile(); }
    }

    private async Task SaveAsync(ConsentFile file)
    {
        SecurityFiles.EnsureDirectory(_path);
        file.UpdatedUtc = _time.GetUtcNow();
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(file, JsonOptions));
        SecurityFiles.Restrict(_path);
    }

    /// <summary>Lazily flip anything past its deadline; keeps state honest without a background job.</summary>
    private List<ConsentRequest> Reap(List<ConsentRequest> requests)
    {
        var now = _time.GetUtcNow();
        for (var i = 0; i < requests.Count; i++)
        {
            var r = requests[i];
            if (r.Status == ConsentStatus.Pending && r.ExpiresUtc <= now)
                requests[i] = r with { Status = ConsentStatus.Expired };
            else if (r.Status == ConsentStatus.Approved && r.GrantExpiresUtc is { } g && g <= now)
                requests[i] = r with { Status = ConsentStatus.Expired };
        }

        // Bound the file: keep every live request, and the 50 most recent resolved ones.
        var live = requests.Where(r => r.Status is ConsentStatus.Pending or ConsentStatus.Approved).ToList();
        var resolved = requests.Where(r => r.Status is not (ConsentStatus.Pending or ConsentStatus.Approved))
            .OrderByDescending(r => r.CreatedUtc).Take(50);
        return live.Concat(resolved).OrderByDescending(r => r.CreatedUtc).ToList();
    }

    private static string NewId()
    {
        Span<byte> buf = stackalloc byte[4];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    public async Task<ConsentRequest> CreateAsync(
        string actionId, string resource, string summary,
        IReadOnlyList<string> targets, TimeSpan pendingTtl, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var file = await LoadAsync();
            file.Requests = Reap(file.Requests);

            var now = _time.GetUtcNow();
            var fingerprint = Fingerprint(targets);

            // Re-use a live pending request for the identical ask, so a retrying agent doesn't
            // spray the human with a new id on every attempt.
            var existing = file.Requests.FirstOrDefault(r =>
                r.Status == ConsentStatus.Pending &&
                r.ActionId == actionId && r.Resource == resource && r.TargetsFingerprint == fingerprint);
            if (existing != null) return existing;

            var ids = file.Requests.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string id;
            do { id = NewId(); } while (ids.Contains(id));

            var request = new ConsentRequest
            {
                Id = id,
                ActionId = actionId,
                Resource = resource,
                Summary = summary,
                Targets = targets.ToArray(),
                TargetsFingerprint = fingerprint,
                Status = ConsentStatus.Pending,
                CreatedUtc = now,
                ExpiresUtc = now + pendingTtl,
                RemainingUses = 0,
                RequestedBy = Environment.GetEnvironmentVariable("SUDO_USER") ?? Environment.UserName,
            };

            file.Requests.Insert(0, request);
            await SaveAsync(file);
            return request;
        }
        finally { _lock.Release(); }
    }

    public async Task<ConsentRequest?> GetAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var file = await LoadAsync();
            file.Requests = Reap(file.Requests);
            return file.Requests.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<ConsentRequest>> ListAsync(bool includeResolved = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var file = await LoadAsync();
            file.Requests = Reap(file.Requests);
            return includeResolved
                ? file.Requests
                : file.Requests.Where(r => r.Status is ConsentStatus.Pending or ConsentStatus.Approved).ToList();
        }
        finally { _lock.Release(); }
    }

    public async Task<ConsentRequest> ApproveAsync(string id, int uses, TimeSpan grantTtl, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var file = await LoadAsync();
            file.Requests = Reap(file.Requests);
            var idx = file.Requests.FindIndex(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new InvalidOperationException($"No consent request '{id}'.");

            var r = file.Requests[idx];
            if (r.Status != ConsentStatus.Pending)
                throw new InvalidOperationException($"Consent request '{id}' is {r.Status.ToString().ToLowerInvariant()}, not pending.");

            var now = _time.GetUtcNow();
            var approved = r with
            {
                Status = ConsentStatus.Approved,
                ApprovedUtc = now,
                GrantExpiresUtc = now + grantTtl,
                RemainingUses = Math.Max(1, uses),
            };
            file.Requests[idx] = approved;
            await SaveAsync(file);
            return approved;
        }
        finally { _lock.Release(); }
    }

    public async Task<ConsentRequest> DenyAsync(string id, string? reason, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var file = await LoadAsync();
            file.Requests = Reap(file.Requests);
            var idx = file.Requests.FindIndex(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new InvalidOperationException($"No consent request '{id}'.");

            var denied = file.Requests[idx] with { Status = ConsentStatus.Denied, DeniedReason = reason };
            file.Requests[idx] = denied;
            await SaveAsync(file);
            return denied;
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> TryConsumeAsync(string actionId, string resource, string fingerprint, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var file = await LoadAsync();
            file.Requests = Reap(file.Requests);
            var now = _time.GetUtcNow();

            var idx = file.Requests.FindIndex(r =>
                r.ActionId == actionId &&
                r.Resource == resource &&
                r.TargetsFingerprint == fingerprint &&
                r.IsUsable(now));
            if (idx < 0) return false;

            var r = file.Requests[idx];
            var remaining = r.RemainingUses - 1;
            file.Requests[idx] = r with
            {
                RemainingUses = remaining,
                Status = remaining <= 0 ? ConsentStatus.Consumed : ConsentStatus.Approved,
            };
            await SaveAsync(file);
            return true;
        }
        finally { _lock.Release(); }
    }

    public async Task<ConsentRequest?> FindPendingAsync(string actionId, string resource, string fingerprint, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var file = await LoadAsync();
            file.Requests = Reap(file.Requests);
            return file.Requests.FirstOrDefault(r =>
                r.Status == ConsentStatus.Pending &&
                r.ActionId == actionId && r.Resource == resource && r.TargetsFingerprint == fingerprint);
        }
        finally { _lock.Release(); }
    }
}
