using System.Text.Json;
using global::Azure.Core;
using global::Azure.Core.Pipeline;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Azure Files over every storage account the <see cref="IAzureResourceRegistry"/> has enabled.
/// Each account carries the tenant it was discovered in, so two accounts in two tenants get two
/// different bearer tokens; the legacy single selection in <c>fileshare.azure.credentials</c> is
/// only written here (by <see cref="AuthenticateAsync"/>) — the registry's one-shot migration
/// reads it, and everything else in this class reads the registry.
/// </summary>
public class AzureFileShareProvider : IFileShareProvider
{
    private const string StorageKey = "fileshare.azure.credentials";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<AzureFileShareProvider> _logger;
    private readonly AzureFileShareAuthConfig _config;
    private readonly IAzureTenantCredentialStore _tenants;
    private readonly IAzureResourceRegistry _resources;

    /// <summary>One self-renewing storage credential per tenant: accounts in the same tenant share
    /// a token, accounts in different tenants never do.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RefreshingTokenCredential> _storageCredentials =
        new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => "Azure File Share";
    public string ProviderKey => "azure-fileshare";

    public AzureFileShareProvider(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<AzureFileShareProvider> logger,
        IAzureTenantCredentialStore tenants,
        IAzureResourceRegistry resources,
        AzureFileShareAuthConfig? config = null)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _logger = logger;
        _config = config ?? new AzureFileShareAuthConfig();
        _tenants = tenants;
        _resources = resources;
    }

    /// <summary>
    /// Signed in means at least one enabled storage account whose tenant the tenant store holds
    /// (the only signed-in tenant, for an entry lifted from the legacy selection that never recorded
    /// one). The refresh token itself lives in the tenant store; the registry carries the selection.
    /// </summary>
    public async Task<bool> IsAuthenticatedAsync()
    {
        foreach (var entry in await _resources.ListEnabledAsync(AzureResourceKind.Storage))
        {
            if (await TryResolveTenantAsync(entry) is not null)
                return true;
        }
        return false;
    }

    /// <summary>The tenant an entry belongs to, or null when that tenant is not signed in (or the
    /// entry recorded none and several are) — a per-account condition, never a provider-wide one.</summary>
    private async Task<string?> TryResolveTenantAsync(AzureResourceEntry entry)
    {
        try
        {
            return await _tenants.ResolveTenantAsync(entry.TenantId);
        }
        catch (AzureAuthExpiredException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<bool> AuthenticateAsync(IAnsiConsole console, CancellationToken ct = default)
    {
        AzureTenantInfo tenant;
        try
        {
            tenant = await _tenants.LoginAsync(null, console, ct);
        }
        catch (OperationCanceledException)
        {
            console.MarkupLine("[red]Authentication timed out.[/]");
            return false;
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Authentication failed: {Markup.Escape(ex.Message)}[/]");
            return false;
        }
        var tenantId = tenant.TenantId;

        string managementToken;
        try
        {
            managementToken = await _tenants.GetAccessTokenAsync(tenantId, _config.ManagementScope, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to obtain management access token");
            console.MarkupLine("[red]Failed to obtain management access token.[/]");
            return false;
        }

        // Select subscription
        var subscriptions = await ListSubscriptionsAsync(managementToken, ct);
        if (subscriptions.Count == 0)
        {
            console.MarkupLine("[red]No Azure subscriptions found for this account.[/]");
            return false;
        }

        AzureSubscription selectedSubscription;
        if (subscriptions.Count == 1)
        {
            selectedSubscription = subscriptions[0];
            console.MarkupLine($"[dim]Using subscription: [bold]{Markup.Escape(selectedSubscription.DisplayName)}[/][/]");
        }
        else
        {
            var subName = console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Azure subscription:[/]")
                    .AddChoices(subscriptions.Select(s => s.DisplayName)));
            selectedSubscription = subscriptions.First(s => s.DisplayName == subName);
        }

        // Select storage account
        var accounts = await ListStorageAccountsAsync(managementToken, selectedSubscription.SubscriptionId, ct);
        if (accounts.Count == 0)
        {
            console.MarkupLine("[red]No storage accounts with file share support found in this subscription.[/]");
            return false;
        }

        StorageAccountInfo selectedAccount;
        if (accounts.Count == 1)
        {
            selectedAccount = accounts[0];
            console.MarkupLine($"[dim]Using storage account: [bold]{Markup.Escape(selectedAccount.Name)}[/][/]");
        }
        else
        {
            var accountName = console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select a storage account:[/]")
                    .AddChoices(accounts.Select(a => a.Name)));
            selectedAccount = accounts.First(a => a.Name == accountName);
        }

        var resourceGroup = ParseResourceGroup(selectedAccount.Id);

        // Store the selection. The refresh token stays in the tenant store, keyed by tenant.
        await StoreCredentialsAsync(new FileShareStoredCredentials
        {
            TenantId = tenantId,
            RefreshToken = string.Empty,
            SelectedSubscriptionId = selectedSubscription.SubscriptionId,
            SelectedSubscriptionName = selectedSubscription.DisplayName,
            SelectedStorageAccountName = selectedAccount.Name,
            SelectedStorageAccountResourceGroup = resourceGroup,
            CreatedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow
        });

        // The registry is what list/ls/sync/rm read; this makes the account one of the enabled set.
        await _resources.UpsertAsync(new[]
        {
            new AzureResourceEntry
            {
                Kind = AzureResourceKind.Storage,
                Key = selectedAccount.Name,
                Name = selectedAccount.Name,
                ResourceId = selectedAccount.Id,
                TenantId = tenantId,
                SubscriptionId = selectedSubscription.SubscriptionId,
                SubscriptionName = selectedSubscription.DisplayName,
                ResourceGroup = resourceGroup,
                Enabled = true,
                DiscoveredAt = DateTime.UtcNow
            }
        });
        await _resources.SetEnabledAsync(AzureResourceKind.Storage, selectedAccount.Name, true);

        console.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold green]Authentication Successful[/]");
        table.AddColumn("[bold]Property[/]");
        table.AddColumn("[bold]Value[/]");
        table.AddRow("Tenant", Markup.Escape(tenantId));
        table.AddRow("Subscription", Markup.Escape(selectedSubscription.DisplayName));
        table.AddRow("Storage Account", Markup.Escape(selectedAccount.Name));
        table.AddRow("Resource Group", Markup.Escape(resourceGroup));
        console.Write(table);
        console.WriteLine();
        console.MarkupLine("[dim]Tip: Use [bold]pks storage list[/] to see available file shares.[/]");

        return true;
    }

    /// <summary>
    /// The shares of every enabled storage account, listed in parallel. An account that cannot be
    /// listed — tenant not signed in, token dead, ARM 403, network — is logged and skipped so one
    /// bad account never hides the others.
    /// </summary>
    public async Task<IEnumerable<StorageResource>> ListResourcesAsync(CancellationToken ct = default)
    {
        var entries = await _resources.ListEnabledAsync(AzureResourceKind.Storage);
        var perAccount = await Task.WhenAll(entries.Select(e => ListAccountSharesAsync(e, ct)));
        return perAccount.SelectMany(shares => shares).ToList();
    }

    private async Task<IReadOnlyList<StorageResource>> ListAccountSharesAsync(AzureResourceEntry entry, CancellationToken ct)
    {
        var tenantLabel = entry.TenantId ?? "(only signed-in tenant)";
        try
        {
            var tenantId = await _tenants.ResolveTenantAsync(entry.TenantId);
            tenantLabel = tenantId;

            if (string.IsNullOrWhiteSpace(entry.SubscriptionId) || string.IsNullOrWhiteSpace(entry.ResourceGroup))
                throw new InvalidOperationException("the registry entry records no subscription or resource group; run 'pks fileshare init' to rediscover it");

            var token = await _tenants.GetAccessTokenAsync(tenantId, _config.ManagementScope, ct);
            var shares = await ListFileSharesAsync(token, entry.SubscriptionId, entry.ResourceGroup, entry.Key, ct);

            var subscription = string.IsNullOrWhiteSpace(entry.SubscriptionName) ? null : $" · {entry.SubscriptionName}";
            return shares.Select(s => new StorageResource
            {
                ProviderKey = ProviderKey,
                ProviderName = ProviderName,
                AccountName = entry.Key,
                ResourceName = s.Name,
                Description = $"{s.Properties.ShareQuota} GiB · {s.Properties.EnabledProtocols}{subscription}"
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skipping storage account {Account} in tenant {Tenant}: {Reason}", entry.Key, tenantLabel, ex.Message);
            return Array.Empty<StorageResource>();
        }
    }

    public async Task<SyncResult> SyncAsync(StorageSyncRequest request, Action<SyncProgressUpdate> progress, CancellationToken ct = default)
    {
        var result = new SyncResult();
        var entry = await FindEnabledAccountAsync(request.AccountName);
        if (entry == null)
        {
            result.Errors.Add((await _resources.ListEnabledAsync(AzureResourceKind.Storage)).Count == 0
                ? "Not authenticated. Run 'pks fileshare init' first."
                : NotEnabledMessage(request.AccountName));
            return result;
        }

        // Fail fast on a dead refresh token rather than deep inside the transfer loop.
        var tenantId = await TryResolveTenantAsync(entry);
        if (tenantId == null || await AcquireTokenAsync(tenantId, _config.StorageScope, ct) == null)
        {
            result.Errors.Add("Failed to obtain storage access token.");
            return result;
        }

        try
        {
            var shareClient = await CreateShareClientAsync(request.AccountName, request.ResourceName);

            if (request.Direction is SyncDirection.Download or SyncDirection.Bidirectional)
                await DownloadParallelAsync(shareClient.GetRootDirectoryClient(), request, result, progress, ct);

            if (request.Direction is SyncDirection.Upload or SyncDirection.Bidirectional)
                await UploadDirectoryAsync(shareClient.GetRootDirectoryClient(), request.LocalDirectory, request, result, progress, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed");
            result.Errors.Add($"Sync error: {ex.Message}");
        }

        return result;
    }

    public async Task<StorageListResult> ListDirectoryAsync(
        string accountName, string resourceName,
        StorageListRequest request, CancellationToken ct = default)
    {
        var result = new StorageListResult
        {
            ShareName = resourceName,
            Path = request.Path
        };

        try
        {
            var shareClient = await CreateShareClientAsync(accountName, resourceName);

            var normalizedPath = request.Path.Trim('/');
            var dirClient = string.IsNullOrEmpty(normalizedPath)
                ? shareClient.GetRootDirectoryClient()
                : shareClient.GetDirectoryClient(normalizedPath);

            var count = 0;
            await foreach (var item in dirClient.GetFilesAndDirectoriesAsync(cancellationToken: ct))
            {
                if (request.DirsOnly && !item.IsDirectory)
                    continue;

                if (count >= request.Limit)
                {
                    result.Truncated = true;
                    break;
                }

                var listItem = new StorageListItem
                {
                    Name = item.Name,
                    Type = item.IsDirectory ? StorageItemType.Directory : StorageItemType.File,
                    SizeBytes = item.IsDirectory ? null : item.FileSize
                };

                if (request.IncludeCount && item.IsDirectory)
                {
                    listItem.ItemCount = await CountItemsAsync(
                        dirClient.GetSubdirectoryClient(item.Name), ct);
                }

                result.Items.Add(listItem);
                count++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListDirectory failed for {Path}", request.Path);
        }

        return result;
    }

    public async Task<IReadOnlyList<StorageFileRef>> EnumerateFilesAsync(
        string accountName, string resourceName, string path, bool recursive, CancellationToken ct = default)
    {
        var found = new List<StorageFileRef>();

        var shareClient = await CreateShareClientAsync(accountName, resourceName);
        var normalized = path.Trim('/');

        // A path that names a single file resolves to exactly that file.
        if (!string.IsNullOrEmpty(normalized))
        {
            var fileClient = shareClient.GetRootDirectoryClient().GetFileClient(normalized);
            try
            {
                if (await fileClient.ExistsAsync(ct))
                {
                    var props = await fileClient.GetPropertiesAsync(cancellationToken: ct);
                    found.Add(new StorageFileRef(normalized, props.Value.ContentLength));
                    return found;
                }
            }
            catch (global::Azure.RequestFailedException)
            {
                // Not addressable as a file; fall through and treat it as a directory.
            }
        }

        var dirClient = string.IsNullOrEmpty(normalized)
            ? shareClient.GetRootDirectoryClient()
            : shareClient.GetDirectoryClient(normalized);

        await CollectAsync(dirClient, normalized, found, recursive, ct);
        return found;
    }

    private static async Task CollectAsync(
        global::Azure.Storage.Files.Shares.ShareDirectoryClient dir,
        string relBase,
        List<StorageFileRef> into,
        bool recursive,
        CancellationToken ct)
    {
        await foreach (var item in dir.GetFilesAndDirectoriesAsync(cancellationToken: ct))
        {
            var rel = string.IsNullOrEmpty(relBase) ? item.Name : $"{relBase}/{item.Name}";
            if (item.IsDirectory)
            {
                if (recursive)
                    await CollectAsync(dir.GetSubdirectoryClient(item.Name), rel, into, true, ct);
            }
            else
            {
                into.Add(new StorageFileRef(rel, item.FileSize ?? 0));
            }
        }
    }

    public async Task<StorageDeleteResult> DeleteFilesAsync(
        string accountName, string resourceName, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var result = new StorageDeleteResult();

        var shareClient = await CreateShareClientAsync(accountName, resourceName);
        var root = shareClient.GetRootDirectoryClient();

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var rel = path.Trim('/');
            try
            {
                var fileClient = root.GetFileClient(rel);
                var size = 0L;
                try
                {
                    var props = await fileClient.GetPropertiesAsync(cancellationToken: ct);
                    size = props.Value.ContentLength;
                }
                catch (global::Azure.RequestFailedException) { /* size is best-effort */ }

                var deleted = await fileClient.DeleteIfExistsAsync(cancellationToken: ct);
                if (deleted.Value)
                {
                    result.FilesDeleted++;
                    result.BytesDeleted += size;
                }
                else
                {
                    result.Errors.Add($"Not found: {rel}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete {File}", rel);
                result.Errors.Add($"Delete failed: {rel} — {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Share client carrying a self-renewing OAuth bearer for the account's own tenant plus the
    /// backup request-intent Azure Files demands. Every share client goes through here so no code
    /// path can pin a token again, and no code path can reach an account the registry has not enabled.
    /// </summary>
    /// <exception cref="InvalidOperationException">The account is unknown to the registry or disabled.</exception>
    /// <exception cref="AzureAuthExpiredException">The account's tenant is not signed in.</exception>
    private async Task<global::Azure.Storage.Files.Shares.ShareClient> CreateShareClientAsync(string accountName, string resourceName)
    {
        var entry = await FindEnabledAccountAsync(accountName)
            ?? throw new InvalidOperationException(NotEnabledMessage(accountName));
        var tenantId = await _tenants.ResolveTenantAsync(entry.TenantId);

        var credential = _storageCredentials.GetOrAdd(tenantId,
            tenant => new RefreshingTokenCredential(token => AcquireTokenAsync(tenant, _config.StorageScope, token)));

        var options = new global::Azure.Storage.Files.Shares.ShareClientOptions();
        options.AddPolicy(new FileRequestIntentPolicy(), HttpPipelinePosition.PerCall);
        return new global::Azure.Storage.Files.Shares.ShareClient(
            new Uri($"https://{entry.Key}.file.core.windows.net/{resourceName}"),
            credential,
            options);
    }

    private async Task<AzureResourceEntry?> FindEnabledAccountAsync(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return null;
        var entry = await _resources.FindAsync(AzureResourceKind.Storage, accountName);
        return entry is { Enabled: true } ? entry : null;
    }

    private static string NotEnabledMessage(string accountName)
        => $"Storage account {accountName} is not enabled. Run pks fileshare init";

    private static async Task<int> CountItemsAsync(
        global::Azure.Storage.Files.Shares.ShareDirectoryClient dir, CancellationToken ct)
    {
        var count = 0;
        await foreach (var _ in dir.GetFilesAndDirectoriesAsync(cancellationToken: ct))
            count++;
        return count;
    }

    // ── Internal ARM helpers ────────────────────────────────────────────────

    /// <summary>An access token for <paramref name="scope"/> in the tenant of the enabled storage
    /// account <paramref name="accountName"/>; null when the account is not enabled or its tenant
    /// is not signed in.</summary>
    public async Task<string?> GetAccessTokenAsync(string accountName, string scope, CancellationToken ct = default)
    {
        var entry = await FindEnabledAccountAsync(accountName);
        if (entry == null)
        {
            _logger.LogWarning("Cannot obtain token: storage account {Account} is not enabled", accountName);
            return null;
        }

        var tenantId = await TryResolveTenantAsync(entry);
        if (tenantId == null)
        {
            _logger.LogError("Azure sign-in for storage account {Account} is missing or expired. Run 'pks fileshare init' to sign in again.", accountName);
            return null;
        }

        return (await AcquireTokenAsync(tenantId, scope, ct))?.Token;
    }

    /// <summary>
    /// An access token for <paramref name="scope"/> in <paramref name="tenantId"/> from the tenant
    /// store (which caches and refreshes it), with the expiry the token itself carries so a client
    /// held open across a long sync renews in time. Null when the tenant is not signed in — callers
    /// report that and stop.
    /// </summary>
    private async Task<(string Token, DateTimeOffset ExpiresOn)?> AcquireTokenAsync(string tenantId, string scope, CancellationToken ct = default)
    {
        try
        {
            var token = await _tenants.GetAccessTokenAsync(tenantId, scope, ct);

            // The STS's own expiry when the token is a JWT; otherwise renew well inside the cache's
            // 50-minute lifetime — a renewal is a cache hit, not a redemption.
            var expiresOn = AzureJwt.GetExpiry(token) ?? DateTimeOffset.UtcNow.AddMinutes(10);
            return (token, expiresOn);
        }
        catch (AzureAuthExpiredException ex)
        {
            _logger.LogError("Azure sign-in for tenant {TenantId} is missing or expired. Run 'pks fileshare init' to sign in again.", ex.TenantId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token refresh failed with exception");
            return null;
        }
    }

    public Task<List<AzureSubscription>> ListSubscriptionsAsync(string accessToken, CancellationToken ct = default)
        => AzureArmRequests.ListSubscriptionsAsync(_httpClient, accessToken, ct);

    public Task<List<StorageAccountInfo>> ListStorageAccountsAsync(string accessToken, string subscriptionId, CancellationToken ct = default)
        => AzureArmRequests.ListStorageAccountsAsync(_httpClient, accessToken, subscriptionId, ct);

    public Task<List<AzureFileShareInfo>> ListFileSharesAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken ct = default)
        => AzureArmRequests.ListFileSharesAsync(_httpClient, accessToken, subscriptionId, resourceGroup, accountName, ct);

    private async Task StoreCredentialsAsync(FileShareStoredCredentials credentials)
    {
        var json = JsonSerializer.Serialize(credentials);
        await _configurationService.SetAsync(StorageKey, json, global: true);
    }

    private static string ParseResourceGroup(string resourceId)
    {
        var parts = resourceId.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        }
        return string.Empty;
    }

    // ── File sync helpers ──────────────────────────────────────────────────

    private async Task DownloadParallelAsync(
        global::Azure.Storage.Files.Shares.ShareDirectoryClient rootDir,
        StorageSyncRequest request,
        SyncResult result,
        Action<SyncProgressUpdate> progress,
        CancellationToken ct)
    {
        // Producer-consumer: enumeration writes to channel as files are discovered;
        // N consumer tasks start downloading immediately without waiting for enumeration to finish.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<(
            global::Azure.Storage.Files.Shares.ShareFileClient Client,
            string LocalPath,
            string RelPath)>(new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = false,
                AllowSynchronousContinuations = false
            });

        var discovered = 0;
        var skipped = 0;
        var upToDate = 0;
        var downloaded = 0;
        var bytesTransferred = 0L;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Build glob matcher once (null = match everything)
        var matcher = BuildMatcher(request.Include, request.Exclude);

        progress(new SyncProgressUpdate(0, 0, "Discovering..."));

        // Ask for timestamps in the listing so up-to-date files can be recognised without a
        // per-file GetProperties round-trip.
        var listOptions = new global::Azure.Storage.Files.Shares.Models.ShareDirectoryGetFilesAndDirectoriesOptions
        {
            Traits = global::Azure.Storage.Files.Shares.Models.ShareFileTraits.Timestamps
        };

        // Producer: enumerate remote files and push to channel (filtered)
        async Task ProduceAsync(global::Azure.Storage.Files.Shares.ShareDirectoryClient dir, string localDir, string relBase)
        {
            await foreach (var item in dir.GetFilesAndDirectoriesAsync(listOptions, ct))
            {
                var rel = relBase.Length == 0 ? item.Name : $"{relBase}/{item.Name}";
                if (item.IsDirectory)
                {
                    var subLocal = Path.Combine(localDir, item.Name);
                    Directory.CreateDirectory(subLocal);
                    await ProduceAsync(dir.GetSubdirectoryClient(item.Name), subLocal, rel);
                }
                else
                {
                    if (matcher != null && !MatcherExtensions.Match(matcher, rel).HasMatches)
                    {
                        Interlocked.Increment(ref skipped);
                        continue;
                    }

                    var localPath = Path.Combine(localDir, item.Name);
                    if (!request.Force && IsLocalCopyCurrent(localPath, item.FileSize, item.Properties?.LastModified))
                    {
                        Interlocked.Increment(ref upToDate);
                        continue;
                    }

                    Interlocked.Increment(ref discovered);
                    await channel.Writer.WriteAsync((dir.GetFileClient(item.Name), localPath, rel), ct);
                }
            }
        }

        var producer = Task.Run(async () =>
        {
            try { await ProduceAsync(rootDir, request.LocalDirectory, string.Empty); }
            finally { channel.Writer.Complete(); }
        }, ct);

        // Consumers: MaxParallelism tasks reading from channel
        var consumers = Enumerable.Range(0, request.MaxParallelism).Select(_ => Task.Run(async () =>
        {
            await foreach (var (client, localPath, rel) in channel.Reader.ReadAllAsync(ct))
            {
                var disc = Volatile.Read(ref discovered);
                if (request.DryRun)
                {
                    var done = Interlocked.Increment(ref downloaded);
                    progress(new SyncProgressUpdate(done, disc, rel));
                    continue;
                }

                // Download beside the target and rename into place, so an interrupted run can never
                // leave a right-sized-but-truncated file that the next run would treat as complete.
                var tempPath = localPath + ".pks-part";
                try
                {
                    var dl = await client.DownloadAsync(cancellationToken: ct);
                    await using (var fs = new FileStream(
                        tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await dl.Value.Content.CopyToAsync(fs, ct);
                    }

                    File.Move(tempPath, localPath, overwrite: true);
                    var done = Interlocked.Increment(ref downloaded);
                    Interlocked.Add(ref bytesTransferred, dl.Value.ContentLength);
                    progress(new SyncProgressUpdate(done, Volatile.Read(ref discovered), rel));
                }
                catch (Exception ex)
                {
                    TryDelete(tempPath);
                    _logger.LogError(ex, "Failed to download {File}", rel);
                    errors.Add($"Download failed: {rel} — {ex.Message}");
                }
            }
        }, ct));

        await Task.WhenAll(new[] { producer }.Concat(consumers));
        result.FilesDownloaded = downloaded;
        result.FilesSkipped += skipped;
        result.FilesUpToDate += upToDate;
        result.BytesTransferred += bytesTransferred;
        result.Errors.AddRange(errors);
    }

    /// <summary>
    /// Decides whether an existing local file can stand in for the remote one, so an interrupted
    /// sync resumes instead of starting over. Size is the primary signal — the listing always
    /// carries it — and the remote timestamp is used as a tiebreaker when the service returns one:
    /// a remote file modified after the local copy was written is re-fetched even at equal size.
    /// </summary>
    internal static bool IsLocalCopyCurrent(string localPath, long? remoteSize, DateTimeOffset? remoteModified)
    {
        if (remoteSize is not { } size)
            return false;

        var local = new FileInfo(localPath);
        if (!local.Exists || local.Length != size)
            return false;

        // Two seconds of slack: filesystems and the service do not agree on timestamp resolution,
        // and the local mtime is stamped when the download finished, not when the blob changed.
        if (remoteModified is { } modified && modified > local.LastWriteTimeUtc.AddSeconds(2))
            return false;

        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort — a stray .pks-part is harmless */ }
    }

    private static Matcher? BuildMatcher(string[] include, string[] exclude)
    {
        if (include.Length == 0 && exclude.Length == 0)
            return null;

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);

        if (include.Length > 0)
            foreach (var p in include) matcher.AddInclude(p);
        else
            matcher.AddInclude("**");

        foreach (var p in exclude)
            matcher.AddExclude(p);

        return matcher;
    }

    private async Task UploadDirectoryAsync(
        global::Azure.Storage.Files.Shares.ShareDirectoryClient remoteDir,
        string localDir,
        StorageSyncRequest request,
        SyncResult result,
        Action<SyncProgressUpdate> progress,
        CancellationToken ct)
    {
        if (!Directory.Exists(localDir)) return;

        await remoteDir.CreateIfNotExistsAsync(cancellationToken: ct);

        foreach (var localFile in Directory.GetFiles(localDir))
        {
            var fileName = Path.GetFileName(localFile);
            var fileClient = remoteDir.GetFileClient(fileName);
            var fileInfo = new FileInfo(localFile);

            if (request.DryRun)
            {
                progress(new SyncProgressUpdate(result.FilesUploaded + 1, 0, fileName));
                result.FilesUploaded++;
                continue;
            }

            try
            {
                progress(new SyncProgressUpdate(result.FilesUploaded + 1, 0, fileName));
                await fileClient.CreateAsync(fileInfo.Length, cancellationToken: ct);
                await using var fs = File.OpenRead(localFile);
                await fileClient.UploadAsync(fs, cancellationToken: ct);
                result.FilesUploaded++;
                result.BytesTransferred += fileInfo.Length;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload {File}", fileName);
                result.Errors.Add($"Upload failed: {fileName} — {ex.Message}");
            }
        }

        foreach (var subDir in Directory.GetDirectories(localDir))
        {
            var dirName = Path.GetFileName(subDir);
            await UploadDirectoryAsync(remoteDir.GetSubdirectoryClient(dirName), subDir, request, result, progress, ct);
        }
    }

    // ── Azure File Share pipeline policies ─────────────────────────────────

    // Azure requires x-ms-file-request-intent: backup when using OAuth (bearer token) auth
    private sealed class FileRequestIntentPolicy : HttpPipelinePolicy
    {
        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (!message.Request.Headers.Contains("x-ms-file-request-intent"))
                message.Request.Headers.Add("x-ms-file-request-intent", "backup");
            ProcessNext(message, pipeline);
        }

        public override ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            if (!message.Request.Headers.Contains("x-ms-file-request-intent"))
                message.Request.Headers.Add("x-ms-file-request-intent", "backup");
            return ProcessNextAsync(message, pipeline);
        }
    }

    // ── Token credential wrapper for global::Azure.Storage.Files.Shares SDK ────────

    /// <summary>
    /// Mints storage tokens on demand from the stored refresh token, reporting the token's REAL
    /// expiry so the SDK's bearer policy can refresh before it lapses.
    /// </summary>
    /// <remarks>
    /// The previous implementation captured one token string and reported <c>UtcNow.AddHours(1)</c>
    /// on every call. That expiry is recomputed each time it is asked, so the SDK's cache never saw
    /// a token approaching expiry and never refreshed — any operation still running when the real
    /// token lapsed (an hour in, mid-way through a large sync) died with 401/403. Reporting the
    /// truth is the whole fix; <c>BearerTokenAuthenticationPolicy</c> does the rest.
    /// </remarks>
    private sealed class RefreshingTokenCredential : global::Azure.Core.TokenCredential
    {
        private readonly Func<CancellationToken, Task<(string Token, DateTimeOffset ExpiresOn)?>> _acquire;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private global::Azure.Core.AccessToken _cached;

        /// <summary>Renew this far ahead of expiry, covering clock skew and an in-flight request.</summary>
        private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

        public RefreshingTokenCredential(Func<CancellationToken, Task<(string, DateTimeOffset)?>> acquire)
            => _acquire = acquire;

        public override global::Azure.Core.AccessToken GetToken(global::Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
            => GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<global::Azure.Core.AccessToken> GetTokenAsync(
            global::Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            if (IsFresh(_cached)) return _cached;

            await _lock.WaitAsync(cancellationToken);
            try
            {
                // Another caller may have renewed while we waited — parallel downloads all miss at once.
                if (IsFresh(_cached)) return _cached;

                var acquired = await _acquire(cancellationToken)
                    ?? throw new InvalidOperationException(
                        "Could not refresh the storage access token. Run 'pks fileshare init' to sign in again.");

                _cached = new global::Azure.Core.AccessToken(acquired.Token, acquired.ExpiresOn);
                return _cached;
            }
            finally { _lock.Release(); }
        }

        private static bool IsFresh(global::Azure.Core.AccessToken token)
            => !string.IsNullOrEmpty(token.Token) && token.ExpiresOn - RenewBefore > DateTimeOffset.UtcNow;
    }
}
