using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Talks to the Hetzner Cloud API (https://api.hetzner.cloud/v1) with a per-project
/// bearer token. Billing model that shapes this service: a server is billed by the hour,
/// capped at the monthly price, <b>until it is deleted — a powered-off server still costs
/// the full rate</b>. The ephemeral pattern is therefore create → use → delete, with a
/// snapshot or a volume as the cheap persistent layer in between.
/// </summary>
public interface IHetznerCloudService
{
    // Credentials
    Task<bool> IsAuthenticatedAsync();
    Task<HetznerStoredCredentials?> GetStoredCredentialsAsync();
    Task StoreCredentialsAsync(HetznerStoredCredentials credentials);
    Task ClearStoredCredentialsAsync();

    // Catalog (location/types/images/prices)
    /// <summary>Lists locations; <paramref name="token"/> lets <c>hetzner init</c> validate a token before it is stored.</summary>
    Task<List<HetznerLocation>> ListLocationsAsync(string? token = null, CancellationToken ct = default);
    Task<List<HetznerServerType>> ListServerTypesAsync(CancellationToken ct = default);
    Task<List<HetznerImage>> ListImagesAsync(string architecture = "x86", CancellationToken ct = default);
    Task<HetznerPricing> GetPricingAsync(CancellationToken ct = default);

    // SSH keys
    /// <summary>Uploads the public key under <paramref name="name"/>, or returns the id of the identical key already in the project.</summary>
    Task<long> EnsureSshKeyAsync(string name, string publicKey, CancellationToken ct = default);

    // Servers
    Task<List<HetznerServer>> ListServersAsync(CancellationToken ct = default);
    Task<HetznerServer?> GetServerAsync(long serverId, CancellationToken ct = default);
    Task<string?> GetServerStatusAsync(long serverId, CancellationToken ct = default);
    /// <summary>Issue a power action: "poweron", "poweroff" (hard), "shutdown" (ACPI), "reboot", "reset".</summary>
    Task PerformActionAsync(long serverId, string action, CancellationToken ct = default);
    /// <summary>Creates the server and returns as soon as the API accepted it (status "initializing"); pair with <see cref="WaitForStatusAsync"/>.</summary>
    Task<HetznerServer> CreateServerAsync(HetznerCreateOptions options, CancellationToken ct = default);
    /// <summary>Polls until the server reports <paramref name="status"/> or the timeout passes; returns the last observed server.</summary>
    Task<HetznerServer?> WaitForStatusAsync(long serverId, string status, TimeSpan timeout, Action<string>? onProgress = null, CancellationToken ct = default);
    Task DeleteServerAsync(long serverId, CancellationToken ct = default);
}

public class HetznerCloudService : IHetznerCloudService
{
    private const string StorageKey = "hetzner.auth.credentials";
    private const string BaseUrl = "https://api.hetzner.cloud/v1";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _config;
    private readonly ISecretResolver _secrets;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public HetznerCloudService(HttpClient httpClient, IConfigurationService config, ISecretResolver secrets)
    {
        _httpClient = httpClient;
        _config = config;
        _secrets = secrets;
    }

    // ---------------------------------------------------------------- credentials

    public async Task<bool> IsAuthenticatedAsync()
    {
        var creds = await GetStoredCredentialsAsync();
        return creds != null && creds.Token.HasValue;
    }

    public async Task<HetznerStoredCredentials?> GetStoredCredentialsAsync()
    {
        var json = await _secrets.RevealAsync(StorageKey);
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<HetznerStoredCredentials>(json, SecretJson.Persistence); }
        catch (JsonException) { return null; }
    }

    public async Task StoreCredentialsAsync(HetznerStoredCredentials credentials)
    {
        // Persistence options, or the token is written as "***" (see ScalewayService).
        var json = JsonSerializer.Serialize(credentials, SecretJson.Persistence);
        await _config.SetAsync(StorageKey, json, global: true);
    }

    public Task ClearStoredCredentialsAsync() => _config.DeleteAsync(StorageKey);

    // ---------------------------------------------------------------- catalog

    public async Task<List<HetznerLocation>> ListLocationsAsync(string? token = null, CancellationToken ct = default)
    {
        token ??= await RequireTokenAsync();
        var resp = await SendAsync(HttpMethod.Get, "/locations?per_page=50", token, body: null, ct);
        await EnsureSuccessAsync(resp);
        var parsed = JsonSerializer.Deserialize<HetznerLocationListResponse>(await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        return parsed?.Locations ?? new List<HetznerLocation>();
    }

    public async Task<List<HetznerServerType>> ListServerTypesAsync(CancellationToken ct = default)
    {
        var token = await RequireTokenAsync();
        var all = new List<HetznerServerType>();
        for (var page = 1; page < 10; page++)
        {
            var resp = await SendAsync(HttpMethod.Get, $"/server_types?per_page=50&page={page}", token, body: null, ct);
            await EnsureSuccessAsync(resp);
            var parsed = JsonSerializer.Deserialize<HetznerServerTypeListResponse>(await resp.Content.ReadAsStringAsync(ct), JsonOptions);
            if (parsed == null) break;
            all.AddRange(parsed.ServerTypes);
            if (parsed.Meta?.Pagination?.NextPage is not > 0) break;
        }
        return all;
    }

    public async Task<List<HetznerImage>> ListImagesAsync(string architecture = "x86", CancellationToken ct = default)
    {
        var token = await RequireTokenAsync();
        var resp = await SendAsync(HttpMethod.Get,
            $"/images?type=system&status=available&architecture={Uri.EscapeDataString(architecture)}&per_page=50&sort=name:asc",
            token, body: null, ct);
        await EnsureSuccessAsync(resp);
        var parsed = JsonSerializer.Deserialize<HetznerImageListResponse>(await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        return parsed?.Images.Where(i => string.IsNullOrEmpty(i.Deprecated)).ToList() ?? new List<HetznerImage>();
    }

    public async Task<HetznerPricing> GetPricingAsync(CancellationToken ct = default)
    {
        var token = await RequireTokenAsync();
        var resp = await SendAsync(HttpMethod.Get, "/pricing", token, body: null, ct);
        await EnsureSuccessAsync(resp);
        var parsed = JsonSerializer.Deserialize<HetznerPricingResponse>(await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        return parsed?.Pricing ?? new HetznerPricing();
    }

    // ---------------------------------------------------------------- ssh keys

    public async Task<long> EnsureSshKeyAsync(string name, string publicKey, CancellationToken ct = default)
    {
        var token = await RequireTokenAsync();
        var key = publicKey.Trim();
        var payload = JsonSerializer.Serialize(new { name, public_key = key, labels = new Dictionary<string, string> { ["managed-by"] = "pks" } });
        var resp = await SendAsync(HttpMethod.Post, "/ssh_keys", token, payload, ct);
        if (resp.IsSuccessStatusCode)
        {
            var created = JsonSerializer.Deserialize<HetznerSshKeySingleResponse>(await resp.Content.ReadAsStringAsync(ct), JsonOptions)?.SshKey
                ?? throw new InvalidOperationException("Hetzner returned no ssh_key on create.");
            return created.Id;
        }

        // Same key material already uploaded (keys are per VM name under ~/.pks-cli/keys, so a
        // re-init of a destroyed name lands here): find it by content and reuse its id.
        var errorBody = await resp.Content.ReadAsStringAsync(ct);
        var isDuplicate = resp.StatusCode == HttpStatusCode.Conflict
            || errorBody.Contains("uniqueness_error", StringComparison.OrdinalIgnoreCase);
        if (!isDuplicate)
            throw new HttpRequestException($"Hetzner API {(int)resp.StatusCode} {resp.ReasonPhrase}: {errorBody}");

        var listResp = await SendAsync(HttpMethod.Get, "/ssh_keys?per_page=50", token, body: null, ct);
        await EnsureSuccessAsync(listResp);
        var keys = JsonSerializer.Deserialize<HetznerSshKeyListResponse>(await listResp.Content.ReadAsStringAsync(ct), JsonOptions)?.SshKeys
            ?? new List<HetznerSshKey>();
        var wanted = KeyMaterial(key);
        var match = keys.FirstOrDefault(k => KeyMaterial(k.PublicKey) == wanted)
            ?? throw new InvalidOperationException("Hetzner says the SSH key already exists, but it was not found in the project's key list.");
        return match.Id;
    }

    /// <summary>"type base64" — the comment differs between uploads of the same key.</summary>
    private static string KeyMaterial(string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey)) return string.Empty;
        var parts = publicKey.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]} {parts[1]}" : parts[0];
    }

    // ---------------------------------------------------------------- servers

    public async Task<List<HetznerServer>> ListServersAsync(CancellationToken ct = default)
    {
        var token = await RequireTokenAsync();
        var all = new List<HetznerServer>();
        for (var page = 1; page < 20; page++)
        {
            var resp = await SendAsync(HttpMethod.Get, $"/servers?per_page=50&page={page}", token, body: null, ct);
            await EnsureSuccessAsync(resp);
            var parsed = JsonSerializer.Deserialize<HetznerServerListResponse>(await resp.Content.ReadAsStringAsync(ct), JsonOptions);
            if (parsed == null) break;
            all.AddRange(parsed.Servers);
            if (parsed.Meta?.Pagination?.NextPage is not > 0) break;
        }
        return all;
    }

    public async Task<HetznerServer?> GetServerAsync(long serverId, CancellationToken ct = default)
    {
        var token = await RequireTokenAsync();
        var resp = await SendAsync(HttpMethod.Get, $"/servers/{serverId}", token, body: null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp);
        return JsonSerializer.Deserialize<HetznerServerSingleResponse>(await resp.Content.ReadAsStringAsync(ct), JsonOptions)?.Server;
    }

    public async Task<string?> GetServerStatusAsync(long serverId, CancellationToken ct = default)
        => (await GetServerAsync(serverId, ct))?.Status;

    public async Task PerformActionAsync(long serverId, string action, CancellationToken ct = default)
    {
        var token = await RequireTokenAsync();
        var resp = await SendAsync(HttpMethod.Post, $"/servers/{serverId}/actions/{action}", token, body: null, ct);
        await EnsureSuccessAsync(resp);
    }

    public async Task<HetznerServer> CreateServerAsync(HetznerCreateOptions options, CancellationToken ct = default)
    {
        var token = await RequireTokenAsync();
        var payload = JsonSerializer.Serialize(new
        {
            name = options.Name,
            server_type = options.ServerType,
            image = options.Image,
            location = options.Location,
            ssh_keys = options.SshKeyIds,
            user_data = BuildCloudInit(options),
            labels = options.Labels,
            start_after_create = true,
            public_net = new { enable_ipv4 = true, enable_ipv6 = true }
        });
        var resp = await SendAsync(HttpMethod.Post, "/servers", token, payload, ct);
        await EnsureSuccessAsync(resp);
        var created = JsonSerializer.Deserialize<HetznerServerCreateResponse>(await resp.Content.ReadAsStringAsync(ct), JsonOptions)?.Server
            ?? throw new InvalidOperationException("Hetzner returned no server on create.");
        return created;
    }

    /// <summary>
    /// The user-data the server boots with. The key goes into root's authorized_keys as well as
    /// via <c>ssh_keys</c>, and Docker installs the same way as on the Azure path so the box
    /// satisfies the devcontainer-host contract.
    /// </summary>
    internal static string BuildCloudInit(HetznerCreateOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("#cloud-config\n");
        var key = options.SshPublicKey.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            sb.Append("ssh_authorized_keys:\n");
            sb.Append($"  - {key}\n");
        }
        if (options.InstallDocker)
        {
            sb.Append("package_update: true\n");
            sb.Append("packages:\n  - curl\n  - git\n");
        }
        sb.Append("runcmd:\n");
        if (!string.IsNullOrEmpty(key))
        {
            sb.Append("  - mkdir -p /root/.ssh\n");
            sb.Append("  - chmod 700 /root/.ssh\n");
            sb.Append($"  - echo '{key}' >> /root/.ssh/authorized_keys\n");
            sb.Append("  - chmod 600 /root/.ssh/authorized_keys\n");
            sb.Append("  - sort -u -o /root/.ssh/authorized_keys /root/.ssh/authorized_keys\n");
        }
        if (options.InstallDocker)
            sb.Append("  - curl -fsSL https://get.docker.com | sh\n");
        if (options.TailscaleUpArgs.HasValue)
        {
            // The one place the auth key is read: it has to be literal text in the cloud-init document.
            sb.Append("  - curl -fsSL https://tailscale.com/install.sh | sh\n");
            sb.Append($"  - tailscale up {options.TailscaleUpArgs.Reveal()}\n");
        }
        return sb.ToString();
    }

    public async Task<HetznerServer?> WaitForStatusAsync(long serverId, string status, TimeSpan timeout, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        HetznerServer? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await GetServerAsync(serverId, ct);
            if (last != null && string.Equals(last.Status, status, StringComparison.OrdinalIgnoreCase))
                return last;
            onProgress?.Invoke($"State: {last?.Status ?? "unknown"}...");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return last;
    }

    public async Task DeleteServerAsync(long serverId, CancellationToken ct = default)
    {
        var token = await RequireTokenAsync();
        var resp = await SendAsync(HttpMethod.Delete, $"/servers/{serverId}", token, body: null, ct);
        if (resp.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(resp);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<string> RequireTokenAsync()
    {
        var creds = await GetStoredCredentialsAsync();
        if (creds == null || !creds.Token.HasValue)
            throw new InvalidOperationException("Not authenticated with Hetzner Cloud. Run 'pks hetzner init' first.");
        return creds.Token.Reveal()!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, string? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _httpClient.SendAsync(req, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        var message = body;
        try
        {
            var err = JsonSerializer.Deserialize<HetznerErrorResponse>(body, JsonOptions)?.Error;
            if (err != null) message = $"{err.Code}: {err.Message}";
        }
        catch (JsonException) { }
        throw new HttpRequestException($"Hetzner API {(int)resp.StatusCode} {resp.ReasonPhrase}: {message}");
    }
}
