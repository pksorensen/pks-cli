using System.Text.Json;
using Azure.Core;
using PKS.Infrastructure.Services;

namespace PKS.Infrastructure.Services.Agent;

/// <summary>
/// Adapts <see cref="IAzureFoundryAuthService"/>'s refresh-token flow to the
/// Azure SDK's <see cref="TokenCredential"/> contract, so AzureOpenAIClient
/// can fetch Bearer tokens for Foundry-hosted cognitive services endpoints
/// without needing `az login` / DefaultAzureCredential.
///
/// Two-level cache:
///   L1 — process-wide in-memory (static). Survives the ~66 calls a single
///        <c>pks persona score-all</c> process makes.
///   L2 — on-disk, shared across processes (~/.pks-cli/foundry-token-cache.json),
///        guarded by a cross-process file lock.
///
/// L2 exists because batch workloads fan out across MANY short-lived `pks`
/// processes (e.g. a workflow running <c>score-all</c> for dozens of posts in
/// parallel). Each process starts with a cold L1, so without L2 every process
/// independently redeems the stored refresh token at startup. AAD rotates
/// refresh tokens on redemption — so N concurrent redemptions of the same
/// token make the first win and the rest fail with <c>invalid_grant</c>, and
/// the racing writes of the rotated token back to the shared credential file
/// clobber each other, leaving a stale token that kills every later process.
/// L2 collapses N concurrent refreshes into ~1 per <see cref="CacheLifetime"/>:
/// the first process to miss takes the cross-process lock, refreshes once
/// (rotating the refresh token exactly once, no race), and writes the access
/// token to disk; everyone else reads that.
/// </summary>
public sealed class FoundryTokenCredential : TokenCredential
{
    private readonly IAzureFoundryAuthService _auth;

    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    // L1 in-memory cache. `s_cachedScope` participates in the key so a switch
    // to a different audience triggers a refresh rather than returning a token
    // minted for the wrong scope.
    private static string? s_cachedToken;
    private static string? s_cachedScope;
    private static DateTimeOffset s_cachedExpiry;

    /// <summary>
    /// Cache duration. AAD access tokens live ~1 hour; we cache for 50 min so
    /// the SDK's expiry comparisons + our skew never let a stale token slip
    /// through, leaving a 10-min margin for clock skew and slow downstream
    /// calls.
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(50);

    /// <summary>
    /// Skew before treating a cached token as stale and refreshing
    /// pre-emptively, so we never hand the SDK a token that expires mid-call.
    /// </summary>
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for the cross-process refresh lock before giving up.</summary>
    private static readonly TimeSpan CrossProcessLockTimeout = TimeSpan.FromSeconds(90);

    private static readonly string PksDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli");
    private static readonly string DiskCachePath = Path.Combine(PksDir, "foundry-token-cache.json");
    private static readonly string DiskLockPath = Path.Combine(PksDir, "foundry-token-cache.lock");

    public FoundryTokenCredential(IAzureFoundryAuthService auth)
    {
        _auth = auth;
    }

    private sealed class DiskToken
    {
        public string? Scope { get; set; }
        public string? Token { get; set; }
        public DateTimeOffset Expiry { get; set; }
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var scope = requestContext.Scopes.Length > 0
            ? requestContext.Scopes[0]
            : "https://cognitiveservices.azure.com/.default";

        // L1 fast path: in-memory, no IO.
        if (TryReadMemory(scope, out var cached)) return cached;

        // L2 fast path: disk cache shared across processes, no lock for a read.
        if (TryReadDisk(scope, out cached))
        {
            StoreMemory(scope, cached);
            return cached;
        }

        // Slow path. Serialize within the process first (L1), then across
        // processes (L2 file lock), re-checking the cache at each gate so only
        // one refresh happens per CacheLifetime regardless of fan-out.
        await RefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (TryReadMemory(scope, out cached)) return cached;
            if (TryReadDisk(scope, out cached)) { StoreMemory(scope, cached); return cached; }

            using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken);

            // Another process may have refreshed while we waited for the lock.
            if (TryReadDisk(scope, out cached)) { StoreMemory(scope, cached); return cached; }

            var token = await _auth.GetAccessTokenAsync(scope, cancellationToken);
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException(
                    "Could not obtain Foundry access token. Run `pks foundry login` first.");
            }

            var expiry = DateTimeOffset.UtcNow + CacheLifetime;
            var access = new AccessToken(token, expiry);
            StoreMemory(scope, access);
            WriteDisk(new DiskToken { Scope = scope, Token = token, Expiry = expiry });
            return access;
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    private static bool TryReadMemory(string scope, out AccessToken token)
    {
        var t = s_cachedToken; var s = s_cachedScope; var e = s_cachedExpiry;
        if (!string.IsNullOrEmpty(t)
            && string.Equals(s, scope, StringComparison.Ordinal)
            && e - DateTimeOffset.UtcNow > RefreshSkew)
        {
            token = new AccessToken(t!, e);
            return true;
        }
        token = default;
        return false;
    }

    private static void StoreMemory(string scope, AccessToken token)
    {
        s_cachedToken = token.Token;
        s_cachedScope = scope;
        s_cachedExpiry = token.ExpiresOn;
    }

    private static bool TryReadDisk(string scope, out AccessToken token)
    {
        token = default;
        try
        {
            if (!File.Exists(DiskCachePath)) return false;
            var dt = JsonSerializer.Deserialize<DiskToken>(File.ReadAllText(DiskCachePath));
            if (dt is null || string.IsNullOrEmpty(dt.Token)) return false;
            if (!string.Equals(dt.Scope, scope, StringComparison.Ordinal)) return false;
            if (dt.Expiry - DateTimeOffset.UtcNow <= RefreshSkew) return false;
            token = new AccessToken(dt.Token!, dt.Expiry);
            return true;
        }
        catch
        {
            // Missing/torn/garbage cache — treat as a miss and refresh.
            return false;
        }
    }

    private static void WriteDisk(DiskToken dt)
    {
        try
        {
            Directory.CreateDirectory(PksDir);
            // Atomic write so lock-free readers never see a half-written file.
            var tmp = DiskCachePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dt));
            File.Move(tmp, DiskCachePath, overwrite: true);
        }
        catch
        {
            // A cache write failure is non-fatal: L1 still serves this process.
        }
    }

    private static async Task<FileStream> AcquireCrossProcessLockAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(PksDir);
        var deadline = DateTimeOffset.UtcNow + CrossProcessLockTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // FileShare.None makes this an OS-level mutex across processes.
                return new FileStream(DiskLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (DateTimeOffset.UtcNow > deadline)
                    throw new TimeoutException("Timed out waiting for the Foundry token refresh lock.");
                await Task.Delay(150, ct);
            }
        }
    }
}
