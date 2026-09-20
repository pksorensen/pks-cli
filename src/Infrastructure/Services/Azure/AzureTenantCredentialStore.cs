using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;

namespace PKS.Infrastructure.Services.Azure;

/// <summary>What a command may know about a signed-in tenant: everything except the credential.</summary>
public sealed record AzureTenantInfo(string TenantId, string? TenantName, string? Email, DateTime CreatedAt, DateTime LastRefreshedAt);

/// <summary>
/// The tenant's refresh token is missing, expired or revoked. Only a new interactive sign-in helps,
/// and the message says which one.
/// </summary>
public sealed class AzureAuthExpiredException : Exception
{
    public string TenantId { get; }

    public AzureAuthExpiredException(string tenantId, Exception? innerException = null)
        : base($"Azure sign-in for tenant '{tenantId}' is missing, expired or revoked. " +
               $"Run the init command that registered the resource with `--reauth {tenantId}` " +
               $"(e.g. `pks loganalytics init --reauth {tenantId}` or `pks acs init --reauth {tenantId}`) to sign in again.", innerException)
    {
        TenantId = tenantId;
    }
}

/// <summary>
/// One Azure sign-in per tenant, shared by every Azure-facing feature (Log Analytics, App Insights,
/// file shares). The Foundry credential is deliberately not part of this: it lives under its own key
/// with its own scope and its own login command.
/// </summary>
public interface IAzureTenantCredentialStore
{
    Task<IReadOnlyList<AzureTenantInfo>> ListTenantsAsync();
    Task<bool> HasTenantAsync(string tenantId);

    /// <summary>A fresh access token for <paramref name="scope"/> in <paramref name="tenantId"/>,
    /// served from the shared token cache. Throws <see cref="AzureAuthExpiredException"/> when the
    /// tenant is unknown or its refresh token is dead.</summary>
    Task<string> GetAccessTokenAsync(string tenantId, string scope, CancellationToken ct = default);

    /// <summary>Interactive sign-in. <paramref name="tenantIdOrEmail"/> may be a tenant id, an email
    /// address to discover the tenant from, or null to prompt on <paramref name="console"/>.</summary>
    Task<AzureTenantInfo> LoginAsync(string? tenantIdOrEmail, IAnsiConsole console, CancellationToken ct = default);

    Task RemoveTenantAsync(string tenantId);

    /// <summary>Resolves a tenant for consumers that stored none: the only tenant when exactly one
    /// is known, else throws with a clear message.</summary>
    Task<string> ResolveTenantAsync(string? tenantId);
}

/// <summary>
/// Persisted shape of one tenant entry. The list of these is the value under
/// <see cref="AzureTenantCredentialStore.StorageKey"/>, serialized with
/// <see cref="SecretJson.Persistence"/> so the refresh token survives the round trip into the
/// encrypted store and masks everywhere else.
/// </summary>
public sealed class AzureTenantCredentials
{
    public string TenantId { get; set; } = string.Empty;
    public string? TenantName { get; set; }
    public string? Email { get; set; }
    public SecretValue RefreshToken { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastRefreshedAt { get; set; }

    public AzureTenantInfo ToInfo() => new(TenantId, TenantName, Email, CreatedAt, LastRefreshedAt);
}

/// <summary>
/// Tenant-keyed credential store on top of the encrypted secret store.
///
/// Locking: the store's own lock guards the read-modify-write of the tenant list. Token refreshes
/// run inside <see cref="AzureTokenCache"/>'s lock and take the list lock from there to write a
/// rotated refresh token back, so the order is always cache → list, and nothing here calls the
/// cache while holding the list lock.
///
/// On first access it moves a refresh token left behind by the pre-tenant-store fileshare feature
/// (<c>fileshare.azure.credentials</c>) into the tenant list and blanks it at the source, so the
/// same refresh token never sits in two places. It never deletes that key: the subscription and
/// storage-account selection on it are still the fileshare feature's configuration.
/// </summary>
public sealed class AzureTenantCredentialStore : IAzureTenantCredentialStore
{
    public const string StorageKey = "azure.tenants.credentials";
    public const string FileShareStorageKey = "fileshare.azure.credentials";
    public const string LoginScope = "https://management.azure.com/.default offline_access";
    private const string FallbackTenant = "organizations";

    private readonly IConfigurationService _configurationService;
    private readonly ISecretResolver _secrets;
    private readonly AzureTokenCache _cache;
    private readonly ILogger<AzureTenantCredentialStore> _logger;
    private readonly AzurePublicClientAuth _auth;
    private readonly SemaphoreSlim _listLock = new(1, 1);
    private bool _migrationChecked;

    public AzureTenantCredentialStore(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ISecretResolver secrets,
        AzureTokenCache cache,
        ILogger<AzureTenantCredentialStore> logger)
    {
        _configurationService = configurationService;
        _secrets = secrets;
        _cache = cache;
        _logger = logger;
        _auth = new AzurePublicClientAuth(httpClient, logger: logger);
    }

