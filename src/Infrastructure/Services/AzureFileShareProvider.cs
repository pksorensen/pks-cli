using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Infrastructure.Services;

public class AzureFileShareProvider : IFileShareProvider
{
    private const string StorageKey = "fileshare.azure.credentials";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<AzureFileShareProvider> _logger;
    private readonly AzureFileShareAuthConfig _config;

    public string ProviderName => "Azure File Share";
    public string ProviderKey => "azure-fileshare";

    public AzureFileShareProvider(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<AzureFileShareProvider> logger,
        AzureFileShareAuthConfig? config = null)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _logger = logger;
        _config = config ?? new AzureFileShareAuthConfig();
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

        var storageToken = await GetAccessTokenAsync(_config.StorageScope, ct);
        if (string.IsNullOrEmpty(storageToken))
        {
            result.Errors.Add("Failed to obtain storage access token.");
            return result;
        }

        try
        {
            var credential = new BearerTokenCredential(storageToken);
            var shareOptions = new Azure.Storage.Files.Shares.ShareClientOptions();
            shareOptions.AddPolicy(new FileRequestIntentPolicy(), HttpPipelinePosition.PerCall);
            var shareClient = new Azure.Storage.Files.Shares.ShareClient(
                new Uri($"https://{request.AccountName}.file.core.windows.net/{request.ResourceName}"),
                credential,
                shareOptions);

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

    // ── Internal ARM helpers ────────────────────────────────────────────────

    public async Task<string?> GetAccessTokenAsync(string scope, CancellationToken ct = default)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null || string.IsNullOrEmpty(credentials.RefreshToken))
        {
            _logger.LogWarning("Cannot refresh token: no stored credentials or refresh token");
            return null;
        }

