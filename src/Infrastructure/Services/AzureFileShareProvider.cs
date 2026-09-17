using System.Text.Json;
using global::Azure.Core;
using global::Azure.Core.Pipeline;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services;

public class AzureFileShareProvider : IFileShareProvider
{
    private const string StorageKey = "fileshare.azure.credentials";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ISecretResolver _secrets;
    private readonly ILogger<AzureFileShareProvider> _logger;
    private readonly AzureFileShareAuthConfig _config;
    private readonly AzurePublicClientAuth _auth;

    public string ProviderName => "Azure File Share";
    public string ProviderKey => "azure-fileshare";

    public AzureFileShareProvider(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<AzureFileShareProvider> logger,
        ISecretResolver secrets,
        AzureFileShareAuthConfig? config = null)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _logger = logger;
        _config = config ?? new AzureFileShareAuthConfig();
        _secrets = secrets;
        _auth = new AzurePublicClientAuth(
            httpClient, _config.ClientId, _config.AuthorizeUrl, _config.TokenUrl, _config.CallbackTimeoutSeconds, logger);
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var credentials = await GetStoredCredentialsAsync();
        return credentials != null && !string.IsNullOrEmpty(credentials.RefreshToken);
    }

    public async Task<bool> AuthenticateAsync(IAnsiConsole console, CancellationToken ct = default)
    {
        var email = console.Prompt(
            new TextPrompt<string>("[cyan]Enter your email address[/] [dim](or press Enter to sign in with 'common' tenant)[/]:")
                .AllowEmpty());

        string tenantId;
        string? loginHint = null;

        if (!string.IsNullOrWhiteSpace(email))
        {
            loginHint = email.Trim();
            console.MarkupLine("[dim]Discovering tenant...[/]");
            var discovered = await DiscoverTenantAsync(loginHint, ct);
            if (!string.IsNullOrEmpty(discovered))
            {
                tenantId = discovered;
                console.MarkupLine($"[green]Found tenant: [bold]{Markup.Escape(tenantId)}[/][/]");
            }
            else
            {
                tenantId = "organizations";
                console.MarkupLine("[yellow]Could not discover tenant, using 'organizations'.[/]");
            }
        }
        else
        {
            tenantId = "organizations";
        }

        console.MarkupLine("[cyan]Starting Azure authentication...[/]");
        console.MarkupLine("[dim]A browser window will open. If it doesn't, use the URL printed below.[/]");
        console.WriteLine();

        FileShareTokenResponse authTokens;
        try
        {
            authTokens = await InitiateLoginAsync(tenantId, loginHint, ct);
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

        // Store initial credentials so token refresh works for subsequent calls
        await StoreCredentialsAsync(new FileShareStoredCredentials
        {
            TenantId = tenantId,
            RefreshToken = authTokens.RefreshToken ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow
        });

        var managementToken = await GetAccessTokenAsync(_config.ManagementScope, ct);
        if (string.IsNullOrEmpty(managementToken))
        {
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

        // Store complete credentials
        await StoreCredentialsAsync(new FileShareStoredCredentials
        {
            TenantId = tenantId,
            RefreshToken = authTokens.RefreshToken ?? string.Empty,
            SelectedSubscriptionId = selectedSubscription.SubscriptionId,
            SelectedSubscriptionName = selectedSubscription.DisplayName,
            SelectedStorageAccountName = selectedAccount.Name,
            SelectedStorageAccountResourceGroup = resourceGroup,
            CreatedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow
        });

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

    public async Task<IEnumerable<StorageResource>> ListResourcesAsync(CancellationToken ct = default)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null || string.IsNullOrEmpty(credentials.RefreshToken))
            return Enumerable.Empty<StorageResource>();

        var token = await GetAccessTokenAsync(_config.ManagementScope, ct);
        if (string.IsNullOrEmpty(token))
            return Enumerable.Empty<StorageResource>();

        try
        {
            var shares = await ListFileSharesAsync(
                token,
                credentials.SelectedSubscriptionId,
                credentials.SelectedStorageAccountResourceGroup,
                credentials.SelectedStorageAccountName,
                ct);

            return shares.Select(s => new StorageResource
            {
                ProviderKey = ProviderKey,
                ProviderName = ProviderName,
                AccountName = credentials.SelectedStorageAccountName,
                ResourceName = s.Name,
                Description = $"{s.Properties.ShareQuota} GiB · {s.Properties.EnabledProtocols}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list Azure file shares");
            return Enumerable.Empty<StorageResource>();
        }
    }

    public async Task<SyncResult> SyncAsync(StorageSyncRequest request, Action<SyncProgressUpdate> progress, CancellationToken ct = default)
    {
        var result = new SyncResult();
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null)
        {
            result.Errors.Add("Not authenticated. Run 'pks fileshare init' first.");
            return result;
        }

        // Fail fast on a dead refresh token rather than deep inside the transfer loop.
        if (string.IsNullOrEmpty(await GetAccessTokenAsync(_config.StorageScope, ct)))
        {
            result.Errors.Add("Failed to obtain storage access token.");
            return result;
        }

        try
        {
            var shareClient = CreateShareClient(request.AccountName, request.ResourceName);

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
            var shareClient = CreateShareClient(accountName, resourceName);

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

        var shareClient = CreateShareClient(accountName, resourceName);
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

        var shareClient = CreateShareClient(accountName, resourceName);
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
    /// Share client carrying a self-renewing OAuth bearer plus the backup request-intent Azure Files
    /// demands. Every share client goes through here so no code path can pin a token again.
    /// </summary>
    private global::Azure.Storage.Files.Shares.ShareClient CreateShareClient(string accountName, string resourceName)
    {
        var options = new global::Azure.Storage.Files.Shares.ShareClientOptions();
        options.AddPolicy(new FileRequestIntentPolicy(), HttpPipelinePosition.PerCall);
        return new global::Azure.Storage.Files.Shares.ShareClient(
            new Uri($"https://{accountName}.file.core.windows.net/{resourceName}"),
            new RefreshingTokenCredential(token => AcquireTokenAsync(_config.StorageScope, token)),
            options);
    }

    private static async Task<int> CountItemsAsync(
        global::Azure.Storage.Files.Shares.ShareDirectoryClient dir, CancellationToken ct)
    {
        var count = 0;
        await foreach (var _ in dir.GetFilesAndDirectoriesAsync(cancellationToken: ct))
            count++;
        return count;
    }

    // ── Internal ARM helpers ────────────────────────────────────────────────

    public async Task<string?> GetAccessTokenAsync(string scope, CancellationToken ct = default)
        => (await AcquireTokenAsync(scope, ct))?.Token;

    /// <summary>
    /// Exchange the stored refresh token for an access token, keeping the expiry the STS reported.
    /// Callers that hold a client open for a long time need that expiry to renew in time.
    /// </summary>
    private async Task<(string Token, DateTimeOffset ExpiresOn)?> AcquireTokenAsync(string scope, CancellationToken ct = default)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null || string.IsNullOrEmpty(credentials.RefreshToken))
        {
            _logger.LogWarning("Cannot refresh token: no stored credentials or refresh token");
            return null;
        }

        try
        {
            var refreshed = await _auth.RedeemRefreshTokenAsync(
                credentials.TenantId, SecretValue.From(credentials.RefreshToken), scope, ct);

            if (refreshed.NewRefreshToken is { HasValue: true } rotated)
            {
                credentials.RefreshToken = rotated.Reveal()!;
            }
            credentials.LastRefreshedAt = DateTime.UtcNow;
            await _configurationService.SetAsync(StorageKey, JsonSerializer.Serialize(credentials), global: true);

            return (refreshed.AccessToken, refreshed.ExpiresOn);
        }
        catch (AzureTokenEndpointException ex)
        {
            _logger.LogError("Token refresh failed: {StatusCode} {Response}", ex.StatusCode, ex.ResponseBody);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token refresh failed with exception");
            return null;
        }
    }

    public async Task<List<AzureSubscription>> ListSubscriptionsAsync(string accessToken, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com/subscriptions?api-version=2022-12-01");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _httpClient.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        var listResponse = JsonSerializer.Deserialize<AzureSubscriptionListResponse>(content);
        return listResponse?.Value ?? new List<AzureSubscription>();
    }

    public async Task<List<StorageAccountInfo>> ListStorageAccountsAsync(string accessToken, string subscriptionId, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Storage/storageAccounts?api-version=2023-01-01";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _httpClient.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        var listResponse = JsonSerializer.Deserialize<StorageAccountListResponse>(content);
        // Exclude BlobStorage accounts — they don't support file shares
        return (listResponse?.Value ?? new List<StorageAccountInfo>())
            .Where(a => !a.Kind.Equals("BlobStorage", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<List<AzureFileShareInfo>> ListFileSharesAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Storage/storageAccounts/{accountName}/fileServices/default/shares?api-version=2023-01-01";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _httpClient.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        var listResponse = JsonSerializer.Deserialize<AzureFileShareListResponse>(content);
        return listResponse?.Value ?? new List<AzureFileShareInfo>();
    }

    private async Task<FileShareStoredCredentials?> GetStoredCredentialsAsync()
    {
        try
        {
            var json = await _secrets.RevealAsync(StorageKey);
            if (string.IsNullOrEmpty(json))
                return null;
            return JsonSerializer.Deserialize<FileShareStoredCredentials>(json);
        }
        catch
        {
            return null;
        }
    }

    private async Task StoreCredentialsAsync(FileShareStoredCredentials credentials)
    {
        var json = JsonSerializer.Serialize(credentials);
        await _configurationService.SetAsync(StorageKey, json, global: true);
    }

    // ── Sign-in (shared mechanics in AzurePublicClientAuth) ──────────────────

    private async Task<FileShareTokenResponse> InitiateLoginAsync(string tenantId, string? loginHint, CancellationToken ct)
    {
        var login = await _auth.LoginInteractiveAsync(tenantId, _config.InitialScope, loginHint, ct);
        return new FileShareTokenResponse
        {
            AccessToken = login.AccessToken,
            RefreshToken = login.RefreshToken.Reveal(),
            ExpiresIn = login.ExpiresIn,
            TokenType = "Bearer"
        };
    }

    private Task<string?> DiscoverTenantAsync(string email, CancellationToken ct)
        => _auth.DiscoverTenantAsync(email, ct);

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