    public async Task<IReadOnlyList<AzureTenantInfo>> ListTenantsAsync()
    {
        var list = await WithListAsync(list => Task.FromResult(list.Select(t => t.ToInfo()).ToList()));
        return list;
    }

    public async Task<bool> HasTenantAsync(string tenantId)
        => await WithListAsync(list => Task.FromResult(Find(list, tenantId) is not null));

    public async Task<string> GetAccessTokenAsync(string tenantId, string scope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        // Existence first, outside the cache: a removed tenant must not be served a cached token.
        if (!await HasTenantAsync(tenantId))
            throw new AzureAuthExpiredException(tenantId);

        return await _cache.GetOrRefreshAsync(tenantId, scope, async token =>
        {
            var refreshToken = await WithListAsync(list =>
                Task.FromResult(Find(list, tenantId)?.RefreshToken ?? SecretValue.None));
            if (!refreshToken.HasValue)
                throw new AzureAuthExpiredException(tenantId);

            AzurePublicClientAuth.RefreshResult refreshed;
            try
            {
                refreshed = await _auth.RedeemRefreshTokenAsync(tenantId, refreshToken, scope, token);
            }
            catch (AzureRefreshTokenExpiredException ex)
            {
                _logger.LogError("Azure refresh token for tenant {TenantId} has expired or been revoked. Re-auth with: pks loganalytics init --reauth {TenantId}", tenantId, tenantId);
                throw new AzureAuthExpiredException(tenantId, ex);
            }

            if (refreshed.NewRefreshToken is { HasValue: true } rotated)
            {
                await WithListAsync(async list =>
                {
                    var entry = Find(list, tenantId);
                    if (entry is null) return false;
                    entry.RefreshToken = rotated;
                    entry.LastRefreshedAt = DateTime.UtcNow;
                    await SaveAsync(list);
                    return true;
                });
            }

            return (refreshed.AccessToken, refreshed.ExpiresOn);
        }, ct);
    }

    public async Task<AzureTenantInfo> LoginAsync(string? tenantIdOrEmail, IAnsiConsole console, CancellationToken ct = default)
    {
        var input = tenantIdOrEmail?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            input = console.Prompt(
                new TextPrompt<string>("[cyan]Enter your email address[/] [dim](or press Enter to sign in with the 'organizations' tenant)[/]:")
                    .AllowEmpty()).Trim();
        }

        string tenantId;
        string? loginHint = null;
        if (string.IsNullOrEmpty(input))
        {
            tenantId = FallbackTenant;
        }
        else if (input.Contains('@'))
        {
            loginHint = input;
            console.MarkupLine("[dim]Discovering tenant...[/]");
            var discovered = await _auth.DiscoverTenantAsync(input, ct);
            if (!string.IsNullOrEmpty(discovered))
            {
                tenantId = discovered;
                console.MarkupLine($"[green]Found tenant: [bold]{Markup.Escape(tenantId)}[/][/]");
            }
            else
            {
                tenantId = FallbackTenant;
                console.MarkupLine("[yellow]Could not discover tenant, using 'organizations'.[/]");
            }
        }
        else
        {
            // A tenant id (GUID), a verified domain, or one of the AAD aliases.
            tenantId = input;
        }

        console.MarkupLine("[cyan]Starting Azure authentication...[/]");
        console.MarkupLine("[dim]A browser window will open. If it doesn't, use the URL printed below.[/]");
        console.WriteLine();

        var login = await _auth.LoginInteractiveAsync(tenantId, LoginScope, loginHint, ct);
        if (!login.RefreshToken.HasValue)
            throw new InvalidOperationException("Azure sign-in returned no refresh token; the account cannot be kept signed in.");

        // "organizations"/"common"/a domain is what the user typed, not a tenant. The access token
        // names the real tenant, and that is what consumers store and what HasTenantAsync is asked.
        var claims = AzureJwt.ReadPayload(login.AccessToken);
        var realTenant = AzureJwt.GetString(claims, "tid");
        if (!string.IsNullOrEmpty(realTenant)) tenantId = realTenant;
        var email = loginHint
            ?? AzureJwt.GetString(claims, "preferred_username")
            ?? AzureJwt.GetString(claims, "upn")
            ?? AzureJwt.GetString(claims, "email");

        var now = DateTime.UtcNow;
        var info = await WithListAsync(async list =>
        {
            var entry = Find(list, tenantId);
            if (entry is null)
            {
                entry = new AzureTenantCredentials { TenantId = tenantId, CreatedAt = now };
                list.Add(entry);
            }
            entry.RefreshToken = login.RefreshToken;
            entry.LastRefreshedAt = now;
            if (!string.IsNullOrEmpty(email)) entry.Email = email;
            await SaveAsync(list);
            return entry.ToInfo();
        });

        console.MarkupLine($"[green]Signed in to tenant [bold]{Markup.Escape(info.TenantId)}[/][/]");
        return info;
    }

