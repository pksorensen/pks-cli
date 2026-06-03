using System.Text.Json;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

public interface ITailscaleService
{
    Task<bool> IsAuthenticatedAsync();
    Task<TailscaleStoredCredentials?> GetStoredCredentialsAsync();
    Task StoreCredentialsAsync(TailscaleStoredCredentials credentials);
    Task ClearStoredCredentialsAsync();

    /// <summary>Build the <c>tailscale up …</c> argument string for a given hostname.</summary>
    string BuildUpArgs(TailscaleStoredCredentials creds, string hostname);
}

public class TailscaleService : ITailscaleService
{
    private const string StorageKey = "tailscale.auth.credentials";
    private readonly IConfigurationService _config;

    public TailscaleService(IConfigurationService config) => _config = config;

    public async Task<bool> IsAuthenticatedAsync()
    {
        var creds = await GetStoredCredentialsAsync();
        return creds != null && !string.IsNullOrEmpty(creds.AuthKey);
    }

    public async Task<TailscaleStoredCredentials?> GetStoredCredentialsAsync()
    {
        var json = await _config.GetAsync(StorageKey);
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<TailscaleStoredCredentials>(json); }
        catch (JsonException) { return null; }
    }

    public async Task StoreCredentialsAsync(TailscaleStoredCredentials credentials)
    {
        var json = JsonSerializer.Serialize(credentials);
        await _config.SetAsync(StorageKey, json, global: true);
    }

    public Task ClearStoredCredentialsAsync() => _config.DeleteAsync(StorageKey);

    public string BuildUpArgs(TailscaleStoredCredentials creds, string hostname)
    {
        var args = new List<string>
        {
            $"--authkey={creds.AuthKey}",
            $"--hostname={Sanitize(hostname)}"
        };
        if (creds.EnableSsh) args.Add("--ssh");
        if (creds.AcceptRoutes) args.Add("--accept-routes");
        if (creds.AdvertiseExitNode) args.Add("--advertise-exit-node");
        if (!string.IsNullOrWhiteSpace(creds.LoginServer)) args.Add($"--login-server={creds.LoginServer}");
        return string.Join(' ', args);
    }

    // Tailscale hostnames must be DNS-label-safe: lowercase alphanumerics and hyphens.
    private static string Sanitize(string name)
    {
        var chars = name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }
}