        try
        {
            var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _config.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credentials.RefreshToken,
                ["scope"] = scope
            });

            var tokenUrl = _config.GetTokenUrl(credentials.TenantId);
            var response = await _httpClient.PostAsync(tokenUrl, requestBody, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Token refresh failed: {StatusCode} {Response}", response.StatusCode, content);
                return null;
            }

            var tokenResponse = JsonSerializer.Deserialize<FileShareTokenResponse>(content);
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                _logger.LogError("Token refresh returned no access token");
                return null;
            }

            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken) &&
                tokenResponse.RefreshToken != credentials.RefreshToken)
            {
                credentials.RefreshToken = tokenResponse.RefreshToken;
            }
            credentials.LastRefreshedAt = DateTime.UtcNow;
            await _configurationService.SetAsync(StorageKey, JsonSerializer.Serialize(credentials), global: true);

            return tokenResponse.AccessToken;
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
            var json = await _configurationService.GetAsync(StorageKey);
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

    // ── PKCE Auth flow (ported from AzureFoundryAuthService) ───────────────

    private async Task<FileShareTokenResponse> InitiateLoginAsync(string tenantId, string? loginHint, CancellationToken ct)
    {
        var pkce = GeneratePkce();
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var port = GetFreePort();
        var redirectUri = $"http://localhost:{port}";

        var authorizeUrl = $"{_config.GetAuthorizeUrl(tenantId)}" +
            $"?client_id={Uri.EscapeDataString(_config.ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(_config.InitialScope)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(pkce.CodeChallenge)}" +
            $"&code_challenge_method=S256" +
            $"&prompt=select_account";

        if (!string.IsNullOrEmpty(loginHint))
            authorizeUrl += $"&login_hint={Uri.EscapeDataString(loginHint)}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        Console.WriteLine(authorizeUrl);
        TryOpenBrowser(authorizeUrl);

        var code = await WaitForCallbackAsync(listener, state, ct);
        return await ExchangeCodeForTokensAsync(code, redirectUri, pkce.CodeVerifier, tenantId, ct);
    }

    private async Task<string?> DiscoverTenantAsync(string email, CancellationToken ct)
    {
        try
        {
            var url = $"https://login.microsoftonline.com/common/userrealm/{Uri.EscapeDataString(email)}?api-version=1.0";
            var response = await _httpClient.GetAsync(url, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(content);
            var domain = doc.RootElement.TryGetProperty("DomainName", out var d) ? d.GetString() : null;
            if (string.IsNullOrEmpty(domain)) return null;

            var openIdUrl = $"https://login.microsoftonline.com/{Uri.EscapeDataString(domain)}/.well-known/openid-configuration";
            var openIdResponse = await _httpClient.GetAsync(openIdUrl, ct);
            var openIdContent = await openIdResponse.Content.ReadAsStringAsync(ct);
            if (!openIdResponse.IsSuccessStatusCode) return domain;

            using var openIdDoc = JsonDocument.Parse(openIdContent);
            var issuer = openIdDoc.RootElement.TryGetProperty("issuer", out var i) ? i.GetString() : null;
            if (!string.IsNullOrEmpty(issuer))
            {
                var parts = issuer.TrimEnd('/').Split('/');
                var tenantId = parts[^1];
                if (tenantId == "v2.0" && parts.Length >= 2) tenantId = parts[^2];
                if (!string.IsNullOrEmpty(tenantId)) return tenantId;
            }

            return domain;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant discovery failed for {Email}", email);
            return null;
        }
    }

    private async Task<FileShareTokenResponse> ExchangeCodeForTokensAsync(
        string code, string redirectUri, string codeVerifier, string tenantId, CancellationToken ct)
    {
        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _config.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = _config.InitialScope
        });

        var tokenUrl = _config.GetTokenUrl(tenantId);
        var response = await _httpClient.PostAsync(tokenUrl, requestBody, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var tokenResponse = JsonSerializer.Deserialize<FileShareTokenResponse>(content);
        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            throw new InvalidOperationException("Token exchange returned no access token");

        return tokenResponse;
    }

    private async Task<string> WaitForCallbackAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.CallbackTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var contextTask = listener.GetContextAsync();
            var completedTask = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, linkedCts.Token));

            if (completedTask != contextTask)
                throw new OperationCanceledException("Authentication callback timed out");

            var context = await contextTask;
            var query = context.Request.QueryString;
            var code = query["code"];
            var returnedState = query["state"];
            var error = query["error"];

            var responseHtml = "<html><body><h2>Authentication complete. You can close this tab.</h2></body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, linkedCts.Token);
            context.Response.Close();

            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException($"Authentication error: {error}");
            if (returnedState != expectedState)
                throw new InvalidOperationException("State mismatch — possible CSRF attack");
            if (string.IsNullOrEmpty(code))
                throw new InvalidOperationException("No authorization code received");

            return code;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static (string CodeVerifier, string CodeChallenge) GeneratePkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);
        return (codeVerifier, codeChallenge);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryOpenBrowser(string url)
    {
        var browserEnv = Environment.GetEnvironmentVariable("BROWSER");
        if (!string.IsNullOrEmpty(browserEnv))
        {
            try
            {
                Process.Start(new ProcessStartInfo(browserEnv, url) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
                return;
            }
            catch { }
        }
        try
        {
            if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
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
        Azure.Storage.Files.Shares.ShareDirectoryClient rootDir,
        StorageSyncRequest request,
        SyncResult result,
        Action<SyncProgressUpdate> progress,
        CancellationToken ct)
    {
        // Producer-consumer: enumeration writes to channel as files are discovered;
        // N consumer tasks start downloading immediately without waiting for enumeration to finish.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<(
            Azure.Storage.Files.Shares.ShareFileClient Client,
            string LocalPath,
            string RelPath)>(new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = false,
                AllowSynchronousContinuations = false
            });

        var discovered = 0;
        var downloaded = 0;
        var bytesTransferred = 0L;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        progress(new SyncProgressUpdate(0, 0, "Discovering..."));

        // Producer: enumerate remote files and push to channel
        async Task ProduceAsync(Azure.Storage.Files.Shares.ShareDirectoryClient dir, string localDir, string relBase)
        {
            await foreach (var item in dir.GetFilesAndDirectoriesAsync(cancellationToken: ct))
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
                    Interlocked.Increment(ref discovered);
                    await channel.Writer.WriteAsync(
                        (dir.GetFileClient(item.Name), Path.Combine(localDir, item.Name), rel), ct);
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

                try
                {
                    var dl = await client.DownloadAsync(cancellationToken: ct);
                    await using var fs = File.OpenWrite(localPath);
                    await dl.Value.Content.CopyToAsync(fs, ct);
                    var done = Interlocked.Increment(ref downloaded);
                    Interlocked.Add(ref bytesTransferred, dl.Value.ContentLength);
                    progress(new SyncProgressUpdate(done, Volatile.Read(ref discovered), rel));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download {File}", rel);
                    errors.Add($"Download failed: {rel} — {ex.Message}");
                }
            }
        }, ct));

        await Task.WhenAll(new[] { producer }.Concat(consumers));
        result.FilesDownloaded = downloaded;
        result.BytesTransferred += bytesTransferred;
        result.Errors.AddRange(errors);
    }

    private async Task UploadDirectoryAsync(
        Azure.Storage.Files.Shares.ShareDirectoryClient remoteDir,
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

    // ── Token credential wrapper for Azure.Storage.Files.Shares SDK ────────

    private sealed class BearerTokenCredential : Azure.Core.TokenCredential
    {
        private readonly string _token;

        public BearerTokenCredential(string token) => _token = token;

        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new Azure.Core.AccessToken(_token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(new Azure.Core.AccessToken(_token, DateTimeOffset.UtcNow.AddHours(1)));
    }
}