    public Task RemoveTenantAsync(string tenantId)
        => WithListAsync(async list =>
        {
            var removed = list.RemoveAll(t => string.Equals(t.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) await SaveAsync(list);
            return removed;
        });

    public async Task<string> ResolveTenantAsync(string? tenantId)
    {
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            if (!await HasTenantAsync(tenantId))
                throw new AzureAuthExpiredException(tenantId.Trim());
            return tenantId.Trim();
        }

        var tenants = await ListTenantsAsync();
        return tenants.Count switch
        {
            0 => throw new InvalidOperationException(
                "No Azure tenant is signed in. Run `pks loganalytics init` to sign in."),
            1 => tenants[0].TenantId,
            _ => throw new InvalidOperationException(
                "Several Azure tenants are signed in: " + string.Join(", ", tenants.Select(t => t.TenantId)) +
                ". Pass --tenant <id> to choose one."),
        };
    }

    // ── List access ──────────────────────────────────────────────────────────

    private static AzureTenantCredentials? Find(List<AzureTenantCredentials> list, string tenantId)
        => list.FirstOrDefault(t => string.Equals(t.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs <paramref name="work"/> against the loaded list under the list lock, after the
    /// one-time fileshare migration. Callers must not touch the token cache inside.</summary>
    private async Task<T> WithListAsync<T>(Func<List<AzureTenantCredentials>, Task<T>> work)
    {
        await _listLock.WaitAsync();
        try
        {
            var list = await LoadAsync();
            if (!_migrationChecked)
            {
                await MigrateFileShareTokenAsync(list);
                _migrationChecked = true;
            }
            return await work(list);
        }
        finally
        {
            _listLock.Release();
        }
    }

    private async Task<List<AzureTenantCredentials>> LoadAsync()
    {
        try
        {
            var json = await _secrets.RevealAsync(StorageKey);
            if (string.IsNullOrWhiteSpace(json)) return new List<AzureTenantCredentials>();
            return JsonSerializer.Deserialize<List<AzureTenantCredentials>>(json, SecretJson.Persistence)
                   ?? new List<AzureTenantCredentials>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Stored Azure tenant credentials are unreadable; treating as empty");
            return new List<AzureTenantCredentials>();
        }
    }

    private async Task SaveAsync(List<AzureTenantCredentials> list)
    {
        // The one place tenant credentials are written unmasked, and it writes into the encrypted
        // store (the key classifies as secret; encrypt: true says so at the call site too).
        var json = JsonSerializer.Serialize(list, SecretJson.Persistence);
        await _configurationService.SetAsync(StorageKey, json, global: true, encrypt: true);
    }

    /// <summary>
    /// Moves the refresh token the fileshare feature stored on its own key into the tenant list,
    /// then blanks it at the source with every other field intact. Runs once per instance; a no-op
    /// when there is nothing to move or the tenant is already known.
    /// </summary>
    private async Task MigrateFileShareTokenAsync(List<AzureTenantCredentials> list)
    {
        FileShareStoredCredentials? fileShare;
        try
        {
            var json = await _secrets.RevealAsync(FileShareStorageKey);
            if (string.IsNullOrWhiteSpace(json)) return;
            fileShare = JsonSerializer.Deserialize<FileShareStoredCredentials>(json);
        }
        catch (JsonException)
        {
            return;
        }

        if (fileShare is null || string.IsNullOrEmpty(fileShare.RefreshToken) || string.IsNullOrWhiteSpace(fileShare.TenantId))
            return;
        if (Find(list, fileShare.TenantId) is not null)
            return;

        list.Add(new AzureTenantCredentials
        {
            TenantId = fileShare.TenantId,
            RefreshToken = SecretValue.From(fileShare.RefreshToken),
            CreatedAt = fileShare.CreatedAt == default ? DateTime.UtcNow : fileShare.CreatedAt,
            LastRefreshedAt = fileShare.LastRefreshedAt == default ? DateTime.UtcNow : fileShare.LastRefreshedAt
        });
        await SaveAsync(list);

        // Only now, with the token safely in the tenant list, take it out of the fileshare entry.
        fileShare.RefreshToken = string.Empty;
        await _configurationService.SetAsync(FileShareStorageKey, JsonSerializer.Serialize(fileShare), global: true);
        _logger.LogInformation("Moved the Azure file share sign-in for tenant {TenantId} into the shared tenant store", fileShare.TenantId);
    }
}

/// <summary>Reads claims off an access token without validating it — the token came from the STS
/// over TLS a moment ago; this is for the tenant id and expiry it carries, not for trust.</summary>
internal static class AzureJwt
{
    public static JsonElement? ReadPayload(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    public static string? GetString(JsonElement? payload, string claim)
        => payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(claim, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>The <c>exp</c> claim as a timestamp, or null when the token is not a JWT.</summary>
    public static DateTimeOffset? GetExpiry(string? token)
    {
        var payload = ReadPayload(token);
        return payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }
}
