using System.Text.Json;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

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
    private readonly ISecretResolver _secrets;

    public TailscaleService(IConfigurationService config, ISecretResolver? secrets = null)
    {
        _config = config;
        _secrets = secrets ?? new SecretStore();
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var creds = await GetStoredCredentialsAsync();
        return creds != null && creds.AuthKey.HasValue;
    }

    public async Task<TailscaleStoredCredentials?> GetStoredCredentialsAsync()
    {
        var json = await _secrets.RevealAsync(StorageKey);
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<TailscaleStoredCredentials>(json, SecretJson.Persistence); }
        catch (JsonException) { return null; }
    }

    public async Task StoreCredentialsAsync(TailscaleStoredCredentials credentials)
    {
        // Persistence options — the default masks the auth key, and a stored "***" would silently
        // stop every future VM from joining the tailnet.
        var json = JsonSerializer.Serialize(credentials, SecretJson.Persistence);
        await _config.SetAsync(StorageKey, json, global: true);
    }

    public Task ClearStoredCredentialsAsync() => _config.DeleteAsync(StorageKey);

    /// <summary>Builds the <c>tailscale up</c> argument line. The result carries the auth key in
    /// plaintext because it has to reach a remote shell — callers may ship it over ssh or into
    /// cloud-init, and must never print or log it.</summary>
    public string BuildUpArgs(TailscaleStoredCredentials creds, string hostname)
    {
        var args = new List<string>
        {
            $"--authkey={creds.AuthKey.Reveal()}",
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
