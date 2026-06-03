using System.Net;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Talks to the Scaleway public API (https://api.scaleway.com) using static API-key
/// authentication (the secret key is sent as the <c>X-Auth-Token</c> header). Unlike
/// Azure there is no OAuth/token-refresh dance — the stored secret key is used directly.
/// </summary>
public interface IScalewayService
{
    // Credentials
    Task<bool> IsAuthenticatedAsync();
    Task<ScalewayStoredCredentials?> GetStoredCredentialsAsync();
    Task StoreCredentialsAsync(ScalewayStoredCredentials credentials);
    Task ClearStoredCredentialsAsync();

    // Account / IAM (used by `scaleway init`)
    Task<ScalewayApiKeyInfo?> GetApiKeyInfoAsync(string accessKey, string secretKey, CancellationToken ct = default);
    Task<ScalewayProject?> GetProjectAsync(string projectId, string secretKey, CancellationToken ct = default);
    Task<List<ScalewayProject>> ListProjectsAsync(string organizationId, string secretKey, CancellationToken ct = default);

    // Instances
    Task<List<ScalewayServer>> ListServersAsync(string zone, string? projectId = null, CancellationToken ct = default);
    Task<ScalewayServer?> GetServerAsync(string zone, string serverId, CancellationToken ct = default);
    Task<string?> GetServerStateAsync(string zone, string serverId, CancellationToken ct = default);
    Task<List<ScalewayServerType>> ListServerTypesAsync(string zone, CancellationToken ct = default);
    Task<List<ScalewayImage>> ListImagesAsync(string zone, string? arch = null, CancellationToken ct = default);

    /// <summary>Issue a power action: "poweron", "poweroff", "stop_in_place", "reboot", "terminate".</summary>
    Task PerformActionAsync(string zone, string serverId, string action, CancellationToken ct = default);

    Task<ScalewayServer> CreateServerAsync(ScalewayCreateOptions options, Action<string>? onProgress = null, CancellationToken ct = default);
    Task DeleteServerAsync(string zone, string serverId, CancellationToken ct = default);
}

public class ScalewayService : IScalewayService
{
    private const string StorageKey = "scaleway.auth.credentials";
    private const string BaseUrl = "https://api.scaleway.com";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _config;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ScalewayService(HttpClient httpClient, IConfigurationService config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    // ---------------------------------------------------------------- credentials

    public async Task<bool> IsAuthenticatedAsync()
    {
        var creds = await GetStoredCredentialsAsync();
        return creds != null && !string.IsNullOrEmpty(creds.SecretKey);
    }

    public async Task<ScalewayStoredCredentials?> GetStoredCredentialsAsync()
    {
        var json = await _config.GetAsync(StorageKey);
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<ScalewayStoredCredentials>(json); }
        catch (JsonException) { return null; }
    }

    public async Task StoreCredentialsAsync(ScalewayStoredCredentials credentials)
    {
        var json = JsonSerializer.Serialize(credentials);
        await _config.SetAsync(StorageKey, json, global: true);
    }

    public Task ClearStoredCredentialsAsync() => _config.DeleteAsync(StorageKey);

    // ---------------------------------------------------------------- account / IAM

    public async Task<ScalewayApiKeyInfo?> GetApiKeyInfoAsync(string accessKey, string secretKey, CancellationToken ct = default)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/iam/v1alpha1/api-keys/{accessKey}", secretKey, body: null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp);
        return JsonSerializer.Deserialize<ScalewayApiKeyInfo>(await resp.Content.ReadAsStringAsync(ct), JsonOptions);
    }

    public async Task<ScalewayProject?> GetProjectAsync(string projectId, string secretKey, CancellationToken ct = default)
    {
        var resp = await SendAsync(HttpMethod.Get, $"/account/v3/projects/{projectId}", secretKey, body: null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp);
        return JsonSerializer.Deserialize<ScalewayProject>(await resp.Content.ReadAsStringAsync(ct), JsonOptions);
    }

    public async Task<List<ScalewayProject>> ListProjectsAsync(string organizationId, string secretKey, CancellationToken ct = default)
    {
        var resp = await SendAsync(HttpMethod.Get,
            $"/account/v3/projects?organization_id={organizationId}&page_size=100", secretKey, body: null, ct);
        await EnsureSuccessAsync(resp);
        var parsed = JsonSerializer.Deserialize<ScalewayProjectListResponse>(
            await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        return parsed?.Projects ?? new List<ScalewayProject>();
    }

    // ---------------------------------------------------------------- instances

    public async Task<List<ScalewayServer>> ListServersAsync(string zone, string? projectId = null, CancellationToken ct = default)
    {
        var secret = await RequireSecretAsync();
        var path = $"/instance/v1/zones/{zone}/servers?per_page=100";
        if (!string.IsNullOrEmpty(projectId)) path += $"&project={projectId}";
        var resp = await SendAsync(HttpMethod.Get, path, secret, body: null, ct);
        await EnsureSuccessAsync(resp);
        var parsed = JsonSerializer.Deserialize<ScalewayServerListResponse>(
            await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        var servers = parsed?.Servers ?? new List<ScalewayServer>();
        foreach (var s in servers) s.Zone ??= zone;
        return servers;
    }

    public async Task<ScalewayServer?> GetServerAsync(string zone, string serverId, CancellationToken ct = default)
    {
        var secret = await RequireSecretAsync();
        var resp = await SendAsync(HttpMethod.Get, $"/instance/v1/zones/{zone}/servers/{serverId}", secret, body: null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(resp);
        var parsed = JsonSerializer.Deserialize<ScalewayServerSingleResponse>(
            await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        var server = parsed?.Server;
        if (server != null) server.Zone ??= zone;
        return server;
    }

    public async Task<string?> GetServerStateAsync(string zone, string serverId, CancellationToken ct = default)
    {
        var server = await GetServerAsync(zone, serverId, ct);
        return server?.State;
    }

    public async Task<List<ScalewayServerType>> ListServerTypesAsync(string zone, CancellationToken ct = default)
    {
        var secret = await RequireSecretAsync();
        var resp = await SendAsync(HttpMethod.Get, $"/instance/v1/zones/{zone}/products/servers?per_page=100", secret, body: null, ct);
        await EnsureSuccessAsync(resp);
        var parsed = JsonSerializer.Deserialize<ScalewayServerTypesResponse>(
            await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        if (parsed == null) return new List<ScalewayServerType>();
        var list = new List<ScalewayServerType>();
        foreach (var (name, type) in parsed.Servers)
        {
            type.Name = name;
            list.Add(type);
        }
        return list;
    }

    public async Task<List<ScalewayImage>> ListImagesAsync(string zone, string? arch = null, CancellationToken ct = default)
    {
        var secret = await RequireSecretAsync();
        var path = $"/instance/v1/zones/{zone}/images?per_page=100";
        if (!string.IsNullOrEmpty(arch)) path += $"&arch={arch}";
        var resp = await SendAsync(HttpMethod.Get, path, secret, body: null, ct);
        await EnsureSuccessAsync(resp);
        var parsed = JsonSerializer.Deserialize<ScalewayImageListResponse>(
            await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        return parsed?.Images ?? new List<ScalewayImage>();
    }

    public async Task PerformActionAsync(string zone, string serverId, string action, CancellationToken ct = default)
    {
        var secret = await RequireSecretAsync();
        var body = JsonSerializer.Serialize(new { action });
        var resp = await SendAsync(HttpMethod.Post, $"/instance/v1/zones/{zone}/servers/{serverId}/action", secret, body, ct);
        await EnsureSuccessAsync(resp);
    }

    public async Task<ScalewayServer> CreateServerAsync(ScalewayCreateOptions options, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var secret = await RequireSecretAsync();

        onProgress?.Invoke("Creating server...");
        var payload = JsonSerializer.Serialize(new
        {
            name = options.Name,
            commercial_type = options.CommercialType,
            image = options.Image,
            project = options.ProjectId,
            dynamic_ip_required = options.EnablePublicIp,
            tags = options.Tags
        });

        var resp = await SendAsync(HttpMethod.Post, $"/instance/v1/zones/{options.Zone}/servers", secret, payload, ct);
        await EnsureSuccessAsync(resp);
        var created = JsonSerializer.Deserialize<ScalewayServerSingleResponse>(
            await resp.Content.ReadAsStringAsync(ct), JsonOptions)?.Server
            ?? throw new InvalidOperationException("Scaleway returned no server on create.");
        created.Zone ??= options.Zone;

        // Inject our SSH public key via cloud-init user-data BEFORE first boot so the box
        // accepts our key (Scaleway has no per-create ssh-key field; cloud-init is per-VM).
        if (!string.IsNullOrWhiteSpace(options.SshPublicKey))
        {
            onProgress?.Invoke("Setting cloud-init (ssh key)...");
            var cloudInit =
                "#cloud-config\n" +
                "ssh_authorized_keys:\n" +
                $"  - {options.SshPublicKey.Trim()}\n";
            await SetCloudInitAsync(options.Zone, created.Id, cloudInit, secret, ct);
        }

        onProgress?.Invoke("Powering on...");
        await PerformActionAsync(options.Zone, created.Id, "poweron", ct);

        // Poll until running (or until an IP is assigned), best-effort up to ~5 minutes.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var current = await GetServerAsync(options.Zone, created.Id, ct);
            if (current == null) continue;
            onProgress?.Invoke($"State: {current.State}...");
            if (string.Equals(current.State, "running", StringComparison.OrdinalIgnoreCase))
                return current;
        }
        return created;
    }

    public async Task DeleteServerAsync(string zone, string serverId, CancellationToken ct = default)
    {
        var secret = await RequireSecretAsync();
        var resp = await SendAsync(HttpMethod.Delete, $"/instance/v1/zones/{zone}/servers/{serverId}", secret, body: null, ct);
        if (resp.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(resp);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<string> RequireSecretAsync()
    {
        var creds = await GetStoredCredentialsAsync();
        if (creds == null || string.IsNullOrEmpty(creds.SecretKey))
            throw new InvalidOperationException("Not authenticated with Scaleway. Run 'pks scaleway init' first.");
        return creds.SecretKey;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string secretKey, string? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.Add("X-Auth-Token", secretKey);
        if (body != null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _httpClient.SendAsync(req, ct);
    }

    /// <summary>
    /// Set the "cloud-init" user-data on a server. This endpoint takes a RAW text body
    /// (not JSON), so it bypasses <see cref="SendAsync"/>. Must be called before poweron.
    /// </summary>
    internal async Task SetCloudInitAsync(string zone, string serverId, string cloudInit, string secretKey, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch,
            $"{BaseUrl}/instance/v1/zones/{zone}/servers/{serverId}/user_data/cloud-init");
        req.Headers.Add("X-Auth-Token", secretKey);
        req.Content = new StringContent(cloudInit, Encoding.UTF8, "text/plain");
        var resp = await _httpClient.SendAsync(req, ct);
        await EnsureSuccessAsync(resp);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        throw new HttpRequestException($"Scaleway API {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
    }
}
